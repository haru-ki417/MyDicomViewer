// MyDicomViewer Web — 保存・キー操作（ファイルはブラウザーの中だけで扱い、どこにも送らない）

export function download(name, mime, bytes) {
  const blob = new Blob([bytes], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; a.rel = 'noopener';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

export function downloadDataUrl(name, dataUrl) {
  const a = document.createElement('a');
  a.href = dataUrl; a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
}

// 見本の DICOM（サイトに置いた CC0 の画像）を取ってくる
export async function fetchBytes(url) {
  const res = await fetch(url);
  if (!res.ok) return null;
  return new Uint8Array(await res.arrayBuffer());
}

let keyHandler = null;
export function listenKeys(dotnet) {
  if (keyHandler) document.removeEventListener('keydown', keyHandler);
  keyHandler = e => {
    const t = e.target;
    const typing = t && (t.tagName === 'INPUT' && t.type !== 'range' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    if (typing || e.altKey) return;
    let key = null;
    if (e.ctrlKey || e.metaKey) {
      if (e.key.toLowerCase() === 'o') key = e.shiftKey ? 'open-folder' : 'open';
    } else {
      key = { ArrowDown: 'next', PageDown: 'next', ArrowUp: 'prev', PageUp: 'prev', Escape: 'reset', m: 'measure', v: 'pan', t: 'tags' }[e.key] || null;
      if ((key === 'next' || key === 'prev') && t && t.type === 'range') key = null;
    }
    if (key) { e.preventDefault(); dotnet.invokeMethodAsync('OnKey', key); }
  };
  document.addEventListener('keydown', keyHandler);
}

export function clickElement(id) { document.getElementById(id)?.click(); }
export function isNarrow() { return window.matchMedia('(max-width: 1023px)').matches; }
export function folderPickerSupported() { return 'webkitdirectory' in document.createElement('input') && !/Android|iPhone|iPad/i.test(navigator.userAgent); }
