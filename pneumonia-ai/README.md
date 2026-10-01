# MyDicomViewer AI — 胸部X線 肺炎疑い判定 + Grad-CAM

MyDicomViewer に組み込むための、胸部X線(DICOM)から肺炎の疑いを推定するモデルの学習・変換コードです。
推論結果の確率と、AIが注目した領域を示すヒートマップ(Grad-CAM)を同時に出力します。

> ⚠️ 本プロジェクトは研究・学習目的です。医療機器として承認されたものではなく、診断に使用することはできません。

## 構成

| ファイル | 役割 |
|---|---|
| `preprocess.py` | DICOM → モデル入力の前処理(学習・推論・C#側で共通の仕様) |
| `train.py` | RSNA Pneumonia データで EfficientNet-B0 を転移学習 |
| `export_onnx.py` | 「確率 + Grad-CAM」を同時に出す ONNX を出力し、ONNX Runtime で一致を検証 |
| `requirements.txt` | 依存パッケージ |

## データ

Kaggle の [RSNA Pneumonia Detection Challenge](https://www.kaggle.com/c/rsna-pneumonia-detection-challenge) を使用します。
画像は DICOM 形式で、`stage_2_train_labels.csv` の `Target`(1 = 肺炎の所見あり)を患者単位の二値ラベルとして扱います。
データは患者単位で train 80% / val 10% / test 10% に分割し、閾値は val で決め、最終評価は一度も学習・調整に使っていない test で行います。

## Kaggle での実行手順

1. Kaggle でコンペのルールに同意し、データを利用できる状態にする
2. 新しい Notebook を作り、右パネルで以下を設定
   - Input: `rsna-pneumonia-detection-challenge` を追加
   - Accelerator: GPU(T4 など)
   - Internet: ON(ImageNet 学習済み重みのダウンロードに必要)
3. セルで実行

```bash
!git clone https://github.com/haru-ki417/MyDicomViewer.git
%cd MyDicomViewer/pneumonia-ai
!pip install -q onnx onnxruntime

# まず1エポックで動作確認 → 問題なければ本番(既定 8 エポック)
!python train.py --epochs 1
!python train.py

# ONNX へ変換(--sample で重ね合わせ画像も保存)
!python export_onnx.py --weights /kaggle/working/outputs/best.pt \
    --out /kaggle/working/outputs/pneumonia.onnx \
    --sample /kaggle/input/rsna-pneumonia-detection-challenge/stage_2_test_images/<任意のID>.dcm
```

初回は全 DICOM(約2.7万枚)を 224×224 に変換してキャッシュするため数分かかります。2回目以降はキャッシュを使います。

出力(`/kaggle/working/outputs/`):

- `best.pt` … 検証 AUC が最良のモデル
- `pneumonia.onnx` … C# 側で使うモデル(入力 `input` (N,3,224,224) / 出力 `prob` (N,1), `cam` (N,1,224,224))
- `model_meta.json` … 判定閾値と前処理パラメータ
- `metrics.json` … 学習履歴と test セットの AUC・感度・特異度

## 設計上の工夫

**Grad-CAM を ONNX に埋め込む**
EfficientNet は「Global Average Pooling → 全結合1層」で終わる構造のため、Grad-CAM の重み(勾配の空間平均)は全結合層の重みに比例します。
そのため Grad-CAM は勾配計算なしに順伝播だけで計算でき(CAM と同値)、ONNX のグラフの中に組み込めます。
C# 側では ONNX Runtime で1回推論するだけで、確率とヒートマップの両方が得られます。

**前処理の一元化**
学習と推論で前処理が少しでも違うと精度が大きく落ちるため、`preprocess.py` に仕様を集約し、C# 側はこの手順をそのまま移植します。

**評価の誠実さ**
クラス不均衡(陽性 約2割)は損失関数の `pos_weight` で補正し、閾値は検証セットで決めたものをテストセットにそのまま適用しています。

## 今後

- [ ] C#(.NET)の MyDicomViewer に ONNX Runtime で推論機能を追加
- [ ] ヒートマップの重ね合わせ表示と透明度スライダー
- [ ] 結果画面に「診断用ではない」旨を常時表示
