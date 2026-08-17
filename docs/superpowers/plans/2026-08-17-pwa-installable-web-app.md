# Installable PWA (web app manifest) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Diariz web app installable in Chromium browsers, so a Linux user - who has no desktop build - gets a launcher entry, an icon, and a chromeless application window.

**Architecture:** A static web app manifest plus three PNG icons in `apps/web/public`, linked from `index.html`. nginx is taught the `.webmanifest` MIME type (its default `mime.types` has no entry) and explicit caching for both. Discoverability comes from one conditional row in the existing account menu, driven by a small self-contained `installPrompt` module that captures Chromium's `beforeinstallprompt` event at module scope. No service worker anywhere.

**Tech Stack:** React 19 + TypeScript + Vite, vitest + @testing-library/react, nginx (alpine), Node (the dependency-free icon generator in `apps/desktop/build`).

Spec: `docs/superpowers/specs/2026-08-17-pwa-installable-web-app-design.md`

## Global Constraints

- **Read the spec first.** Every design decision below has a recorded reason there; do not re-litigate them mid-implementation.
- **TDD is required.** Write the failing test, run it, watch it fail with the expected message, then write the minimal code. No production code without a preceding failing test.
- **Mutation-verify every assertion.** After a test goes green, break the thing it checks and confirm *that specific test* fails with a real message. A test that reads a file and asserts a field exists is exactly the shape that can pass while proving nothing.
- **No em or en dashes** (`-` only, never `—` or `–`) in UI strings, i18n catalogues, release notes, help articles, and user-visible copy. Code, comments, and internal docs are unaffected.
- **No new dependencies.** The icon generator is deliberately dependency-free; the tests parse PNG headers with `node:zlib` and `Buffer`, not an image library.
- **No jest-dom.** Zero of the 230+ web test files use its matchers. Use plain assertions (`expect(el).toBeTruthy()`, `expect(screen.queryByRole(...)).toBeNull()`).
- **Never `git add -A`.** Stage explicit paths - this repo accumulates agent scratch files.
- **Branch:** `feat/pwa-installable` (already created, spec already committed there). `main` is protected; finish by pushing and opening a PR.
- **Exact values:** `theme_color` and `background_color` are `#4f46e5`. Icon sizes are 192 and 512. `display` is `standalone`. Version goes `0.219.0` -> `0.220.0`.

## File Structure

| File | Responsibility |
|---|---|
| `apps/desktop/build/make-app-icon.js` | **Modify.** Gains a second output pass writing the three web icons. Single definition of the glyph geometry for every consumer. |
| `apps/web/public/icons/icon-192.png` | **Create (generated).** Manifest icon. |
| `apps/web/public/icons/icon-512.png` | **Create (generated).** Manifest icon. |
| `apps/web/public/icons/icon-maskable-512.png` | **Create (generated).** Full-bleed variant for platform masks. |
| `apps/web/public/manifest.webmanifest` | **Create.** The manifest. Static, no version number. |
| `apps/web/index.html` | **Modify.** Add `<link rel="manifest">`. |
| `apps/web/nginx.conf` | **Modify.** `.webmanifest` MIME type + explicit caching for the manifest and `/icons/`. |
| `apps/web/src/lib/installPrompt.ts` | **Create.** Captures `beforeinstallprompt`, exposes `useInstallPrompt()`. The only place install logic lives. |
| `apps/web/src/lib/manifest.test.ts` | **Create.** Static-asset assertions: the manifest, its icons, the generator drift guard, the nginx config, the index.html link. Sits beside `lib/linuxSystemAudio.test.ts`, which does the same job for the PipeWire drop-in. |
| `apps/web/src/lib/installPrompt.test.ts` | **Create.** Behaviour of the module and hook. |
| `apps/web/src/components/UserMenu.tsx` | **Modify.** One conditional `MenuRow`. |
| `apps/web/src/components/UserMenu.test.tsx` | **Modify.** Row visibility and click behaviour. |
| `apps/web/src/locales/{en,de,es,fr}/account.json` | **Modify.** The row label. |
| Release + docs targets | **Modify.** Listed in full in Task 6. |

---

### Task 1: Icons

Generate the three PNGs by extending the existing generator, and pin them with tests that would actually fail if the files were wrong.

**Files:**
- Modify: `apps/desktop/build/make-app-icon.js`
- Create (generated): `apps/web/public/icons/icon-192.png`, `icon-512.png`, `icon-maskable-512.png`
- Test: `apps/web/src/lib/manifest.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: three PNG files at `apps/web/public/icons/`, referenced by the manifest in Task 2. The generator gains an internal `render(size, { maskable })` signature; nothing outside the script imports it.

**Background you need:** `make-app-icon.js` draws the mark analytically (no rasteriser, no dependency) from a 60-unit viewBox: `inBackground(x, y)` is the rounded square, `inGlyph(x, y)` is the white microphone, and `render(size)` supersamples 4x4 over both. It currently writes only `build/icon.png` at 1024px. A maskable icon is cropped by the platform to a circle, squircle, or rounded square of its choosing, so only the central 80% of the canvas is guaranteed visible - feeding the rounded-square mark in unchanged would have its corners shaved and read as damaged. Full-bleed indigo survives every mask. No rescale of the glyph is needed: its furthest point from the centre is the stem tip at 17.5 of 60 units, i.e. 58% of the radius, already inside the safe circle.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/manifest.test.ts`:

```ts
/**
 * The web app manifest and its icons are shipped as static assets, so - like the Linux PipeWire drop-in
 * next door in linuxSystemAudio.test.ts - the files themselves are the deliverable and these assertions
 * pin the properties they must have to work at all.
 *
 * Chromium surfaces NO error when a site fails the install criteria: the install offer simply never
 * appears. That is why these are asserted rather than eyeballed.
 */
import { readFileSync, existsSync } from "node:fs";
import { inflateSync } from "node:zlib";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const WEB = join(__dirname, "..", "..");
const ICONS = join(WEB, "public", "icons");
const INDIGO = [79, 70, 229]; // #4f46e5, the mark's background

/// Width and height straight out of the PNG's IHDR chunk: an 8-byte signature, then a 4-byte length and
/// 4-byte type, then width and height as big-endian uint32s. No image library needed.
function pngSize(file: string): { width: number; height: number } {
  const buf = readFileSync(file);
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

/// The RGBA of the top-left pixel. Valid only for PNGs written by make-app-icon.js (8-bit RGBA, filter 0
/// on every row), which is all this test reads. Enough to tell a full-bleed icon from one whose corners
/// are transparent, which is the whole difference between the maskable variant and the plain one.
function topLeftPixel(file: string): number[] {
  const buf = readFileSync(file);
  const idat: Buffer[] = [];
  let off = 8;
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    if (buf.toString("ascii", off + 4, off + 8) === "IDAT") idat.push(buf.subarray(off + 8, off + 8 + len));
    off += 12 + len;
  }
  const raw = inflateSync(Buffer.concat(idat));
  return [raw[1], raw[2], raw[3], raw[4]]; // raw[0] is the row's filter byte
}

describe("PWA icons", () => {
  it("ships the two sizes Chromium requires, at their real pixel dimensions", () => {
    // A manifest may declare any `sizes` string it likes; Chromium checks the actual image. A 512 entry
    // pointing at a 192 file fails installability with nothing shown to the user.
    for (const px of [192, 512]) {
      const file = join(ICONS, `icon-${px}.png`);
      expect(existsSync(file)).toBe(true);
      expect(pngSize(file)).toEqual({ width: px, height: px });
    }
  });

  it("ships a 512 maskable variant", () => {
    const file = join(ICONS, "icon-maskable-512.png");
    expect(existsSync(file)).toBe(true);
    expect(pngSize(file)).toEqual({ width: 512, height: 512 });
  });

  it("draws the maskable variant full-bleed, not as a copy of the rounded square", () => {
    // The distinction is the entire point of the maskable entry, and a copy would pass every other
    // assertion here. The plain icon's corner is outside the rounded square, so it is transparent; the
    // maskable one must be opaque indigo right into the corner or the platform's mask will shave it.
    expect(topLeftPixel(join(ICONS, "icon-512.png"))[3]).toBe(0);
    expect(topLeftPixel(join(ICONS, "icon-maskable-512.png"))).toEqual([...INDIGO, 255]);
  });

  // Drift guard rather than a behaviour test, in the shape of the build-deb.sh assertion at the end of
  // linuxSystemAudio.test.ts: the committed PNGs are the source of truth, and if the generator is ever
  // repointed elsewhere they would start ageing silently with nothing to catch it.
  it("is generated by the desktop icon generator, so the mark has one definition", () => {
    const script = readFileSync(join(WEB, "..", "desktop", "build", "make-app-icon.js"), "utf8");
    expect(script).toContain("web");
    expect(script).toContain("public");
    expect(script).toContain("icons");
    expect(script).toContain("icon-maskable-512.png");
  });
});
```

- [ ] **Step 2: Run the test and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/manifest.test.ts
```

Expected: every test FAILS. The first two on `expect(existsSync(file)).toBe(true)` returning false; the drift guard on `icon-maskable-512.png` not appearing in the generator source.

- [ ] **Step 3: Extend the generator**

In `apps/desktop/build/make-app-icon.js`, add the output path constant below the existing `WHITE` constant (around line 22):

```js
/// The web app's manifest icons. Generated here rather than by a second script so the glyph geometry
/// below stays the single definition of the mark - it is already duplicated in diariz.svg, favicon.svg
/// and trayTemplate.png, each held in step by a comment rather than a check.
const WEB_ICONS = path.join(__dirname, "..", "..", "web", "public", "icons");
```

Change the `render` signature (line 95) and its background test (line 109):

```js
/// `maskable`: fill the whole canvas instead of only the rounded square. A maskable icon is cropped by
/// the platform to a circle/squircle of its choosing, so a rounded square's corners get shaved and the
/// silhouette reads as damaged; full bleed survives every mask. The glyph needs no rescale - its furthest
/// point from the centre is the stem tip at 17.5 of the 60-unit viewBox, 58% of the radius, already
/// inside the central 80% that a maskable icon guarantees is visible.
function render(size, { maskable = false } = {}) {
```

```js
          if (!maskable && !inBackground(vx, vy)) continue;
```

Replace the two trailing output lines (128-129) with:

```js
fs.writeFileSync(path.join(__dirname, "icon.png"), pngFromRGBA(SIZE, render(SIZE)));
console.log(`wrote icon.png (${SIZE}x${SIZE}) - microphone on an indigo rounded square`);

// The web app's manifest icons. Re-rendered analytically at each size rather than downscaled from the
// 1024, so small sizes stay as crisp as the supersampling allows.
fs.mkdirSync(WEB_ICONS, { recursive: true });
for (const px of [192, 512]) {
  fs.writeFileSync(path.join(WEB_ICONS, `icon-${px}.png`), pngFromRGBA(px, render(px)));
  console.log(`wrote ../../web/public/icons/icon-${px}.png (${px}x${px})`);
}
fs.writeFileSync(
  path.join(WEB_ICONS, "icon-maskable-512.png"),
  pngFromRGBA(512, render(512, { maskable: true })),
);
console.log("wrote ../../web/public/icons/icon-maskable-512.png (512x512) - full bleed for platform masks");
```

Also extend the file's header comment (line 12) so the run instruction mentions the new outputs:

```js
// Run: node build/make-app-icon.js   (from apps/desktop). Writes build/icon.png for the desktop app and
// apps/web/public/icons/* for the web app manifest. The committed PNGs are the source of truth; this
// script just regenerates them if the mark ever changes.
```

- [ ] **Step 4: Run the generator**

```bash
cd apps/desktop && node build/make-app-icon.js
```

Expected: four `wrote ...` lines. Confirm `git status` shows `build/icon.png` **unmodified** - the desktop icon must be byte-identical, since nothing about its rendering changed. If it differs, the `render` refactor altered the default path and must be fixed.

- [ ] **Step 5: Run the test and watch it pass**

```bash
cd apps/web && npx vitest run src/lib/manifest.test.ts
```

Expected: PASS.

- [ ] **Step 6: Mutation-verify**

Temporarily change `{ maskable: true }` to `{ maskable: false }` in the generator, re-run it, and re-run the test. Expected: the full-bleed test FAILS with the received value being `[0, 0, 0, 0]` rather than `[79, 70, 229, 255]`. Then revert the edit, re-run the generator, and confirm green again. If that test passed with the mutation in place, it is not testing what it claims.

- [ ] **Step 7: Commit**

```bash
git add apps/desktop/build/make-app-icon.js apps/web/public/icons apps/web/src/lib/manifest.test.ts
git commit -m "feat(pwa): generate the manifest icons from the existing mark generator"
```

---

### Task 2: The manifest and its link

**Files:**
- Create: `apps/web/public/manifest.webmanifest`
- Modify: `apps/web/index.html`
- Test: `apps/web/src/lib/manifest.test.ts` (add a second `describe`)

**Interfaces:**
- Consumes: the three icon files from Task 1, at `/icons/icon-192.png`, `/icons/icon-512.png`, `/icons/icon-maskable-512.png`.
- Produces: `/manifest.webmanifest` at the site root (Vite copies `public/` into `dist/` verbatim), consumed by the browser and by the nginx rules in Task 3.

- [ ] **Step 1: Write the failing test**

Append to `apps/web/src/lib/manifest.test.ts`:

```ts
describe("web app manifest", () => {
  const MANIFEST = join(WEB, "public", "manifest.webmanifest");
  const manifest = () => JSON.parse(readFileSync(MANIFEST, "utf8"));

  it("is shipped as a static asset the app can serve", () => {
    expect(existsSync(MANIFEST)).toBe(true);
  });

  // Each of the next four is a hard Chromium install requirement. Missing any one of them means the
  // install offer never appears, with no error anywhere for the user or the developer to see.
  it("names the app", () => {
    expect(manifest().name).toBe("Diariz");
    expect(manifest().short_name).toBe("Diariz");
  });

  it("starts at the app root", () => {
    expect(manifest().start_url).toBe("/");
  });

  it("opens without browser chrome", () => {
    expect(manifest().display).toBe("standalone");
  });

  it("does not defer to a related native app", () => {
    // `prefer_related_applications: true` would make Chromium point at a store listing instead of
    // installing the site. Absent counts as false, which is what we want, so assert it never appears.
    expect(manifest().prefer_related_applications).toBeUndefined();
  });

  it("declares icons that exist at the sizes it claims", () => {
    // The size strings are what Chromium checks against the real files, and Task 1's icons are the
    // files being pointed at - so this is the assertion that catches a typo'd path or a renamed icon.
    for (const icon of manifest().icons) {
      const file = join(WEB, "public", icon.src.replace(/^\//, ""));
      expect(existsSync(file)).toBe(true);
      const [w, h] = icon.sizes.split("x").map(Number);
      expect(pngSize(file)).toEqual({ width: w, height: h });
      expect(icon.type).toBe("image/png");
    }
  });

  it("declares both required sizes and a maskable variant", () => {
    const icons = manifest().icons as { sizes: string; purpose?: string }[];
    expect(icons.map((i) => i.sizes)).toContain("192x192");
    expect(icons.map((i) => i.sizes)).toContain("512x512");
    expect(icons.some((i) => i.purpose === "maskable")).toBe(true);
  });

  it("tints the installed window with the brand indigo", () => {
    // A single static value cannot follow the app's light/dark theme, so it is deliberately the brand
    // colour: intentional in both, rather than a light bar above a dark app. See the spec.
    expect(manifest().theme_color).toBe("#4f46e5");
    expect(manifest().background_color).toBe("#4f46e5");
  });

  it("carries no version number, which would only drift", () => {
    expect(manifest().version).toBeUndefined();
  });

  it("is linked from the document, without which it is inert", () => {
    const html = readFileSync(join(WEB, "index.html"), "utf8");
    expect(html).toContain('rel="manifest"');
    expect(html).toContain("/manifest.webmanifest");
  });
});
```

- [ ] **Step 2: Run the test and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/manifest.test.ts
```

Expected: the new `describe` FAILS - the first on `existsSync` false, the rest on `readFileSync` throwing ENOENT.

- [ ] **Step 3: Create the manifest**

`apps/web/public/manifest.webmanifest`:

```json
{
  "name": "Diariz",
  "short_name": "Diariz",
  "description": "Record, transcribe and diarize your meetings.",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "theme_color": "#4f46e5",
  "background_color": "#4f46e5",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png" },
    { "src": "/icons/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

- [ ] **Step 4: Link it from the document**

In `apps/web/index.html`, immediately after the existing favicon link:

```html
    <link rel="manifest" href="/manifest.webmanifest" />
```

- [ ] **Step 5: Run the test and watch it pass**

```bash
cd apps/web && npx vitest run src/lib/manifest.test.ts
```

Expected: PASS, all tests in both describes.

- [ ] **Step 6: Mutation-verify**

Change the manifest's `"/icons/icon-512.png"` to `"/icons/icon-511.png"` and re-run. Expected: the "declares icons that exist" test FAILS on `existsSync`. Revert. Then change `display` to `"browser"` and re-run: the "opens without browser chrome" test must FAIL. Revert.

- [ ] **Step 7: Commit**

```bash
git add apps/web/public/manifest.webmanifest apps/web/index.html apps/web/src/lib/manifest.test.ts
git commit -m "feat(pwa): add the web app manifest and link it from the document"
```

---

### Task 3: nginx MIME type and caching

**Files:**
- Modify: `apps/web/nginx.conf`
- Test: `apps/web/src/lib/manifest.test.ts` (add a third `describe`)

**Interfaces:**
- Consumes: `/manifest.webmanifest` and `/icons/` from Tasks 1-2.
- Produces: nothing other code imports. Behaviour verified live in Task 7.

**Background you need:** verified against the real runtime image, not assumed -

```bash
docker run --rm nginx:alpine sh -c "grep -n 'manifest' /etc/nginx/mime.types"
```

returns nothing on nginx 1.31.2. The default `mime.types` has no `manifest` entry, so `.webmanifest` would be served as `application/octet-stream`. Vite's dev server resolves it correctly, so this breaks **only** on a deployed box. Separately, files in `public/` land at the web root rather than under `/assets/`, so they fall through to `location /` with no `Cache-Control` and pick up heuristic freshness (RFC 9111 4.2.2) - the exact behaviour the long comment on the `index.html` block in this file warns about.

- [ ] **Step 1: Write the failing test**

Append to `apps/web/src/lib/manifest.test.ts`:

```ts
describe("nginx serves the manifest correctly", () => {
  const conf = () => readFileSync(join(WEB, "nginx.conf"), "utf8");

  it("maps the .webmanifest extension, which the base image does not", () => {
    // nginx:alpine's default mime.types has no `manifest` entry at all, so without this the file goes
    // out as application/octet-stream. The dev server gets it right, so this only ever breaks on a
    // deployed box - which is why it is asserted here rather than left to be noticed.
    expect(conf()).toContain("application/manifest+json");
    expect(conf()).toContain("webmanifest");
  });

  it("revalidates the manifest, which names the icons", () => {
    // Same argument the index.html block in this file makes at length: a document that names other
    // assets must not be served from a heuristic cache, or a stale copy pins the app's identity.
    expect(conf()).toMatch(/location = \/manifest\.webmanifest \{[^}]*no-cache/);
  });

  it("caches the icons hard, since the mark is stable", () => {
    expect(conf()).toMatch(/location \/icons\/ \{[^}]*immutable/);
  });
});
```

- [ ] **Step 2: Run the test and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/manifest.test.ts
```

Expected: the three new tests FAIL - `application/manifest+json` and both `location` blocks are absent from `nginx.conf`.

- [ ] **Step 3: Add the MIME type and caching blocks**

In `apps/web/nginx.conf`, inside the `server { }` block, immediately after the `index index.html;` line:

```nginx
    # nginx's bundled mime.types has no `manifest` entry (verified on nginx 1.31.2: `grep manifest
    # /etc/nginx/mime.types` returns nothing), so a .webmanifest would go out as the default_type,
    # application/octet-stream. Vite's dev server resolves the extension correctly, which is what makes
    # this a deploy-only failure and worth stating explicitly rather than discovering.
    types { application/manifest+json webmanifest; }
```

Then, immediately before the existing `location = /index.html` block:

```nginx
    # The web app manifest, revalidated on every load for the same reason index.html is below it: it is
    # the document that NAMES the icons and the start URL, so a heuristically-cached copy pins the
    # installed app's identity to a previous build. It is a few hundred bytes, so the usual answer is a 304.
    location = /manifest.webmanifest {
        add_header Cache-Control "no-cache";
    }

    # The manifest's icons. Unlike /assets/ these filenames are not content-hashed - they are static
    # files under public/ - but the mark is stable, and changing it can change the filename. Cached hard
    # so an installed app never refetches them. Without an explicit directive these fall through to
    # `location /` with no Cache-Control at all, which is the heuristic-freshness trap described below.
    location /icons/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
        try_files $uri =404;
    }
```

- [ ] **Step 4: Run the test and watch it pass**

```bash
cd apps/web && npx vitest run src/lib/manifest.test.ts
```

Expected: PASS.

- [ ] **Step 5: Verify nginx actually accepts the config**

A syntax error here would only appear when the container starts, so check it directly:

```bash
docker run --rm --add-host api:127.0.0.1 -v "$(pwd)/apps/web/nginx.conf:/etc/nginx/conf.d/default.conf:ro" nginx:alpine nginx -t
```

Expected: `syntax is ok` and `test is successful`. Run this from the repo root.

`--add-host` is required, not optional: `nginx -t` **does** resolve upstream hostnames at parse time, so without a stub for `api` it aborts with `host not found in upstream "api"` at the first `proxy_pass` - around line 56, which is *above* the blocks this task adds. The check would then report a failure that says nothing about your changes while silently never reaching them.

- [ ] **Step 6: Commit**

```bash
git add apps/web/nginx.conf apps/web/src/lib/manifest.test.ts
git commit -m "feat(pwa): serve the manifest with the right MIME type and caching"
```

---

### Task 4: The install prompt module

**Files:**
- Create: `apps/web/src/lib/installPrompt.ts`
- Test: `apps/web/src/lib/installPrompt.test.ts`

**Interfaces:**
- Consumes: `isElectron` from `apps/web/src/lib/audioSource.ts` (an exported `const boolean`).
- Produces: `useInstallPrompt(): { canInstall: boolean; install: () => void }`, consumed by `UserMenu` in Task 5. That exact name and shape.

**Background you need:** `renderHook` from `@testing-library/react` is used here; it is rare in this repo but not new - `src/lib/panelTab.test.ts` is the precedent, in the same directory. Chromium fires `beforeinstallprompt` shortly after load when the page meets the install criteria, and that event object is the *only* handle on the install flow - there is no API to request it later. So the listener must be registered at **module scope**, not in a React effect: by the time the account menu mounts the event may already have fired, and a missed event means the row never appears. Also: **jsdom does not implement `matchMedia`** (see `theme.test.tsx:37`), which is why `theme.tsx:12` uses the optional-call form `window.matchMedia?.(...)`. Follow that idiom or every test in the suite that renders this will throw.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/installPrompt.test.ts`:

```ts
/**
 * The module keeps state at module scope (see the comment in installPrompt.ts for why), so each test
 * re-imports it fresh via vi.resetModules() rather than sharing one instance across the file.
 */
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act } from "@testing-library/react";

/// A stand-in for Chromium's BeforeInstallPromptEvent: a real Event (so dispatchEvent works) carrying
/// the one method the real one adds.
function installEvent(): Event & { prompt: ReturnType<typeof vi.fn> } {
  const e = new Event("beforeinstallprompt") as Event & { prompt: ReturnType<typeof vi.fn> };
  e.prompt = vi.fn().mockResolvedValue(undefined);
  return e;
}

/// jsdom has no matchMedia at all, so the module's installed-check needs one stubbed in before import.
function stubDisplayMode(standalone: boolean) {
  window.matchMedia = vi.fn().mockReturnValue({
    matches: standalone,
    addEventListener: () => {},
    removeEventListener: () => {},
  }) as unknown as typeof window.matchMedia;
}

async function load() {
  vi.resetModules();
  return await import("./installPrompt");
}

beforeEach(() => {
  stubDisplayMode(false);
  vi.doUnmock("./audioSource");
});

afterEach(() => {
  vi.resetModules();
});

describe("useInstallPrompt", () => {
  it("cannot install before the browser has offered", async () => {
    // No event means one of: wrong browser, plain http, criteria unmet, or already installed. All of
    // them are "do not show the row".
    const { useInstallPrompt } = await load();
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
  });

  it("can install once the browser has offered, even if the event fired before React mounted", async () => {
    // The load-order case the module exists to handle: dispatch first, mount second.
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(true);
  });

  it("can install when the event arrives after mounting", async () => {
    const { useInstallPrompt } = await load();
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
    act(() => {
      window.dispatchEvent(installEvent());
    });
    expect(result.current.canInstall).toBe(true);
  });

  it("suppresses Chromium's own mini-infobar, so the menu row is the only affordance", async () => {
    await load();
    const e = installEvent();
    const prevented = vi.spyOn(e, "preventDefault");
    act(() => {
      window.dispatchEvent(e);
    });
    expect(prevented).toHaveBeenCalled();
  });

  it("prompts the stashed event, once", async () => {
    const { useInstallPrompt } = await load();
    const e = installEvent();
    act(() => {
      window.dispatchEvent(e);
    });
    const { result } = renderHook(() => useInstallPrompt());
    act(() => {
      result.current.install();
    });
    expect(e.prompt).toHaveBeenCalledTimes(1);
    // The event is single-use - Chromium rejects a second prompt() on the same one - so the row must go
    // away rather than sit there doing nothing.
    expect(result.current.canInstall).toBe(false);
    act(() => {
      result.current.install();
    });
    expect(e.prompt).toHaveBeenCalledTimes(1);
  });

  it("stops offering once the app has been installed", async () => {
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(true);
    act(() => {
      window.dispatchEvent(new Event("appinstalled"));
    });
    expect(result.current.canInstall).toBe(false);
  });

  it("never offers inside an installed window", async () => {
    // Offering to install the app you are already running is nonsense, and Chromium can still fire the
    // event in some launch paths.
    stubDisplayMode(true);
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
  });

  it("never offers inside the Electron shell, which is already the desktop app", async () => {
    vi.doMock("./audioSource", () => ({ isElectron: true }));
    const { useInstallPrompt } = await load();
    act(() => {
      window.dispatchEvent(installEvent());
    });
    const { result } = renderHook(() => useInstallPrompt());
    expect(result.current.canInstall).toBe(false);
  });
});
```

- [ ] **Step 2: Run the test and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/installPrompt.test.ts
```

Expected: FAIL on the dynamic import - `Failed to resolve import "./installPrompt"`.

- [ ] **Step 3: Write the module**

Create `apps/web/src/lib/installPrompt.ts`:

```ts
import { useEffect, useState } from "react";
import { isElectron } from "./audioSource";

/**
 * Chromium's install flow, wrapped so the app can offer it from its own menu.
 *
 * The browser's own entry point is a small icon in the omnibox, which is not adequate discoverability
 * for the platform this exists to serve: Linux, where there is no desktop build and the installed window
 * is the whole point.
 *
 * `beforeinstallprompt` fires shortly after load when the page meets the install criteria, and that event
 * object is the ONLY handle on the flow - there is no API to ask for it later. So the listener is
 * registered at MODULE SCOPE rather than inside a hook's effect: by the time React has mounted the
 * account menu the event may already have fired, and a missed event means the row never appears at all.
 * That is also why there is a subscriber set here instead of component state - the event belongs to the
 * module, and any number of components may want to know about it.
 */

type InstallEvent = Event & { prompt: () => Promise<unknown> };

let deferred: InstallEvent | null = null;
const subscribers = new Set<() => void>();

function announce(): void {
  for (const notify of subscribers) notify();
}

if (typeof window !== "undefined") {
  window.addEventListener("beforeinstallprompt", (e) => {
    // Suppress Chromium's own mini-infobar so the account-menu row is the single install affordance.
    e.preventDefault();
    deferred = e as InstallEvent;
    announce();
  });
  window.addEventListener("appinstalled", () => {
    deferred = null;
    announce();
  });
}

/// True when this document is itself an installed app. `matchMedia` is called optionally because jsdom
/// does not implement it at all - the same reason theme.tsx guards its prefers-color-scheme query.
function isInstalledWindow(): boolean {
  return window.matchMedia?.("(display-mode: standalone)").matches === true;
}

export function useInstallPrompt(): { canInstall: boolean; install: () => void } {
  const [offered, setOffered] = useState(deferred !== null);

  useEffect(() => {
    const notify = () => setOffered(deferred !== null);
    subscribers.add(notify);
    // The event can land between this component rendering and this effect running.
    notify();
    return () => {
      subscribers.delete(notify);
    };
  }, []);

  const install = () => {
    const event = deferred;
    if (!event) return;
    // Single-use: Chromium rejects a second prompt() on the same event, so drop it before prompting and
    // let the row disappear rather than leaving a control that silently does nothing.
    deferred = null;
    announce();
    void event.prompt();
  };

  return { canInstall: offered && !isElectron && !isInstalledWindow(), install };
}
```

- [ ] **Step 4: Run the test and watch it pass**

```bash
cd apps/web && npx vitest run src/lib/installPrompt.test.ts
```

Expected: PASS, all eight tests.

- [ ] **Step 5: Mutation-verify the two suppression rules**

These are the assertions most likely to be vacuous, because they check for `false` and the default is `false`. Change the return to `{ canInstall: offered, install }` and re-run. Expected: **both** the "never offers inside an installed window" and "never offers inside the Electron shell" tests FAIL. If either still passes, its setup is not reaching the code and the test is worthless. Revert.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/lib/installPrompt.ts apps/web/src/lib/installPrompt.test.ts
git commit -m "feat(pwa): capture the browser install prompt behind a hook"
```

---

### Task 5: The account-menu row

**Files:**
- Modify: `apps/web/src/components/UserMenu.tsx`
- Modify: `apps/web/src/components/UserMenu.test.tsx`
- Modify: `apps/web/src/locales/en/account.json`, `de/account.json`, `es/account.json`, `fr/account.json`

**Interfaces:**
- Consumes: `useInstallPrompt()` from Task 4.
- Produces: a menu row with accessible name "Install app" (English). No exports.

**Background you need:** `UserMenu.tsx` renders rows through a local `MenuRow` component inside a `role="menu"` div, several of them already conditional (`{isAdmin && <MenuRow ... />}`). Reusing `MenuRow` means the row inherits hover treatment, padding, and `role="menuitem"` for free - do not build new UI. Its test file mocks `../auth` and `../lib/api` with `vi.mock` and renders inside `QueryClientProvider` + `MemoryRouter`.

- [ ] **Step 1: Write the failing test**

Add to `apps/web/src/components/UserMenu.test.tsx`. First, at the top with the other mocks, add a controllable mock of the hook:

```ts
// Controllable per test: the real hook depends on a browser event jsdom never fires.
const installState = { canInstall: false, install: vi.fn() };
vi.mock("../lib/installPrompt", () => ({ useInstallPrompt: () => installState }));
```

Then add a **sibling top-level** `describe` after the existing `describe("UserMenu", ...)` block closes - not nested inside it, so its `beforeEach` (which calls `vi.clearAllMocks()` and resets `authState`) cannot clear the `install` spy after this block has set it up.

Note the exact pattern the file already uses: `renderMenu()` only renders - the rows live inside a popover that must be opened by clicking the account button first, and name matchers are case-insensitive regexes.

```ts
describe("UserMenu install row", () => {
  beforeEach(() => {
    installState.canInstall = false;
    installState.install = vi.fn();
  });

  it("is absent when the browser has not offered to install", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.queryByRole("menuitem", { name: /install app/i })).toBeNull();
  });

  it("appears when the browser has offered", () => {
    installState.canInstall = true;
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.getByRole("menuitem", { name: /install app/i })).toBeTruthy();
  });

  it("triggers the install when clicked", () => {
    installState.canInstall = true;
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /install app/i }));
    expect(installState.install).toHaveBeenCalledTimes(1);
  });
});
```

- [ ] **Step 2: Run the test and watch it fail**

```bash
cd apps/web && npx vitest run src/components/UserMenu.test.tsx
```

Expected: the "appears when the browser has offered" and "triggers the install" tests FAIL with `Unable to find an accessible element with the role "menuitem" and name "Install app"`. The "is absent" test passes already - which is exactly why the other two matter.

- [ ] **Step 3: Add the label to all four locales**

In each of `apps/web/src/locales/{en,de,es,fr}/account.json`, add a key immediately after `"showTour"`:

| Locale | Line to add |
|---|---|
| en | `"installApp": "Install app",` |
| de | `"installApp": "App installieren",` |
| es | `"installApp": "Instalar la aplicación",` |
| fr | `"installApp": "Installer l'application",` |

- [ ] **Step 4: Add the row**

In `apps/web/src/components/UserMenu.tsx`, add the import:

```ts
import { useInstallPrompt } from "../lib/installPrompt";
```

Inside the component, beside the other hook calls near the top:

```ts
  const { canInstall, install } = useInstallPrompt();
```

And in the `role="menu"` block, immediately before the `about` row:

```tsx
            {/* Only when the browser has actually offered - see installPrompt.ts. Chromium's own install
                icon in the omnibox is easy to miss, and on Linux the installed window is the only thing
                standing in for a desktop app. */}
            {canInstall && <MenuRow label={t("installApp")} onSelect={run(install)} />}
```

- [ ] **Step 5: Run the tests and watch them pass**

```bash
cd apps/web && npx vitest run src/components/UserMenu.test.tsx
```

Expected: PASS, including the tests that were already in the file.

- [ ] **Step 6: Run the whole web suite**

```bash
cd apps/web && npm test
```

Expected: PASS with no new failures and no new warnings. A passing run here has no errors or warnings at all - if the new row broke another test's snapshot of the menu, fix it now.

- [ ] **Step 7: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: clean `tsc` and a successful Vite build. Confirm the build output includes `manifest.webmanifest` and `icons/`:

```bash
ls apps/web/dist/manifest.webmanifest apps/web/dist/icons
```

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/UserMenu.tsx apps/web/src/components/UserMenu.test.tsx apps/web/src/locales
git commit -m "feat(pwa): offer Install app from the account menu"
```

---

### Task 6: Release and documentation chores

Every item here is required by CLAUDE.md's release checklist. This is a functional enhancement, so **Minor +1: 0.219.0 -> 0.220.0**.

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts` (RELEASES entry + CAPABILITIES row)
- Modify: `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/content/help/en/recording-audio.md`
- Test: `apps/web/src/lib/releases.test.ts` and `versionMirrors.test.ts` (existing - they must pass, not be edited)

- [ ] **Step 1: Bump the version in all five places**

| File | Change |
|---|---|
| `version.json` | `{ "version": "0.220.0" }` |
| `apps/web/package.json` line 4 | `"version": "0.220.0",` |
| `apps/desktop/package.json` line 4 | `"version": "0.220.0",` |
| `integrations/n8n-nodes-diariz/package.json` line 3 | `"version": "0.220.0",` |
| `src/Diariz.Api/Diariz.Api.csproj` line 8 | `<Version>0.220.0</Version>` |

`versionMirrors.test.ts` exists because the n8n node silently sat at `0.1.0` for ~70 releases, and an npm version cannot be corrected once published. Do not skip any of the five.

- [ ] **Step 2: Run the version tests and watch them fail**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: `versionMirrors` PASSES (all five now agree), `releases` FAILS - `RELEASES[0].version` is still `0.219.0` and must equal `version.json`.

- [ ] **Step 3: Add the release entry**

Insert at the top of the `RELEASES` array in `apps/web/src/lib/releases.ts` (before the `0.219.0` entry), leaving `pr` to be corrected in Step 8:

```ts
  {
    version: "0.220.0",
    date: "2026-08-17",
    pr: 0,
    headline: "Install Diariz as an app",
    summary:
      "Diariz can now be installed from a Chromium browser (Chrome, Edge) as an app in its own right: it gets a launcher entry and icon alongside your other applications, and opens in its own window with no browser chrome, tabs, or address bar. An **Install app** entry appears in the account menu whenever your browser offers it, which saves hunting for the small install icon in the address bar.\n\nThis matters most on **Linux**, where there is no desktop installer. Everything else about running Diariz on Linux was already covered - system audio can be recorded by installing the PipeWire configuration described in the Recording audio help article, which publishes your speakers as an ordinary microphone - so the application window was the last piece missing. Installing gives Linux users the same day-to-day feel as the Windows and macOS desktop apps.\n\nWindows and macOS users can install it this way too if they prefer it to the installer, though the desktop app remains the fuller option there: it adds a tray icon, recording without the window open, the pop-out notes window, and meeting screenshots. An installed window is a window, not the whole desktop app - there is no tray presence, and with no network it shows the browser's offline page rather than working offline.",
    added: [
      "Install Diariz from a Chromium browser as an app with its own launcher entry, icon, and chromeless window.",
      "An Install app entry in the account menu, shown whenever the browser offers to install.",
    ],
  },
```

- [ ] **Step 4: Run the release tests and watch them pass**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: PASS.

- [ ] **Step 5: Add the About-box capability row**

In the `CAPABILITIES` template string in the same file, add a row immediately after the `**Help & documentation**` row (the last row of the table, before the closing localisation paragraph):

```
| **Install as an app** | Install Diariz from a Chromium browser and it gets a launcher entry, an icon, and its own window with no browser chrome - the account menu offers it whenever your browser can. This is how Linux gets an app-like Diariz, since the desktop installer is Windows and macOS only. |
```

- [ ] **Step 6: Update the README features table**

In `README.md`, add a row immediately after the `**Desktop apps**` row (line 58):

```
| **Install as an app** | Install from a Chromium browser (Chrome, Edge) for a launcher entry, an icon, and a chromeless window; offered from the account menu. The route to an app-like Diariz on Linux, where there is no installer. |
```

- [ ] **Step 7: Update docs/features.md in lockstep**

In `docs/features.md`, add a bullet immediately before the `- **Help and documentation.**` bullet:

```
- **Install it as an app.** From a Chromium browser (Chrome, Edge), Diariz can be installed as an
application in its own right - a launcher entry and icon alongside your other apps, opening in its own
window with no tabs or address bar. An **Install app** entry appears in the account menu whenever the
browser offers it, so the small install icon in the address bar is not the only way to find it. This is
what gives **Linux** an app-like Diariz: there is no Linux installer, and system audio there is already
handled by the PipeWire drop-in (see **Capture**), so the window was the last piece missing. It works on
Windows and macOS too, though the Electron desktop app remains the fuller option on those platforms - an
installed window has no tray presence, no pop-out notes window, and no screenshots, and offline it shows
the browser's own error page rather than working offline. Firefox does not install web apps.
```

- [ ] **Step 8: Update the architecture doc**

Two edits to `docs/Overall_Synopsis_of_Platform.md`, both in places that already exist - read the surrounding prose and match its voice rather than pasting:

**a. The Components table (line 34).** The **Web** row's description reads "SPA UI (served by nginx in Docker)". Extend it to record that the SPA is also **installable as a PWA** in Chromium browsers, which is the only app-like client on Linux since the desktop shell has no Linux build.

**b. The redeploy/caching passage (lines 93-102).** That passage is the natural home, because it already explains at length why `index.html` is `no-cache` and `/assets/` is `immutable`. Add a short paragraph after it covering the two new facts:

1. `/manifest.webmanifest` is `no-cache` for the same reason as the shell - it is the document that names the icons and the start URL, so a heuristically-cached copy pins the installed app's identity - while `/icons/` is `immutable` like `/assets/`.
2. nginx must be given `application/manifest+json` for `.webmanifest` explicitly, because its bundled `mime.types` has no `manifest` entry (verified on nginx 1.31.2). Vite's dev server resolves the extension on its own, so this fails **only** on a deployed box, and any replacement front end or alternative web server needs the same mapping or the manifest goes out as `application/octet-stream`.

State also that there is **no service worker**, deliberately: a second caching layer in front of the shell is exactly the hazard that passage describes.

`docs/Data_Schema.md` is **deliberately not touched**: no schema, storage, migration, or vector change.

- [ ] **Step 9: Point the Linux help section at Install**

In `apps/web/src/content/help/en/recording-audio.md`, at the end of the "On Linux, share a tab and not the whole screen" section (after the paragraph ending "...it costs nothing when unused."), add:

```
There is no Linux installer for Diariz, but you do not have to live in a browser tab either: open the
account menu and choose **Install app**, and Chromium adds Diariz to your applications with its own icon
and window. Recording works exactly the same way inside it, including the system-audio device above.
```

Content here is **ASCII only** and enforced by `helpContent.test.ts`. Do not add a `?` button, and do not restructure the article - only the behaviour a user relies on has changed, and help articles are not a fourth sync target for the features tables.

- [ ] **Step 10: Run the full web suite**

```bash
cd apps/web && npm test
```

Expected: PASS. `helpContent.test.ts` will fail on any non-ASCII character in the help addition, naming the file, line, and character.

- [ ] **Step 11: Check for stray em dashes in what you wrote**

Two traps here, both of which have produced a false clean result in this repo before:

- **Scope it to the diff, not the files.** `README.md` already carries 18 em/en dashes and `docs/features.md` 58, all pre-existing and none of them your problem. A whole-file count tells you nothing.
- **Do not pipe `git diff` into python.** On this machine the pipe decodes as cp1252 and the dash check reports zero regardless. Capture the bytes and decode UTF-8 explicitly.

This command does both - run it from the repo root:

```bash
python -c "import io, subprocess; raw = subprocess.run(['git','diff','HEAD','--unified=0'], capture_output=True).stdout; t = raw.decode('utf-8', 'replace'); added = [l for l in t.splitlines() if l.startswith('+') and not l.startswith('+++')]; bad = [l for l in added if any(c in l for c in '\u2014\u2013')]; print('added lines:', len(added), '| with em/en dash:', len(bad)); [print('  ', l[:120]) for l in bad]"
```

Expected: `with em/en dash: 0`. Every line it prints is one you added - replace the dash with a plain hyphen and re-run.

- [ ] **Step 12: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help/en/recording-audio.md
git commit -m "chore(release): 0.220.0 - installable PWA"
```

---

### Task 7: Live verification, then the PR

jsdom computes no geometry and cannot install anything, so the claim "this is installable" is unproven until a real browser installs it. Do not skip this task - a manifest that fails one criterion produces no error anywhere.

**Files:** none (verification), then the PR.

- [ ] **Step 1: Build and serve the real container**

```bash
cd deploy && docker compose up --build -d web
```

The SPA is then at http://localhost:8081, which counts as a secure context for install criteria because it is localhost.

- [ ] **Step 2: Verify the manifest is served with the right MIME type**

```bash
curl -sI http://localhost:8081/manifest.webmanifest
```

Expected: `200`, `Content-Type: application/manifest+json`, and `Cache-Control: no-cache`. If the type is `application/octet-stream` the `types` block did not take effect. Then check an icon:

```bash
curl -sI http://localhost:8081/icons/icon-512.png
```

Expected: `200`, `Content-Type: image/png`, and `Cache-Control: public, max-age=31536000, immutable`.

- [ ] **Step 3: Check Chromium's own verdict**

Open http://localhost:8081 in Chrome or Edge, sign in, then DevTools > Application > Manifest. Expected: name, icons, and theme colour all shown, and **no** entries under Installability. That panel names the specific unmet criterion when there is one, which is far faster than guessing.

- [ ] **Step 4: Install it and check the window**

Install from the account menu's **Install app** row (not the omnibox icon - the point is to verify our affordance works). Confirm:

- the row appears at all, and disappears after installing;
- an application/launcher entry exists with the Diariz mark, not a generic browser icon;
- the window opens with no tabs and no address bar, and the titlebar is indigo;
- the account menu inside the installed window does **not** offer Install app any more.

- [ ] **Step 5: Confirm recording still works in the installed window**

Start a recording, stop it, and confirm it uploads and reaches transcription. The installed window is same-origin with everything, so nothing should differ - but "the app still works when it is not a tab" is the claim this whole change rests on, and it costs one recording to check.

- [ ] **Step 6: Screenshot the installed window**

Capture the installed window for the PR description. It is the only evidence a reviewer cannot reproduce from the diff.

- [ ] **Step 7: Push and open the PR**

```bash
git push -u origin feat/pwa-installable
```

Then open the PR with `gh pr create`. The body must state:

- what it does and why (the Linux gap);
- **Deployment surface: server redeploy only.** No desktop release. `apps/desktop/src/**`, shipped `build/**` assets, `electron-builder.config.js`, and desktop dependencies are all untouched - `make-app-icon.js` is a build-time generator, not shipped code - so the lockstep bump to `apps/desktop/package.json` does not need a new installer;
- the nginx MIME requirement, flagged for whoever maintains the outer reverse proxy;
- the live verification results from Steps 2-5, and the screenshot.

- [ ] **Step 8: Correct the PR number in the release notes**

Read the real number from the PR you just opened, then:

```bash
# replace 0 with the real PR number in the 0.220.0 entry
git add apps/web/src/lib/releases.ts
git commit -m "chore(release): record the real PR number for 0.220.0"
git push
```

Do not guess "last + 1" - Dependabot and issues share that sequence, and no test catches a wrong number.

- [ ] **Step 9: Tear down the local stack**

```bash
cd deploy && docker compose stop web
```
