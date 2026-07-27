"use strict";

// Generates build/icon.png - the app icon behind the Windows installer/.ico, the macOS .icns, the app
// window, the tray, and notifications. The artwork is the same mark the n8n community node uses
// (integrations/n8n-nodes-diariz/nodes/Diariz/diariz.svg): a white microphone on an indigo rounded
// square, deliberately simple so it stays legible at 16px in the taskbar and tray.
//
// The SVG's 60x60 viewBox is redrawn here as analytic shapes rather than rasterised, so there is no
// third-party dependency (same approach as make-tray-icon.js). Keep the two in step: if the SVG's
// geometry changes, change GLYPH below to match. Edges are 4x4 supersampled.
//
// Run: node build/make-app-icon.js   (from apps/desktop). The committed PNG is the source of truth;
// this script just regenerates it if the mark ever changes.

const zlib = require("node:zlib");
const fs = require("node:fs");
const path = require("node:path");

const SIZE = 1024;          // >=512 keeps the macOS .icns and the 256px Windows .ico free of upscaling
const VIEW = 60;            // the SVG viewBox the geometry below is expressed in
const INDIGO = [79, 70, 229]; // #4f46e5, the SVG's rect fill
const WHITE = [255, 255, 255];

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

/// Distance from (x,y) to the segment (x1,y1)-(x2,y2). Used for every stroked part of the glyph: a
/// round-capped stroke of width w is exactly "within w/2 of the segment".
function distToSegment(x, y, x1, y1, x2, y2) {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const len2 = dx * dx + dy * dy;
  const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((x - x1) * dx + (y - y1) * dy) / len2));
  return Math.hypot(x - (x1 + t * dx), y - (y1 + t * dy));
}

/// <rect width="60" height="60" rx="12"> - inside the rounded square, so the corners stay transparent.
function inBackground(x, y) {
  const r = 12;
  const qx = Math.abs(x - VIEW / 2) - (VIEW / 2 - r);
  const qy = Math.abs(y - VIEW / 2) - (VIEW / 2 - r);
  if (qx <= 0 || qy <= 0) return x >= 0 && x <= VIEW && y >= 0 && y <= VIEW;
  return Math.hypot(qx, qy) <= r;
}

/// The white microphone: capsule head, the cradle's lower arc with its two uprights, and the stem.
/// Mirrors the three <path>s in diariz.svg (the strokes are width 3, so half-width 1.5).
function inGlyph(x, y) {
  // Head: "M30 14a6 6 0 0 1 6 6v10a6 6 0 0 1-12 0V20a6 6 0 0 1 6-6z" - a capsule between (30,20)
  // and (30,30) with radius 6, spanning y 14..36.
  if (distToSegment(x, y, 30, 20, 30, 30) <= 6) return true;

  // Cradle: "M20 28v2a10 10 0 0 0 20 0v-2" - two short uprights joined by the lower half of a
  // radius-10 arc centred on (30,30).
  if (distToSegment(x, y, 20, 28, 20, 30) <= 1.5) return true;
  if (distToSegment(x, y, 40, 28, 40, 30) <= 1.5) return true;
  if (y >= 30 && Math.abs(Math.hypot(x - 30, y - 30) - 10) <= 1.5) return true;

  // Stem: "M30 40v6".
  return distToSegment(x, y, 30, 40, 30, 46) <= 1.5;
}

function render(size) {
  const rgba = Buffer.alloc(size * size * 4); // transparent
  const samples = 4;
  const scale = VIEW / size;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let covered = 0;
      let r = 0;
      let g = 0;
      let b = 0;
      for (let sy = 0; sy < samples; sy++) {
        for (let sx = 0; sx < samples; sx++) {
          const vx = (x + (sx + 0.5) / samples) * scale;
          const vy = (y + (sy + 0.5) / samples) * scale;
          if (!inBackground(vx, vy)) continue;
          const [cr, cg, cb] = inGlyph(vx, vy) ? WHITE : INDIGO;
          covered++;
          r += cr;
          g += cg;
          b += cb;
        }
      }
      if (covered === 0) continue;
      const o = (y * size + x) * 4;
      rgba[o] = Math.round(r / covered);
      rgba[o + 1] = Math.round(g / covered);
      rgba[o + 2] = Math.round(b / covered);
      rgba[o + 3] = Math.round((covered / (samples * samples)) * 255);
    }
  }
  return rgba;
}

fs.writeFileSync(path.join(__dirname, "icon.png"), pngFromRGBA(SIZE, render(SIZE)));
console.log(`wrote icon.png (${SIZE}x${SIZE}) - microphone on an indigo rounded square`);
