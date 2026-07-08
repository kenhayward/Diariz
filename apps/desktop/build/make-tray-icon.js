"use strict";

// Generates the monochrome macOS menu-bar Template icons - a microphone glyph - as
// build/trayTemplate.png (18x18) and build/trayTemplate@2x.png (36x36). Template images are black-on-
// transparent; macOS recolours them for the light/dark menu bar automatically. Edges are anti-aliased
// (3x3 supersampling) so the curves stay crisp at menu-bar size.
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

function distToVSegment(x, y, cx, y1, y2) {
  if (y < y1) return Math.hypot(x - cx, y - y1);
  if (y > y2) return Math.hypot(x - cx, y - y2);
  return Math.abs(x - cx);
}

// True if the point (in pixel coords) is inside the microphone silhouette on an SxS canvas.
// Shape = capsule (mic head) + U cradle + stem + base, all as fractions of S so it scales.
function inMic(x, y, S) {
  const cx = S / 2;
  // capsule / mic head (a vertical pill)
  const r = S * 0.16;
  const capTop = S * 0.12;
  const capBot = S * 0.54;
  if (distToVSegment(x, y, cx, capTop + r, capBot - r) <= r) return true;
  // cradle: lower half of a ring hugging the capsule
  const arcCy = S * 0.44;
  const R = S * 0.28;
  const t = S * 0.09;
  const d = Math.hypot(x - cx, y - arcCy);
  if (y >= arcCy && d <= R && d >= R - t) return true;
  // stem (connects the cradle to the base)
  if (Math.abs(x - cx) <= S * 0.038 && y >= S * 0.7 && y <= S * 0.83) return true;
  // base
  if (Math.abs(x - cx) <= S * 0.16 && Math.abs(y - S * 0.83) <= S * 0.038) return true;
  return false;
}

function microphone(size) {
  const rgba = Buffer.alloc(size * size * 4); // transparent
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      // 3x3 supersample for anti-aliased edges.
      let hits = 0;
      for (let sy = 0; sy < 3; sy++)
        for (let sx = 0; sx < 3; sx++)
          if (inMic(x + (sx + 0.5) / 3, y + (sy + 0.5) / 3, size)) hits++;
      if (hits === 0) continue;
      const o = (y * size + x) * 4;
      rgba[o] = 0;
      rgba[o + 1] = 0;
      rgba[o + 2] = 0;
      rgba[o + 3] = Math.round((hits / 9) * 255);
    }
  }
  return rgba;
}

const out = __dirname;
fs.writeFileSync(path.join(out, "trayTemplate.png"), pngFromRGBA(18, microphone(18)));
fs.writeFileSync(path.join(out, "trayTemplate@2x.png"), pngFromRGBA(36, microphone(36)));
console.log("wrote trayTemplate.png (18x18) + trayTemplate@2x.png (36x36) - microphone glyph");
