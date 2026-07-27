import { describe, it, expect } from "vitest";
import { resolveImages, buildAssetMap, localImageRefs } from "./images";

const assets = buildAssetMap({
  "../../content/help/en/images/formula-editor.png": "/assets/formula-editor-a1b2.png",
  "../../content/help/en/images/nested/deep.png": "/assets/deep-c3d4.png",
  "../../content/help/de/images/formula-editor.png": "/assets/formula-editor-de-e5f6.png",
});

describe("buildAssetMap", () => {
  it("keys assets by locale and path relative to the locale folder", () => {
    expect(assets["en/images/formula-editor.png"]).toBe("/assets/formula-editor-a1b2.png");
    expect(assets["de/images/formula-editor.png"]).toBe("/assets/formula-editor-de-e5f6.png");
  });

  it("keeps nested folders in the key", () => {
    expect(assets["en/images/nested/deep.png"]).toBe("/assets/deep-c3d4.png");
  });
});

describe("resolveImages", () => {
  it("rewrites a ./-relative image to its bundled url", () => {
    const out = resolveImages("![Editor](./images/formula-editor.png)", "en", assets);
    expect(out).toBe("![Editor](/assets/formula-editor-a1b2.png)");
  });

  it("rewrites a bare relative image too", () => {
    const out = resolveImages("![Editor](images/formula-editor.png)", "en", assets);
    expect(out).toBe("![Editor](/assets/formula-editor-a1b2.png)");
  });

  it("uses the locale's own image when there is one", () => {
    const out = resolveImages("![Editor](./images/formula-editor.png)", "de", assets);
    expect(out).toBe("![Editor](/assets/formula-editor-de-e5f6.png)");
  });

  it("falls back to the English image for a translated article with no localised screenshot", () => {
    const out = resolveImages("![Editor](./images/nested/deep.png)", "de", assets);
    expect(out).toBe("![Editor](/assets/deep-c3d4.png)");
  });

  it("leaves an absolute path alone", () => {
    const md = "![Logo](/logo.png)";
    expect(resolveImages(md, "en", assets)).toBe(md);
  });

  it("leaves an external url alone", () => {
    const md = "![X](https://example.com/x.png)";
    expect(resolveImages(md, "en", assets)).toBe(md);
  });

  it("leaves an unknown local image alone so the content gate can flag it", () => {
    const md = "![Missing](./images/nope.png)";
    expect(resolveImages(md, "en", assets)).toBe(md);
  });

  it("rewrites several images in one document", () => {
    const out = resolveImages(
      "![A](./images/formula-editor.png)\n\ntext\n\n![B](./images/nested/deep.png)",
      "en",
      assets,
    );
    expect(out).toContain("/assets/formula-editor-a1b2.png");
    expect(out).toContain("/assets/deep-c3d4.png");
  });

  it("keeps a markdown title after the path", () => {
    const out = resolveImages('![A](./images/formula-editor.png "The editor")', "en", assets);
    expect(out).toBe('![A](/assets/formula-editor-a1b2.png "The editor")');
  });

  it("does not touch ordinary links", () => {
    const md = "[Formulas](/help/formulas)";
    expect(resolveImages(md, "en", assets)).toBe(md);
  });

});

describe("localImageRefs", () => {
  it("lists only the repo-local images a document references", () => {
    expect(localImageRefs("![A](./images/a.png) ![B](/logo.png) ![C](https://x/y.png)")).toEqual([
      "images/a.png",
    ]);
  });

  it("returns nothing for a document with no images", () => {
    expect(localImageRefs("just **text** and a [link](/help/x)")).toEqual([]);
  });
});
