"use strict";

// Generates the monochrome macOS menu-bar Template icons - a small 5-bar audio-waveform glyph - as
// build/trayTemplate.png (18x18) and build/trayTemplate@2x.png (36x36). Template images are black-on-
// transparent; macOS recolours them for the light/dark menu bar automatically.
//
// Run: node build/make-tray-icon.js   (from apps/desktop). The committed PNGs are the source of truth;
// this script just regenerates them if the glyph ever changes. No third-party deps.

const zlib = require("node:zlib");
const fs = require("node:fs");
const path = require("node:path");

function crc32(buf) {
  let c = ~0;
  for (let i = 0; i < buf.length; i++) {
    c ^= buf[i];
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1));
  }
  return (~c) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}

function pngFromRGBA(size, rgba) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // colour type RGBA
  const stride = size * 4;
  const raw = Buffer.alloc((stride + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (stride + 1)] = 0; // filter: none
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, y * stride + stride);
  }
  const idat = zlib.deflateSync(raw, { level: 9 });
  return Buffer.concat([sig, chunk("IHDR", ihdr), chunk("IDAT", idat), chunk("IEND", Buffer.alloc(0))]);
}

// A symmetric 5-bar equalizer/waveform, centred vertically. Bars and gaps are equal width.
function waveform(size) {
  const rgba = Buffer.alloc(size * size * 4); // transparent
  const mx = size * 0.12; // horizontal margin
  const heights = [0.3, 0.55, 0.85, 0.55, 0.3];
  const slot = (size - 2 * mx) / (heights.length * 2 - 1);
  const put = (x, y) => {
    if (x < 0 || x >= size || y < 0 || y >= size) return;
    const o = (y * size + x) * 4;
    rgba[o] = 0;
    rgba[o + 1] = 0;
    rgba[o + 2] = 0;
    rgba[o + 3] = 255;
  };
  heights.forEach((hf, i) => {
    const x0 = mx + i * 2 * slot;
    const x1 = x0 + slot;
    const h = hf * size;
    const y0 = (size - h) / 2;
    const y1 = y0 + h;
    for (let y = Math.round(y0); y < Math.round(y1); y++)
      for (let x = Math.round(x0); x < Math.round(x1); x++) put(x, y);
  });
  return rgba;
}

const out = __dirname;
fs.writeFileSync(path.join(out, "trayTemplate.png"), pngFromRGBA(18, waveform(18)));
fs.writeFileSync(path.join(out, "trayTemplate@2x.png"), pngFromRGBA(36, waveform(36)));
console.log("wrote trayTemplate.png (18x18) + trayTemplate@2x.png (36x36)");
