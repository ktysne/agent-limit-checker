'use strict';

const { nativeImage } = require('electron');

// Build a small RGBA bitmap that draws two donut rings side-by-side.
// Electron's nativeImage.createFromBitmap consumes pre-multiplied BGRA on Windows.
// We compose with the simple "BGRA, premultiplied" convention.

const SIZE = 32; // logical 32x32 px
const PADDING = 2;

const GLYPHS = {
  C: [
    '111',
    '100',
    '100',
    '100',
    '111',
  ],
  X: [
    '101',
    '101',
    '010',
    '101',
    '101',
  ],
  '!': [
    '1',
    '1',
    '1',
    '0',
    '1',
  ],
};

function clamp(x, lo, hi) {
  return Math.max(lo, Math.min(hi, x));
}

function colorForUtilization(u, error) {
  if (error) return [160, 160, 160, 255]; // gray base with badge
  if (u == null || Number.isNaN(u)) return [120, 120, 120, 255]; // gray
  if (u < 0.7) return [76, 175, 80, 255]; // green
  if (u < 0.85) return [255, 152, 0, 255]; // orange
  return [244, 67, 54, 255]; // red
}

function drawDonut(pixels, size, cx, cy, outerR, innerR, util, error) {
  const trackColor = [180, 180, 180, 96];
  const fillColor = colorForUtilization(util, error);
  const u = util == null || Number.isNaN(util) ? 0 : clamp(util, 0, 1);
  const startAngle = -Math.PI / 2;

  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      const dx = px + 0.5 - cx;
      const dy = py + 0.5 - cy;
      const dist = Math.sqrt(dx * dx + dy * dy);
      if (dist < innerR || dist > outerR) continue;

      let angle = Math.atan2(dy, dx);
      // Normalize so that startAngle is 0 going clockwise
      let rel = angle - startAngle;
      while (rel < 0) rel += Math.PI * 2;
      while (rel >= Math.PI * 2) rel -= Math.PI * 2;

      const useFill = u >= 1 || (rel <= u * Math.PI * 2 && util != null && !Number.isNaN(util) && util > 0);
      const color = useFill ? fillColor : trackColor;

      // Anti-alias the ring edges a little
      const edgeDist = Math.min(dist - innerR, outerR - dist);
      const alphaScale = clamp(edgeDist, 0, 1);
      const a = Math.round(color[3] * alphaScale);
      writePixel(pixels, size, px, py, color[0], color[1], color[2], a);
    }
  }
}

function writePixel(buf, size, x, y, r, g, b, a) {
  if (x < 0 || y < 0 || x >= size || y >= size) return;
  const idx = (y * size + x) * 4;
  // Premultiplied BGRA (Windows convention for nativeImage.createFromBitmap)
  const af = a / 255;
  buf[idx + 0] = Math.round(b * af);
  buf[idx + 1] = Math.round(g * af);
  buf[idx + 2] = Math.round(r * af);
  buf[idx + 3] = a;
}

function drawGlyph(buf, size, cx, cy, glyphName, color, pixelSize) {
  const glyph = GLYPHS[glyphName];
  if (!glyph) return;
  const glyphHeight = glyph.length;
  const glyphWidth = glyph[0].length;
  const left = Math.round(cx - (glyphWidth * pixelSize) / 2);
  const top = Math.round(cy - (glyphHeight * pixelSize) / 2);

  for (let row = 0; row < glyphHeight; row++) {
    for (let col = 0; col < glyphWidth; col++) {
      if (glyph[row][col] !== '1') continue;
      for (let y = 0; y < pixelSize; y++) {
        for (let x = 0; x < pixelSize; x++) {
          writePixel(buf, size, left + col * pixelSize + x, top + row * pixelSize + y, color[0], color[1], color[2], color[3]);
        }
      }
    }
  }
}

function drawFilledCircle(buf, size, cx, cy, radius, color) {
  const left = Math.floor(cx - radius);
  const right = Math.ceil(cx + radius);
  const top = Math.floor(cy - radius);
  const bottom = Math.ceil(cy + radius);
  for (let py = top; py <= bottom; py++) {
    for (let px = left; px <= right; px++) {
      const dx = px + 0.5 - cx;
      const dy = py + 0.5 - cy;
      if (dx * dx + dy * dy <= radius * radius) {
        writePixel(buf, size, px, py, color[0], color[1], color[2], color[3]);
      }
    }
  }
}

function drawErrorBadge(buf, size, cx, cy, scale) {
  drawFilledCircle(buf, size, cx, cy, 4.8 * scale, [220, 53, 69, 255]);
  drawGlyph(buf, size, cx, cy, '!', [255, 255, 255, 255], Math.max(1, Math.round(scale)));
}

function buildBitmap(claudeUtil, codexUtil, options = {}) {
  const scaleFactor = Math.max(1, Math.min(4, Number(options.scaleFactor) || 1));
  const size = Math.max(SIZE, Math.round(SIZE * scaleFactor));
  const scale = size / SIZE;
  const buf = Buffer.alloc(size * size * 4);
  // Left donut = Claude, right donut = Codex
  const halfW = size / 2;
  const cy = size / 2;
  const outerR = (halfW - PADDING * scale) / 1.05;
  const innerR = outerR * 0.55;
  const claudeCx = halfW * 0.5;
  const codexCx = halfW * 1.5;
  drawDonut(buf, size, claudeCx, cy, outerR, innerR, claudeUtil, options.claudeError);
  drawDonut(buf, size, codexCx, cy, outerR, innerR, codexUtil, options.codexError);
  drawGlyph(buf, size, claudeCx, cy, 'C', [245, 245, 245, 230], Math.max(1, Math.round(2 * scale)));
  drawGlyph(buf, size, codexCx, cy, 'X', [245, 245, 245, 230], Math.max(1, Math.round(2 * scale)));
  if (options.claudeError) drawErrorBadge(buf, size, claudeCx + 4 * scale, cy - 7 * scale, scale);
  if (options.codexError) drawErrorBadge(buf, size, codexCx + 4 * scale, cy - 7 * scale, scale);
  return { buf, size, scaleFactor };
}

function buildTrayImage(claudeUtil, codexUtil, options = {}) {
  const { buf, size, scaleFactor } = buildBitmap(claudeUtil, codexUtil, options);
  const img = nativeImage.createFromBitmap(buf, {
    width: size,
    height: size,
    scaleFactor,
  });
  return img;
}

module.exports = { buildTrayImage, SIZE };
