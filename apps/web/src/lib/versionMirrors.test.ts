import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/// `version.json` is the canonical version, and four other files repeat it. Nothing enforced that until this
/// test, which is how the n8n community node sat at 0.1.0 for roughly seventy releases - published once, in
/// July, and unable to be republished afterwards because npm refuses to overwrite a version. Anyone reading
/// the node's version had no way to tell which API surface it wrapped.
///
/// A mirror drifting is not a cosmetic problem: the API reports its version at `/health`, the desktop
/// installer is named from its own, and the node's is what npm publishes under. They have to agree.
describe("version mirrors", () => {
  const root = resolve(__dirname, "../../../..");
  const read = (p: string) => readFileSync(resolve(root, p), "utf8");
  const packageVersion = (p: string) => (JSON.parse(read(p)) as { version: string }).version;

  const canonical = packageVersion("version.json");

  it("is a three-part version", () => {
    expect(canonical).toMatch(/^\d+\.\d+\.\d+$/);
  });

  it.each([
    ["apps/web/package.json"],
    ["apps/desktop/package.json"],
    // Published to npm under this number, so a stale one cannot be corrected after the fact.
    ["integrations/n8n-nodes-diariz/package.json"],
  ])("%s mirrors version.json", (path) => {
    expect(packageVersion(path)).toBe(canonical);
  });

  it("the API csproj mirrors version.json", () => {
    const csproj = read("src/Diariz.Api/Diariz.Api.csproj");
    expect(csproj).toContain(`<Version>${canonical}</Version>`);
  });

  /// The lock file is a fifth mirror, and it drifted to roughly thirty releases behind before anyone
  /// noticed - it was on 0.211.3 at 0.240.0. Nothing reads it at runtime, so unlike the four above a stale
  /// value breaks nothing; it is caught here because `npm install` rewrites it from package.json whenever
  /// it runs, which turns an unrelated dependency change into a surprise version diff on a PR that never
  /// touched the version. Pinning it makes the bump deliberate rather than a side effect.
  ///
  /// Both copies are checked: npm writes the version at the top level AND in `packages[""]`, and syncing
  /// only one by hand is the obvious way to half-fix this.
  it("the web package-lock mirrors version.json, in both places npm writes it", () => {
    const lock = JSON.parse(read("apps/web/package-lock.json")) as {
      version: string;
      packages: Record<string, { version?: string }>;
    };

    expect(lock.version).toBe(canonical);
    expect(lock.packages[""].version).toBe(canonical);
  });
});
