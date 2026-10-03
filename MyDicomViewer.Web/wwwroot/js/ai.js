// MyDicomViewer Web — AI（肺炎スクリーニング）をブラウザーの中で動かす。画像はどこにも送らない。
//   推論: ONNX Runtime Web（WebAssembly、CPU）。Windows 版と同じ ONNX モデル（入力 input、出力 prob・cam）
//   モデル: 学習済みの重みはサイトに含めていないので、使う人が pneumonia.onnx（と model_meta.json）を選ぶ。
//           選んだモデルは、この端末のブラウザー（IndexedDB）にだけ覚えておく。

const ORT_VERSION = '1.30.0';
const ORT_BASE = `https://cdn.jsdelivr.net/npm/onnxruntime-web@${ORT_VERSION}/dist/`;
const DB = 'mydicomviewer', STORE = 'model', KEY = 'pneumonia';

let ort = null, session = null, info = null; // info: { source, name, threshold, size }

async function loadOrt() {
  if (ort) return ort;
  ort = await import(ORT_BASE + 'ort.wasm.min.mjs');
  ort.env.wasm.wasmPaths = ORT_BASE;
  ort.env.wasm.numThreads = 1; // GitHub Pages では複数スレッドに必要な設定（COOP/COEP）ができない
  return ort;
}

function db() {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}
async function idb(mode, fn) {
  const d = await db();
  return new Promise((resolve, reject) => {
    const tx = d.transaction(STORE, mode), store = tx.objectStore(STORE);
    const req = fn(store);
    tx.oncomplete = () => { resolve(req ? req.result : undefined); d.close(); };
    tx.onerror = () => { reject(tx.error); d.close(); };
  });
}

function parseMeta(text) {
  try { const j = JSON.parse(text); return { threshold: typeof j.threshold === 'number' ? j.threshold : 0.5, name: j.model || 'unknown' }; }
  catch { return { threshold: 0.5, name: 'unknown' }; }
}

async function open(bytes, meta, source) {
  await loadOrt();
  session = await ort.InferenceSession.create(bytes, { executionProviders: ['wasm'] });
  info = { source, name: meta.name, threshold: meta.threshold, size: bytes.byteLength };
  return info;
}

// 使えるモデルを探す（この端末に覚えたもの）
export async function probe() {
  if (info) return info;
  try {
    const saved = await idb('readonly', s => s.get(KEY));
    if (saved && saved.bytes) return { source: 'saved', name: saved.meta.name, threshold: saved.meta.threshold, size: saved.bytes.byteLength, pending: true };
  } catch { /* IndexedDB が使えないブラウザー */ }
  return null;
}

async function ensure() {
  if (session) return info;
  const saved = await idb('readonly', s => s.get(KEY)).catch(() => null);
  if (saved && saved.bytes) return open(new Uint8Array(saved.bytes), saved.meta, 'saved');
  throw new Error('AI モデルがありません。pneumonia.onnx を選んでください。');
}

// 「モデルを選ぶ」で選んだファイル（.onnx と、あれば model_meta.json）
export function bindPicker(inputId, dotnet) {
  const input = document.getElementById(inputId);
  if (!input || input.dataset.bound) return;
  input.dataset.bound = '1';
  input.addEventListener('change', async () => {
    const files = [...input.files]; input.value = '';
    const onnx = files.find(f => /\.onnx$/i.test(f.name));
    const metaFile = files.find(f => /\.json$/i.test(f.name));
    if (!onnx) { dotnet.invokeMethodAsync('OnModel', null, 'ONNX のファイル（pneumonia.onnx）を選んでください。'); return; }
    try {
      const bytes = new Uint8Array(await onnx.arrayBuffer());
      const meta = metaFile ? parseMeta(await metaFile.text()) : { threshold: 0.5, name: onnx.name.replace(/\.onnx$/i, '') };
      session = null; info = null;
      const result = await open(bytes, meta, 'saved');
      try { await idb('readwrite', s => s.put({ bytes: bytes.buffer, meta }, KEY)); } catch { result.source = 'session'; }
      dotnet.invokeMethodAsync('OnModel', result, null);
    } catch (e) {
      dotnet.invokeMethodAsync('OnModel', null, 'モデルを読み込めませんでした: ' + (e && e.message ? e.message : e));
    }
  });
}

export async function forget() {
  session = null; info = null;
  try { await idb('readwrite', s => s.delete(KEY)); } catch { }
}

// 推論。tensor は 1x3x224x224 の float32（C# の PneumoniaPreprocessor が作る）。
// 返す値（float32 の並び）: 確率、閾値、推論の時間（ms）、続いて Grad-CAM（size × size）
export async function run(tensorBytes, size) {
  const meta = await ensure();
  const input = new ort.Tensor('float32', new Float32Array(tensorBytes.buffer.slice(tensorBytes.byteOffset, tensorBytes.byteOffset + tensorBytes.byteLength)), [1, 3, size, size]);
  const t0 = performance.now();
  const out = await session.run({ input });
  const ms = performance.now() - t0;
  const prob = out.prob.data[0], cam = out.cam.data;
  const result = new Float32Array(3 + cam.length);
  result[0] = prob; result[1] = meta.threshold; result[2] = ms; result.set(cam, 3);
  return new Uint8Array(result.buffer);
}

export function current() { return info; }
