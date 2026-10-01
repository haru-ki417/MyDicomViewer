"""書き出した ONNX モデルを、学習に使っていないテストデータで評価する。

評価すること:
  1. 分類性能: AUC（ブートストラップ法による 95% 信頼区間つき）、感度・特異度・混同行列
     → ONNX 変換後も学習時（PyTorch）と同じ性能が出ているかの確認も兼ねる
  2. 位置の妥当性: Grad-CAM が、放射線科医が付けた病変の枠（RSNA のバウンディングボックス）を
     指しているか
       - Pointing Game: ヒートマップの最大点が病変の枠の中にある割合
       - Energy Ratio : ヒートマップの総量のうち、病変の枠の中にある割合
     どちらも「枠の面積の割合（ランダムに選んだ場合の期待値）」と比べる

テストデータの分け方は train.py と完全に同じ（患者単位・同じ乱数シード）。

使い方 (Kaggle):
    python evaluate.py --data-dir <コンペのデータ> --model-dir <pneumonia.onnx のあるフォルダ>
"""
from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
import pandas as pd
import pydicom
from sklearn.metrics import confusion_matrix, roc_auc_score, roc_curve
from sklearn.model_selection import train_test_split

from preprocess import IMG_SIZE, dicom_to_uint8, uint8_to_input


def test_split(data_dir: Path, seed: int) -> tuple[pd.DataFrame, pd.DataFrame]:
    """train.py と同じ手順でテスト用の患者を選ぶ。戻り値: (テスト患者, 病変の枠)"""
    raw = pd.read_csv(data_dir / "stage_2_train_labels.csv")
    df = raw.groupby("patientId", as_index=False)["Target"].max().sort_values("patientId").reset_index(drop=True)
    idx = np.arange(len(df))
    y = df["Target"].to_numpy()
    _, tmp_idx = train_test_split(idx, test_size=0.2, stratify=y, random_state=seed)
    _, te_idx = train_test_split(tmp_idx, test_size=0.5, stratify=y[tmp_idx], random_state=seed)
    test = df.iloc[te_idx].reset_index(drop=True)
    boxes = raw[(raw["Target"] == 1) & raw["patientId"].isin(test["patientId"])]
    return test, boxes


def predict(session: ort.InferenceSession, paths: list[str], batch_size: int) -> tuple[np.ndarray, np.ndarray]:
    probs, cams = [], []
    for start in range(0, len(paths), batch_size):
        batch = np.concatenate([uint8_to_input(dicom_to_uint8(p)) for p in paths[start:start + batch_size]])
        prob, cam = session.run(["prob", "cam"], {"input": batch})
        probs.append(prob[:, 0])
        cams.append(cam[:, 0])
        if (start // batch_size) % 10 == 0:
            print(f"  {min(start + batch_size, len(paths))} / {len(paths)}")
    return np.concatenate(probs), np.concatenate(cams)


def bootstrap_auc(labels: np.ndarray, probs: np.ndarray, n: int, seed: int) -> tuple[float, float]:
    rng = np.random.default_rng(seed)
    scores = []
    for _ in range(n):
        i = rng.integers(0, len(labels), len(labels))
        if labels[i].min() != labels[i].max():  # 片方のクラスしかない標本は AUC が定義できない
            scores.append(roc_auc_score(labels[i], probs[i]))
    return float(np.percentile(scores, 2.5)), float(np.percentile(scores, 97.5))


def localization(cam: np.ndarray, patient_boxes: pd.DataFrame, width: int, height: int) -> dict:
    """1枚分の位置評価。枠は元画像の画素座標なので、CAM（224x224）の座標に縮める。"""
    mask = np.zeros_like(cam, dtype=bool)
    sx, sy = cam.shape[1] / width, cam.shape[0] / height
    for _, b in patient_boxes.iterrows():
        x0, y0 = int(np.floor(b.x * sx)), int(np.floor(b.y * sy))
        x1, y1 = int(np.ceil((b.x + b.width) * sx)), int(np.ceil((b.y + b.height) * sy))
        mask[max(y0, 0):y1, max(x0, 0):x1] = True

    peak_y, peak_x = np.unravel_index(np.argmax(cam), cam.shape)
    total = float(cam.sum())
    return {
        "hit": bool(mask[peak_y, peak_x]),
        "energy": float(cam[mask].sum() / total) if total > 0 else 0.0,
        "area": float(mask.mean()),
    }


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--data-dir", type=Path, required=True)
    p.add_argument("--model-dir", type=Path, required=True)
    p.add_argument("--out-dir", type=Path, default=Path("outputs"))
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--batch-size", type=int, default=32)
    p.add_argument("--bootstrap", type=int, default=1000)
    args = p.parse_args()

    meta = json.loads((args.model_dir / "model_meta.json").read_text(encoding="utf-8"))
    threshold = float(meta["threshold"])
    session = ort.InferenceSession(str(args.model_dir / "pneumonia.onnx"), providers=["CPUExecutionProvider"])

    test, boxes = test_split(args.data_dir, args.seed)
    img_dir = args.data_dir / "stage_2_train_images"
    paths = [str(img_dir / f"{pid}.dcm") for pid in test["patientId"]]
    labels = test["Target"].to_numpy()
    print(f"テスト: {len(test)} 人（陽性 {labels.sum()} 人）。推論中...")

    t0 = time.time()
    probs, cams = predict(session, paths, args.batch_size)
    elapsed = time.time() - t0

    # ---- 1. 分類性能
    pred = probs >= threshold
    tn, fp, fn, tp = confusion_matrix(labels, pred, labels=[0, 1]).ravel()
    auc = float(roc_auc_score(labels, probs))
    ci_low, ci_high = bootstrap_auc(labels, probs, args.bootstrap, args.seed)
    classification = {
        "auc": auc, "auc_95ci": [ci_low, ci_high],
        "sensitivity": tp / (tp + fn), "specificity": tn / (tn + fp),
        "ppv": tp / (tp + fp) if tp + fp else None, "npv": tn / (tn + fn) if tn + fn else None,
        "confusion_matrix": {"tp": int(tp), "fp": int(fp), "tn": int(tn), "fn": int(fn)},
        "threshold": threshold, "n": int(len(labels)), "positives": int(labels.sum()),
        "seconds_per_image_cpu": elapsed / len(labels),
    }

    # ---- 2. 位置の妥当性（陽性かつ枠のある画像のみ）
    results = []
    for i, pid in enumerate(test["patientId"]):
        if labels[i] != 1:
            continue
        patient_boxes = boxes[boxes["patientId"] == pid]
        if patient_boxes.empty:
            continue
        header = pydicom.dcmread(paths[i], stop_before_pixels=True)
        results.append(localization(cams[i], patient_boxes, int(header.Columns), int(header.Rows)))

    hits = np.array([r["hit"] for r in results])
    energy = np.array([r["energy"] for r in results])
    area = np.array([r["area"] for r in results])
    localization_summary = {
        "n": len(results),
        "pointing_game": float(hits.mean()),
        "energy_ratio": float(energy.mean()),
        "random_baseline_box_area": float(area.mean()),
    }

    # ---- 保存
    args.out_dir.mkdir(parents=True, exist_ok=True)
    report = {"model": meta.get("model", "unknown"), "classification": classification, "localization": localization_summary}
    (args.out_dir / "evaluation.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    fpr, tpr, _ = roc_curve(labels, probs)
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, ax = plt.subplots(figsize=(5, 5), dpi=150)
        ax.plot(fpr, tpr, color="#7B4FA6", lw=2, label=f"AUC {auc:.3f} (95% CI {ci_low:.3f}–{ci_high:.3f})")
        ax.plot([0, 1], [0, 1], color="#999999", lw=1, ls="--")
        ax.scatter([1 - classification["specificity"]], [classification["sensitivity"]], color="#D9534F", zorder=3,
                   label=f"Operating point (threshold {threshold:.3f})")
        ax.set_xlabel("1 - Specificity")
        ax.set_ylabel("Sensitivity")
        ax.set_title("ROC curve (held-out test set)")
        ax.legend(loc="lower right", fontsize=8)
        fig.tight_layout()
        fig.savefig(args.out_dir / "roc_curve.png")
    except ImportError:
        print("matplotlib が無いため ROC 曲線の画像は省略しました")

    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
