"""DICOM → モデル入力 の前処理。

学習時・推論時(C# 側を含む)で完全に同じ処理をする必要があるため、
ここに一元化しておく。C# 側はこのファイルの手順をそのまま移植すること。

手順:
  1. pixel_array に RescaleSlope / RescaleIntercept を適用
  2. MONOCHROME1 の場合は白黒反転
  3. 画像全体の min-max で 0〜1 に正規化
  4. 224x224 に縮小(INTER_AREA)して 0〜255 の uint8 に
  5. (推論直前) /255 → 3ch に複製 → ImageNet の mean/std で標準化
"""
from __future__ import annotations

import cv2
import numpy as np
import pydicom

IMG_SIZE = 224
MEAN = (0.485, 0.456, 0.406)
STD = (0.229, 0.224, 0.225)


def dicom_to_uint8(path: str, size: int = IMG_SIZE) -> np.ndarray:
    """DICOM ファイルを読み、size x size の uint8 グレースケール画像を返す。"""
    ds = pydicom.dcmread(path)
    img = ds.pixel_array.astype(np.float32)
    if img.ndim != 2:
        raise ValueError(f"2次元のグレースケール画像のみ対応しています: shape={img.shape}")

    slope = float(getattr(ds, "RescaleSlope", 1.0))
    intercept = float(getattr(ds, "RescaleIntercept", 0.0))
    img = img * slope + intercept

    if getattr(ds, "PhotometricInterpretation", "") == "MONOCHROME1":
        img = img.max() - img

    lo, hi = float(img.min()), float(img.max())
    img = (img - lo) / (hi - lo) if hi > lo else np.zeros_like(img)

    img = cv2.resize(img, (size, size), interpolation=cv2.INTER_AREA)
    return np.clip(img * 255.0, 0, 255).round().astype(np.uint8)


def uint8_to_input(img: np.ndarray) -> np.ndarray:
    """uint8 (H, W) → モデル入力 float32 (1, 3, H, W)。"""
    x = img.astype(np.float32) / 255.0
    x = np.repeat(x[None, :, :], 3, axis=0)
    mean = np.array(MEAN, dtype=np.float32)[:, None, None]
    std = np.array(STD, dtype=np.float32)[:, None, None]
    return ((x - mean) / std)[None]
