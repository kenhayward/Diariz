const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");

/// Config guards, in the spirit of electronStore.test.js: assertions about how the app is *packaged*, which no
/// behavioural test would catch and which only fail on a user's machine.

const root = path.join(__dirname, "..");
const config = require("../electron-builder.config.js");
const pkg = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));

test("the Outlook reader is shipped as a resource", () => {
  const resources = config.win?.extraResources ?? [];
  const reader = resources.find((r) => String(r.from).includes("native/publish"));
  assert.ok(reader, "win.extraResources must carry the published reader");
  assert.equal(reader.to, "outlook", "outlookHost.js looks for it under resources/outlook");
  assert.ok(
    (reader.filter ?? []).some((f) => f.includes("Diariz.OutlookReader.exe")),
    "only the exe is needed - not the pdb or the rest of the publish folder",
  );
});

test("the Windows installer stays x64", () => {
  const targets = config.win?.target ?? [];
  const nsis = targets.find((t) => (typeof t === "string" ? t : t.target) === "nsis");
  assert.ok(nsis, "the Windows target is NSIS");
  assert.ok(typeof nsis !== "string", "the target must pin an arch, not just name nsis");
  // The reader is published win-x64. An arm64 installer would package a reader that cannot run, so the app
  // would look installed and simply never find Outlook.
  assert.deepEqual(nsis.arch, ["x64"]);
});

test("packaging always rebuilds the reader first", () => {
  // Otherwise `dist` happily ships whatever stale exe (or none) happens to be in native/publish.
  for (const script of ["dist", "publish"]) {
    assert.match(pkg.scripts[script], /build:outlook/, `${script} must publish the reader first`);
  }
  assert.match(pkg.scripts["build:outlook"], /dotnet publish/);
});

/// The csproj as written. Its comments deliberately describe these decisions in prose rather than naming the
/// XML elements, so a plain match here cannot be fooled by a comment - which it was on this test's first run,
/// before the comment was reworded. That is cheaper and more honest than stripping comments with a regex.
function readerCsproj() {
  return fs.readFileSync(
    path.join(root, "native", "Diariz.OutlookReader", "Diariz.OutlookReader.csproj"),
    "utf8",
  );
}

test("the reader is not a fifth version mirror", () => {
  // versionMirrors.test.ts pins version.json against four package manifests. The helper is never published to
  // a registry, so giving it a version would be one more thing to keep in lockstep for no benefit.
  assert.doesNotMatch(readerCsproj(), /<Version>/, "the reader csproj must not declare a version");
});

test("the reader keeps trimming off", () => {
  // Late-bound COM is entirely reflection; a trimmed build would strip exactly what it calls, and would fail
  // only at runtime on a user's PC.
  const csproj = readerCsproj();
  assert.match(csproj, /<PublishTrimmed>false<\/PublishTrimmed>/);
  assert.match(csproj, /<SelfContained>true<\/SelfContained>/, "a user must not need a .NET runtime installed");
});

/// The reader has no C# test project (it is deliberately outside Diariz.slnx, which CI builds on Linux), so
/// the one rule that has to hold at the source level is guarded here - in the same spirit as the csproj checks
/// above.
test("the reader checks the registry before it ever activates Outlook", () => {
  const program = fs.readFileSync(
    path.join(root, "native", "Diariz.OutlookReader", "Program.cs"),
    "utf8",
  );
  const read = program.slice(program.indexOf("private static ReadResult Read("));
  const probe = read.indexOf("OutlookPresence.Detect()");
  const activate = read.indexOf("CreateApplication()");

  assert.ok(probe >= 0, "Read must ask OutlookPresence whether classic Outlook is installed");
  assert.ok(activate >= 0, "Read still creates the COM object once it knows Outlook is there");
  // On a PC with Office but no Outlook, activating the class makes Windows offer to install it - a dialog the
  // user got on every launch, because a sync runs at startup. The registry answers the question for free.
  assert.ok(probe < activate, "the registry probe must come first, or the install prompt comes back");
});

test("the shell still has no native dependencies", () => {
  // The whole reason the reader is a separate process. A native module here would reintroduce per-Electron
  // rebuilds, arch-specific packaging, and the ABI breakage that ruled out an in-process COM binding.
  const deps = Object.keys(pkg.dependencies ?? {});
  assert.deepEqual(deps.sort(), ["electron-store", "electron-updater"]);
});
