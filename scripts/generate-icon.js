'use strict';

const fs = require('node:fs');
const path = require('node:path');

const SIZE = 256;
const OUT = path.join(__dirname, '..', 'assets', 'app-icon.ico');

function setPixel(buf, x, y, r, g, b, a = 255) {
  if (x < 0 || y < 0 || x >= SIZE || y >= SIZE) return;
  const dibY = SIZE - 1 - y;
  const idx = (dibY * SIZE + x) * 4;
  buf[idx + 0] = b;
  buf[idx + 1] = g;
  buf[idx + 2] = r;
  buf[idx + 3] = a;
}

function drawDonut(buf, cx, cy, outerR, innerR, color) {
  for (let y = 0; y < SIZE; y++) {
    for (let x = 0; x < SIZE; x++) {
      const dx = x + 0.5 - cx;
      const dy = y + 0.5 - cy;
      const d = Math.sqrt(dx * dx + dy * dy);
      if (d >= innerR && d <= outerR) {
        setPixel(buf, x, y, color[0], color[1], color[2], 255);
      }
    }
  }
}

function drawGlyph(buf, cx, cy, rows, color, scale) {
  const left = Math.round(cx - (rows[0].length * scale) / 2);
  const top = Math.round(cy - (rows.length * scale) / 2);
  for (let row = 0; row < rows.length; row++) {
    for (let col = 0; col < rows[row].length; col++) {
      if (rows[row][col] !== '1') continue;
      for (let yy = 0; yy < scale; yy++) {
        for (let xx = 0; xx < scale; xx++) {
          setPixel(buf, left + col * scale + xx, top + row * scale + yy, color[0], color[1], color[2], color[3]);
        }
      }
    }
  }
}

function makeIco() {
  const pixels = Buffer.alloc(SIZE * SIZE * 4);
  const s = SIZE / 32;
  drawDonut(pixels, 8 * s, 16 * s, 6.5 * s, 3.6 * s, [217, 119, 6]);
  drawDonut(pixels, 24 * s, 16 * s, 6.5 * s, 3.6 * s, [16, 163, 127]);
  drawGlyph(pixels, 8 * s, 16 * s, ['111', '100', '100', '100', '111'], [255, 255, 255, 230], 2 * s);
  drawGlyph(pixels, 24 * s, 16 * s, ['101', '101', '010', '101', '101'], [255, 255, 255, 230], 2 * s);

  const dib = Buffer.alloc(40);
  dib.writeUInt32LE(40, 0); // BITMAPINFOHEADER
  dib.writeInt32LE(SIZE, 4);
  dib.writeInt32LE(SIZE * 2, 8); // XOR bitmap + AND mask
  dib.writeUInt16LE(1, 12);
  dib.writeUInt16LE(32, 14);
  dib.writeUInt32LE(0, 16);
  dib.writeUInt32LE(pixels.length, 20);

  const andMaskStride = Math.ceil(SIZE / 32) * 4;
  const andMask = Buffer.alloc(andMaskStride * SIZE);
  const image = Buffer.concat([dib, pixels, andMask]);

  const ico = Buffer.alloc(22);
  ico.writeUInt16LE(0, 0);
  ico.writeUInt16LE(1, 2);
  ico.writeUInt16LE(1, 4);
  ico.writeUInt8(SIZE >= 256 ? 0 : SIZE, 6);
  ico.writeUInt8(SIZE >= 256 ? 0 : SIZE, 7);
  ico.writeUInt8(0, 8);
  ico.writeUInt8(0, 9);
  ico.writeUInt16LE(1, 10);
  ico.writeUInt16LE(32, 12);
  ico.writeUInt32LE(image.length, 14);
  ico.writeUInt32LE(ico.length, 18);

  return Buffer.concat([ico, image]);
}

fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, makeIco());
console.log(`wrote ${path.relative(process.cwd(), OUT)}`);
