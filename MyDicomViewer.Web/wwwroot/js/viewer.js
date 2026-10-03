// MyDicomViewer Web — 画像の表示（拡大・移動・計測・ヒートマップ）。画像の値と濃度は C# が作り、ここでは描くことと操作を伝える。
//   ホイール: スライス送り（1 枚だけのときは拡大）  Ctrl + ホイール・2 本の指: カーソル位置を中心に拡大
//   左ドラッグ・1 本の指: 移動（計測の道具では距離の計測）  右ドラッグ: 濃度（横: 幅 / 縦: 中心）  ダブルクリック: 全体表示

const views = new Map();

class Viewer {
  constructor(host, dotnet) {
    this.host = host; this.dotnet = dotnet;
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'view-canvas';
    this.canvas.tabIndex = 0;
    host.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d');
    this.img = null; this.iw = 0; this.ih = 0;
    this.heat = null; this.heatOpacity = 0.6; this.heatVisible = false;
    this.scale = 1; this.fitScale = 1; this.ox = 0; this.oy = 0; this.userMoved = false;
    this.tool = 'pan'; this.slices = 1; this.spacing = null;
    this.measures = []; this.drag = null; this.pointers = new Map(); this.lastTap = 0;
    new ResizeObserver(() => this.resize()).observe(host);
    this.bind(); this.resize();
  }

  async setImage(w, h, bytes, channels, keepView) {
    const n = w * h, px = new Uint8ClampedArray(n * 4);
    if (channels === 3) for (let i = 0, j = 0, k = 0; i < n; i++, j += 4, k += 3) { px[j] = bytes[k]; px[j + 1] = bytes[k + 1]; px[j + 2] = bytes[k + 2]; px[j + 3] = 255; }
    else for (let i = 0, j = 0; i < n; i++, j += 4) { const v = bytes[i]; px[j] = v; px[j + 1] = v; px[j + 2] = v; px[j + 3] = 255; }
    const seq = this.seq = (this.seq || 0) + 1;
    const bmp = await createImageBitmap(new ImageData(px, w, h));
    if (seq !== this.seq) { bmp.close(); return; }
    const changed = w !== this.iw || h !== this.ih;
    if (this.img && this.img.close) this.img.close();
    this.img = bmp; this.iw = w; this.ih = h;
    if (changed || !keepView) { this.measures = []; this.fit(); } else this.draw();
  }
  clear() { this.img = null; this.iw = this.ih = 0; this.heat = null; this.measures = []; this.draw(); }

  async setHeatmap(w, h, rgba, opacity, visible) {
    this.heatOpacity = opacity; this.heatVisible = visible;
    if (rgba && rgba.length) {
      const bmp = await createImageBitmap(new ImageData(new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, w * h * 4), w, h));
      if (this.heat && this.heat.close) this.heat.close();
      this.heat = bmp;
    } else if (rgba !== undefined && rgba !== null && rgba.length === 0) {
      this.heat = null;
    }
    this.draw();
  }
  setHeatStyle(opacity, visible) { this.heatOpacity = opacity; this.heatVisible = visible; this.draw(); }
  setTool(t) { this.tool = t; this.canvas.style.cursor = t === 'measure' ? 'crosshair' : 'grab'; }
  setInfo(slices, spacing) { this.slices = slices; this.spacing = spacing; this.measures.forEach(m => m.label = this.label(m)); this.draw(); }
  clearMeasures() { this.measures = []; this.draw(); this.report(); }

  resize() {
    const r = this.host.getBoundingClientRect();
    this.dpr = window.devicePixelRatio || 1;
    this.cw = Math.max(1, r.width); this.ch = Math.max(1, r.height);
    this.canvas.width = Math.round(this.cw * this.dpr); this.canvas.height = Math.round(this.ch * this.dpr);
    this.canvas.style.width = this.cw + 'px'; this.canvas.style.height = this.ch + 'px';
    if (!this.userMoved) this.fit(); else this.draw();
  }
  fit() {
    if (!this.iw) { this.draw(); return; }
    this.fitScale = Math.max(0.01, Math.min(this.cw / this.iw, this.ch / this.ih));
    this.scale = this.fitScale;
    this.ox = (this.cw - this.iw * this.scale) / 2; this.oy = (this.ch - this.ih * this.scale) / 2;
    this.userMoved = false; this.draw(); this.report();
  }
  report() { this.dotnet.invokeMethodAsync('OnView', Math.round(this.scale / this.fitScale * 100), this.measures.length); }
  toImg(x, y) { return { x: (x - this.ox) / this.scale, y: (y - this.oy) / this.scale }; }
  toScr(x, y) { return { x: x * this.scale + this.ox, y: y * this.scale + this.oy }; }
  draw() { if (!this.raf) this.raf = requestAnimationFrame(() => { this.raf = 0; this.render(); }); }

  render() {
    const c = this.ctx, d = this.dpr;
    c.setTransform(d, 0, 0, d, 0, 0);
    c.fillStyle = '#000'; c.fillRect(0, 0, this.cw, this.ch);
    if (!this.img) return;
    c.imageSmoothingEnabled = this.scale < 2.5;
    c.drawImage(this.img, this.ox, this.oy, this.iw * this.scale, this.ih * this.scale);
    if (this.heat && this.heatVisible) {
      c.globalAlpha = this.heatOpacity; c.imageSmoothingEnabled = true;
      c.drawImage(this.heat, this.ox, this.oy, this.iw * this.scale, this.ih * this.scale);
      c.globalAlpha = 1;
    }
    for (const m of this.measures) this.drawMeasure(m);
  }
  drawMeasure(m) {
    const c = this.ctx, a = this.toScr(m.x1, m.y1), b = this.toScr(m.x2, m.y2);
    c.strokeStyle = '#F2C14E'; c.fillStyle = '#F2C14E'; c.lineWidth = 2;
    c.beginPath(); c.moveTo(a.x, a.y); c.lineTo(b.x, b.y); c.stroke();
    for (const p of [a, b]) { c.beginPath(); c.arc(p.x, p.y, 3.5, 0, 7); c.fill(); }
    if (m.label) {
      c.font = '600 13px "Bahnschrift","Segoe UI",system-ui,sans-serif';
      const tx = (a.x + b.x) / 2 + 10, ty = (a.y + b.y) / 2 - 10, w = c.measureText(m.label).width;
      c.fillStyle = 'rgba(0,0,0,.65)'; c.fillRect(tx - 5, ty - 15, w + 10, 21);
      c.fillStyle = '#F2C14E'; c.fillText(m.label, tx, ty);
    }
  }
  // 計測の値（Windows 版の PixelSpacing.Distance と同じ式）。画素間隔がなければ画素数
  label(m) {
    const dx = m.x2 - m.x1, dy = m.y2 - m.y1, s = this.spacing;
    if (!s) return `${Math.hypot(dx, dy).toFixed(0)} px`;
    const mm = Math.hypot(dx * s.column, dy * s.row);
    return `${mm.toFixed(1)} mm${s.imager ? ' *' : ''}`;
  }

  bind() {
    const cv = this.canvas;
    cv.addEventListener('wheel', e => {
      if (!this.img) return;
      e.preventDefault();
      const r = cv.getBoundingClientRect();
      if (e.ctrlKey || e.metaKey || this.slices <= 1) { this.zoomAt(e.clientX - r.left, e.clientY - r.top, Math.pow(1.0018, -e.deltaY)); return; }
      this.wheelAcc = (this.wheelAcc || 0) + e.deltaY;
      const steps = Math.trunc(this.wheelAcc / 60);
      if (steps !== 0) { this.wheelAcc -= steps * 60; this.dotnet.invokeMethodAsync('OnScroll', steps); }
    }, { passive: false });
    cv.addEventListener('pointerdown', e => this.down(e));
    cv.addEventListener('pointermove', e => this.move(e));
    cv.addEventListener('pointerup', e => this.up(e));
    cv.addEventListener('pointercancel', e => this.up(e));
    cv.addEventListener('dblclick', () => this.fit());
    cv.addEventListener('contextmenu', e => e.preventDefault());
  }
  pos(e) { const r = this.canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }
  zoomAt(x, y, f) {
    const next = Math.min(this.fitScale * 40, Math.max(this.fitScale * 0.1, this.scale * f)); f = next / this.scale;
    this.ox = x - (x - this.ox) * f; this.oy = y - (y - this.oy) * f; this.scale = next; this.userMoved = true; this.draw(); this.report();
  }
  clampImg(p) { return { x: Math.min(this.iw, Math.max(0, p.x)), y: Math.min(this.ih, Math.max(0, p.y)) }; }

  down(e) {
    if (!this.img) return;
    this.canvas.focus(); this.canvas.setPointerCapture(e.pointerId);
    const p = this.pos(e); this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 2) {
      if (this.drag && this.drag.kind === 'measure') this.measures.pop();
      const [a, b] = [...this.pointers.values()];
      this.drag = { kind: 'pinch', dist: Math.hypot(a.x - b.x, a.y - b.y), mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
      return;
    }
    if (this.pointers.size > 2) return;
    if (e.pointerType === 'touch') {
      const now = performance.now();
      if (now - this.lastTap < 300) { this.fit(); this.lastTap = 0; return; }
      this.lastTap = now;
    }
    if (e.button === 2) { this.drag = { kind: 'window', start: p }; return; }
    if (this.tool === 'measure' && e.button === 0) {
      const ip = this.clampImg(this.toImg(p.x, p.y));
      const m = { x1: ip.x, y1: ip.y, x2: ip.x, y2: ip.y, label: '' };
      this.measures.push(m); this.drag = { kind: 'measure', m }; this.draw(); return;
    }
    this.drag = { kind: 'pan', start: p, ox: this.ox, oy: this.oy };
    this.canvas.style.cursor = 'grabbing';
  }
  move(e) {
    const p = this.pos(e);
    if (this.pointers.has(e.pointerId)) this.pointers.set(e.pointerId, p);
    const d = this.drag; if (!d) return;
    if (d.kind === 'pinch' && this.pointers.size >= 2) {
      const [a, b] = [...this.pointers.values()];
      const dist = Math.hypot(a.x - b.x, a.y - b.y), mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      this.ox += mid.x - d.mid.x; this.oy += mid.y - d.mid.y; this.userMoved = true;
      this.zoomAt(mid.x, mid.y, dist / Math.max(d.dist, 1)); d.dist = dist; d.mid = mid; return;
    }
    if (d.kind === 'pan') { this.ox = d.ox + p.x - d.start.x; this.oy = d.oy + p.y - d.start.y; this.userMoved = true; this.draw(); return; }
    if (d.kind === 'window') { const dx = p.x - d.start.x, dy = p.y - d.start.y; d.start = p; this.dotnet.invokeMethodAsync('OnWindow', dx, dy); return; }
    if (d.kind === 'measure') { const ip = this.clampImg(this.toImg(p.x, p.y)); d.m.x2 = ip.x; d.m.y2 = ip.y; d.m.label = this.label(d.m); this.draw(); }
  }
  up(e) {
    this.pointers.delete(e.pointerId);
    const d = this.drag; if (!d) return;
    if (d.kind === 'pinch') { if (this.pointers.size === 0) this.drag = null; return; }
    this.drag = null;
    this.canvas.style.cursor = this.tool === 'measure' ? 'crosshair' : 'grab';
    // クリックしただけ（ほとんど動かしていない）の線は残さない
    if (d.kind === 'measure' && Math.hypot(d.m.x2 - d.m.x1, d.m.y2 - d.m.y1) < 2) this.measures.pop();
    this.draw(); this.report();
  }
  snapshot() { return this.canvas.toDataURL('image/png'); }
}

export function create(id, dotnet) { views.set(id, new Viewer(document.getElementById(id), dotnet)); }
export function setImage(id, w, h, bytes, channels, keepView) { return views.get(id)?.setImage(w, h, bytes, channels, keepView); }
export function setHeatmap(id, w, h, rgba, opacity, visible) { return views.get(id)?.setHeatmap(w, h, rgba, opacity, visible); }
export function setHeatStyle(id, opacity, visible) { views.get(id)?.setHeatStyle(opacity, visible); }
export function setTool(id, t) { views.get(id)?.setTool(t); }
export function setInfo(id, slices, spacing) { views.get(id)?.setInfo(slices, spacing); }
export function clearMeasures(id) { views.get(id)?.clearMeasures(); }
export function clear(id) { views.get(id)?.clear(); }
export function fit(id) { views.get(id)?.fit(); }
export function snapshot(id) { return views.get(id)?.snapshot(); }
export function dispose(id) { views.delete(id); }
