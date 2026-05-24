'use strict';

const { nativeImage } = require('electron');

// Build a small RGBA bitmap that draws two donut rings side-by-side.
// Electron's nativeImage.createFromBitmap consumes pre-multiplied BGRA on Windows.
// We compose with the simple "BGRA, premultiplied" convention.

const SIZE = 32; // 32x32 px
const PADDING = 2;

function clamp(x, lo, hi) {
  return Math.max(lo, Math.min(hi, x));
}

function colorForUtilization(u) {
  if (u == null || Number.isNaN(u)) return [120, 120, 120, 255]; // gray
  if (u < 0.7) return [76, 175, 80, 255]; // green
  if (u < 0.85) return [255, 152, 0, 255]; // orange
  return [244, 67, 54, 255]; // red
}

function drawDonut(pixels, cx, cy, outerR, innerR, util) {
  const trackColor = [180, 180, 180, 96];
  const fillColor = colorForUtilization(util);
  const u = util == null || Number.isNaN(util) ? 0 : clamp(util, 0, 1);
  const sweepEnd = -Math.PI / 2 + u * Math.PI * 2;
  const startAngle = -Math.PI / 2;

  for (let py = 0; py < SIZE; py++) {
    for (let px = 0; px < SIZE; px++) {
      const dx = px + 0.5 - cx;
      const dy = py + 0.5 - cy;
      const dist = Math.sqrt(dx * dx + dy * dy);
      if (dist < innerR || dist > outerR) continue;

      let angle = Math.atan2(dy, dx);
      // Normalize so that startAngle is 0 going clockwise
      let rel = angle - startAngle;
      while (rel < 0) rel += Math.PI * 2;
      while (rel >= Math.PI * 2) rel -= Math.PI * 2;
      const total = (sweepEnd - startAngle + Math.PI * 4) % (Math.PI * 2);

      const useFill = rel <= total && util != null && !Number.isNaN(util) && util > 0;
      const color = useFill ? fillColor : trackColor;

      // Anti-alias the ring edges a little
      const edgeDist = Math.min(dist - innerR, outerR - dist);
      const alphaScale = clamp(edgeDist, 0, 1);
      const a = Math.round(color[3] * alphaScale);
      writePixel(pixels, px, py, color[0], color[1], color[2], a);
    }
  }
}

function writePixel(buf, x, y, r, g, b, a) {
  const idx = (y * SIZE + x) * 4;
  // Premultiplied BGRA (Windows convention for nativeImage.createFromBitmap)
  const af = a / 255;
  buf[idx + 0] = Math.round(b * af);
  buf[idx + 1] = Math.round(g * af);
  buf[idx + 2] = Math.round(r * af);
  buf[idx + 3] = a;
}

function buildBitmap(claudeUtil, codexUtil) {
  const buf = Buffer.alloc(SIZE * SIZE * 4);
  // Left donut = Claude, right donut = Codex
  const halfW = SIZE / 2;
  const cy = SIZE / 2;
  const outerR = (halfW - PADDING) / 1.05;
  const innerR = outerR * 0.55;
  drawDonut(buf, halfW * 0.5, cy, outerR, innerR, claudeUtil);
  drawDonut(buf, halfW * 1.5, cy, outerR, innerR, codexUtil);
  return buf;
}

function buildTrayImage(claudeUtil, codexUtil) {
  const bitmap = buildBitmap(claudeUtil, codexUtil);
  const img = nativeImage.createFromBitmap(bitmap, {
    width: SIZE,
    height: SIZE,
    scaleFactor: 1,
  });
  return img;
}

module.exports = { buildTrayImage, SIZE };
