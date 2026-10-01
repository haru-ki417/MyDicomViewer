"""RSNA Pneumonia Detection Challenge のデータで「肺炎の疑いあり/なし」の二値分類器を学習する。

Kaggle Notebook (GPU, Internet ON) での実行を想定:
    !python train.py --data-dir /kaggle/input/rsna-pneumonia-detection-challenge

出力 (--out-dir, 既定 /kaggle/working/outputs):
    best.pt          検証 AUC が最も高かったモデルの state_dict
    model_meta.json  閾値・前処理パラメータ(C# 側で使う)
    metrics.json     エポックごとの履歴とテストセットの最終評価
"""
from __future__ import annotations

import argparse
import json
import random
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
import pandas as pd
import torch
import torch.nn as nn
from sklearn.metrics import roc_auc_score, roc_curve
from sklearn.model_selection import train_test_split
from torch.utils.data import DataLoader, Dataset
from torchvision import models, transforms

from preprocess import IMG_SIZE, MEAN, STD, dicom_to_uint8


# ---------------------------------------------------------------- data
def load_labels(data_dir: Path) -> pd.DataFrame:
    """1患者に複数のバウンディングボックス行があるので、患者単位にまとめる。"""
    df = pd.read_csv(data_dir / "stage_2_train_labels.csv")
    df = df.groupby("patientId", as_index=False)["Target"].max()
    return df.sort_values("patientId").reset_index(drop=True)


def build_cache(df: pd.DataFrame, img_dir: Path, cache_dir: Path, workers: int) -> np.ndarray:
    """全 DICOM を一度だけ前処理して .npy にキャッシュする(毎エポック DICOM を読むと遅いため)。"""
    cache_dir.mkdir(parents=True, exist_ok=True)
    img_path, id_path = cache_dir / "images.npy", cache_dir / "ids.npy"
    ids = df["patientId"].to_numpy()
    if img_path.exists() and id_path.exists() and np.array_equal(np.load(id_path, allow_pickle=True), ids):
        print(f"キャッシュを使用: {img_path}")
        return np.load(img_path, mmap_mode="r")

    print(f"{len(ids)} 枚の DICOM を前処理中...")
    t0 = time.time()
    paths = [str(img_dir / f"{pid}.dcm") for pid in ids]
    with ProcessPoolExecutor(max_workers=workers) as ex:
        images = np.stack(list(ex.map(dicom_to_uint8, paths, chunksize=64)))
    np.save(img_path, images)
    np.save(id_path, ids)
    print(f"前処理完了 ({time.time() - t0:.0f} 秒)")
    return images


class CXRDataset(Dataset):
    def __init__(self, images: np.ndarray, indices: np.ndarray, labels: np.ndarray, train: bool):
        self.images, self.indices, self.labels = images, indices, labels
        aug = [
            transforms.RandomAffine(degrees=7, translate=(0.05, 0.05), scale=(0.9, 1.1)),
            transforms.ColorJitter(brightness=0.15, contrast=0.15),
        ] if train else []
        self.aug = transforms.Compose(aug)
        self.norm = transforms.Normalize(MEAN, STD)

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, i: int):
        img = torch.from_numpy(np.array(self.images[self.indices[i]])).float().div(255).unsqueeze(0)
        img = self.aug(img).repeat(3, 1, 1)
        return self.norm(img), torch.tensor(self.labels[i], dtype=torch.float32)


# ---------------------------------------------------------------- model
def build_model(pretrained: bool = True) -> nn.Module:
    weights = models.EfficientNet_B0_Weights.IMAGENET1K_V1 if pretrained else None
    model = models.efficientnet_b0(weights=weights)
    model.classifier[1] = nn.Linear(model.classifier[1].in_features, 1)
    return model


@torch.no_grad()
def predict(model: nn.Module, loader: DataLoader, device: torch.device) -> tuple[np.ndarray, np.ndarray]:
    model.eval()
    probs, labels = [], []
    for x, y in loader:
        logits = model(x.to(device, non_blocking=True)).squeeze(1)
        probs.append(torch.sigmoid(logits.float()).cpu().numpy())
        labels.append(y.numpy())
    return np.concatenate(probs), np.concatenate(labels)


def youden_threshold(labels: np.ndarray, probs: np.ndarray) -> float:
    """感度 + 特異度 - 1 が最大になる閾値(検証セットで決め、テストには流用のみ)。"""
    fpr, tpr, thr = roc_curve(labels, probs)
    return float(thr[np.argmax(tpr - fpr)])


def binary_metrics(labels: np.ndarray, probs: np.ndarray, threshold: float) -> dict:
    pred = probs >= threshold
    pos, neg = labels == 1, labels == 0
    return {
        "auc": float(roc_auc_score(labels, probs)),
        "sensitivity": float((pred & pos).sum() / max(pos.sum(), 1)),
        "specificity": float((~pred & neg).sum() / max(neg.sum(), 1)),
        "accuracy": float((pred == pos).mean()),
        "threshold": threshold,
        "n": int(len(labels)),
    }


# ---------------------------------------------------------------- main
def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--data-dir", type=Path, default=Path("/kaggle/input/rsna-pneumonia-detection-challenge"))
    p.add_argument("--out-dir", type=Path, default=Path("/kaggle/working/outputs"))
    p.add_argument("--cache-dir", type=Path, default=Path("/kaggle/working/cache"))
    p.add_argument("--epochs", type=int, default=8)
    p.add_argument("--batch-size", type=int, default=64)
    p.add_argument("--lr", type=float, default=3e-4)
    p.add_argument("--weight-decay", type=float, default=1e-4)
    p.add_argument("--workers", type=int, default=4)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--no-pretrained", action="store_true", help="ImageNet 重みを使わない(動作確認用)")
    args = p.parse_args()

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    use_amp = device.type == "cuda"
    print(f"device: {device}")

    # データ準備: 患者単位で train 80% / val 10% / test 10%(陽性率を揃えて分割)
    df = load_labels(args.data_dir)
    images = build_cache(df, args.data_dir / "stage_2_train_images", args.cache_dir, args.workers)
    idx = np.arange(len(df))
    y = df["Target"].to_numpy()
    tr_idx, tmp_idx = train_test_split(idx, test_size=0.2, stratify=y, random_state=args.seed)
    va_idx, te_idx = train_test_split(tmp_idx, test_size=0.5, stratify=y[tmp_idx], random_state=args.seed)
    print(f"train {len(tr_idx)} / val {len(va_idx)} / test {len(te_idx)}  陽性率 {y.mean():.1%}")

    def loader(ix: np.ndarray, train: bool) -> DataLoader:
        return DataLoader(CXRDataset(images, ix, y[ix], train), batch_size=args.batch_size,
                          shuffle=train, num_workers=args.workers, pin_memory=use_amp, drop_last=train)

    train_dl, val_dl, test_dl = loader(tr_idx, True), loader(va_idx, False), loader(te_idx, False)

    model = build_model(pretrained=not args.no_pretrained).to(device)
    pos = y[tr_idx].sum()
    pos_weight = torch.tensor([(len(tr_idx) - pos) / max(pos, 1)], device=device)  # クラス不均衡の補正
    criterion = nn.BCEWithLogitsLoss(pos_weight=pos_weight)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs)
    scaler = torch.amp.GradScaler("cuda", enabled=use_amp)

    args.out_dir.mkdir(parents=True, exist_ok=True)
    best_auc, history = -1.0, []
    for epoch in range(1, args.epochs + 1):
        model.train()
        t0, total, count = time.time(), 0.0, 0
        for x, t in train_dl:
            x, t = x.to(device, non_blocking=True), t.to(device, non_blocking=True)
            optimizer.zero_grad(set_to_none=True)
            with torch.autocast(device_type=device.type, enabled=use_amp):
                loss = criterion(model(x).squeeze(1), t)
            scaler.scale(loss).backward()
            scaler.step(optimizer)
            scaler.update()
            total += loss.item() * len(x)
            count += len(x)
        scheduler.step()

        probs, labels = predict(model, val_dl, device)
        val_auc = float(roc_auc_score(labels, probs))
        history.append({"epoch": epoch, "train_loss": total / max(count, 1), "val_auc": val_auc})
        print(f"epoch {epoch}/{args.epochs}  loss {total / max(count, 1):.4f}  val AUC {val_auc:.4f}  ({time.time() - t0:.0f}s)")
        if val_auc > best_auc:
            best_auc = val_auc
            torch.save(model.state_dict(), args.out_dir / "best.pt")

    # 最良モデルで閾値を決め(val)、最終評価(test)
    model.load_state_dict(torch.load(args.out_dir / "best.pt", map_location=device))
    val_probs, val_labels = predict(model, val_dl, device)
    threshold = youden_threshold(val_labels, val_probs)
    test_probs, test_labels = predict(model, test_dl, device)
    result = {
        "val": binary_metrics(val_labels, val_probs, threshold),
        "test": binary_metrics(test_labels, test_probs, threshold),
        "history": history,
    }
    meta = {
        "model": "efficientnet_b0",
        "task": "pneumonia (RSNA Target=1) vs. no pneumonia",
        "input": {"size": IMG_SIZE, "channels": 3, "mean": MEAN, "std": STD,
                  "preprocessing": "see preprocess.py: rescale -> MONOCHROME1 invert -> min-max -> resize(INTER_AREA)"},
        "outputs": {"prob": "sigmoid probability, shape (N,1)", "cam": "Grad-CAM in [0,1], shape (N,1,H,W)"},
        "threshold": threshold,
        "disclaimer": "研究・学習目的。診断には使用不可。",
    }
    (args.out_dir / "metrics.json").write_text(json.dumps(result, indent=2, ensure_ascii=False))
    (args.out_dir / "model_meta.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False))
    print(json.dumps({k: result[k] for k in ("val", "test")}, indent=2))


if __name__ == "__main__":
    main()
