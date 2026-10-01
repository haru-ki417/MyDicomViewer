"""学習済みモデルを「確率 + Grad-CAM ヒートマップ」を同時に出力する ONNX に変換する。

ポイント:
  EfficientNet の末尾は Global Average Pooling → 全結合1層 なので、
  Grad-CAM の重み(勾配の空間平均)は全結合層の重み w_k / (H*W) に一致する。
  つまり Grad-CAM = ReLU(Σ_k w_k A_k) を正規化したもの(= CAM)になり、
  勾配計算なしの順伝播だけで求まる → ONNX のグラフに組み込める。
  C# 側は ONNX Runtime で1回推論するだけで、確率とヒートマップの両方が得られる。

使い方:
    python export_onnx.py --weights outputs/best.pt --out outputs/pneumonia.onnx
    python export_onnx.py ... --sample some.dcm   # ヒートマップ重ね合わせ画像も保存
"""
from __future__ import annotations

import argparse
from pathlib import Path

import cv2
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

from preprocess import IMG_SIZE, dicom_to_uint8, uint8_to_input
from train import build_model


class CamWrapper(nn.Module):
    def __init__(self, model: nn.Module):
        super().__init__()
        self.features = model.features
        self.pool = model.avgpool
        self.fc = model.classifier[1]  # classifier[0] は Dropout(推論時は恒等写像)

    def forward(self, x: torch.Tensor):
        feats = self.features(x)                                   # (N, C, h, w)
        logit = self.fc(torch.flatten(self.pool(feats), 1))        # (N, 1)
        cam = torch.einsum("nchw,oc->nohw", feats, self.fc.weight)  # (N, 1, h, w)
        cam = torch.relu(cam)
        cam = cam / (cam.amax(dim=(2, 3), keepdim=True) + 1e-6)
        cam = F.interpolate(cam, size=(IMG_SIZE, IMG_SIZE), mode="bilinear", align_corners=False)
        return torch.sigmoid(logit), cam


def overlay(gray: np.ndarray, cam: np.ndarray, alpha: float = 0.4) -> np.ndarray:
    heat = cv2.applyColorMap((cam * 255).astype(np.uint8), cv2.COLORMAP_JET)
    base = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)
    return cv2.addWeighted(base, 1 - alpha, heat, alpha, 0)


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--weights", type=Path, required=True)
    p.add_argument("--out", type=Path, default=Path("outputs/pneumonia.onnx"))
    p.add_argument("--sample", type=Path, help="動作確認用の DICOM ファイル")
    args = p.parse_args()

    model = build_model(pretrained=False)
    model.load_state_dict(torch.load(args.weights, map_location="cpu"))
    model.eval()
    wrapper = CamWrapper(model).eval()

    x = torch.from_numpy(uint8_to_input(dicom_to_uint8(str(args.sample)))) if args.sample \
        else torch.randn(1, 3, IMG_SIZE, IMG_SIZE)

    # 検証1: ラッパーの確率が元モデルと一致するか
    with torch.no_grad():
        ref = torch.sigmoid(model(x))
        prob, cam = wrapper(x)
    assert torch.allclose(ref, prob, atol=1e-5), "ラッパーの出力が元モデルと一致しません"

    args.out.parent.mkdir(parents=True, exist_ok=True)
    export_kwargs = dict(
        input_names=["input"], output_names=["prob", "cam"],
        dynamic_axes={"input": {0: "batch"}, "prob": {0: "batch"}, "cam": {0: "batch"}},
        opset_version=17,
    )
    try:  # PyTorch 2.5 以降は新しい exporter が既定になるため、従来方式を明示する
        torch.onnx.export(wrapper, x, str(args.out), dynamo=False, **export_kwargs)
    except TypeError:  # dynamo 引数が無い古い PyTorch
        torch.onnx.export(wrapper, x, str(args.out), **export_kwargs)

    # 検証2: ONNX Runtime の出力が PyTorch と一致するか
    import onnxruntime as ort
    sess = ort.InferenceSession(str(args.out), providers=["CPUExecutionProvider"])
    o_prob, o_cam = sess.run(None, {"input": x.numpy()})
    print(f"ONNX 出力: {args.out}")
    print(f"  prob 最大誤差: {np.abs(o_prob - prob.numpy()).max():.2e}")
    print(f"  cam  最大誤差: {np.abs(o_cam - cam.numpy()).max():.2e}")
    print(f"  確率: {float(o_prob[0, 0]):.4f}")

    if args.sample:
        gray = dicom_to_uint8(str(args.sample))
        out_png = args.out.with_name(args.sample.stem + "_cam.png")
        cv2.imwrite(str(out_png), overlay(gray, o_cam[0, 0]))
        print(f"  ヒートマップ: {out_png}")


if __name__ == "__main__":
    main()
