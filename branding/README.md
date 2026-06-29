# Branding assets

Shared brand assets for Diariz.

| File | What it is |
|---|---|
| `social-preview.png` | GitHub **social preview** card, 1280×640 (~134 KB). |
| `social-preview.svg` | Editable source for the card (logo emblem + copy). |

## Social preview

`social-preview.png` is the Open Graph image shown whenever a link to the repo is shared (X, Slack,
LinkedIn, Discord, etc.). To set it:

**Repo → Settings → General → Social preview → Edit → Upload an image** → pick `social-preview.png`.

GitHub recommends a **2:1** image, **1280×640** (min 640×320), under **1 MB**, PNG/JPG/GIF.

### Re-rendering the card

The PNG is rasterised from `social-preview.svg` (which uses system fonts — Arial/Helvetica). Regenerate it
with any SVG→PNG renderer (Inkscape, ImageMagick/`rsvg`, a headless browser, or `@resvg/resvg-js`), e.g.:

```js
// node, after: npm i @resvg/resvg-js
const { Resvg } = require("@resvg/resvg-js");
const fs = require("fs");
const r = new Resvg(fs.readFileSync("social-preview.svg", "utf8"), {
  fitTo: { mode: "width", value: 1280 },
  font: { loadSystemFonts: true },
});
fs.writeFileSync("social-preview.png", r.render().asPng());
```

The brand mark is the teal emblem from `apps/web/public/favicon.svg` (`#22b7b5`) on a light badge; the card
background is the app's dark slate (`#0b1224` → `#111a33`).
