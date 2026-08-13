/**
 * The Linux "system audio as a microphone" PipeWire drop-in is shipped as a static asset so the help
 * article can tell users to download it rather than paste a wall of config, and so the .deb for managed
 * machines has a single source of truth to package. That makes the file itself a deliverable, and these
 * assertions pin the properties it has to have to work at all.
 *
 * Every one of them was verified on real hardware (Ryzen AI Max+ 395, PipeWire 1.6.2): with this file as
 * the only mechanism, a 440 Hz tone played to the speakers came back from the published source at exactly
 * 440.0 Hz, and a silence control recorded RMS 0.0 / peak 0.
 */
import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const ASSET = join(__dirname, "..", "..", "public", "linux", "99-diariz-system-audio.conf");

describe("Linux system-audio PipeWire drop-in", () => {
  it("is shipped as a static asset the app can serve", () => {
    expect(existsSync(ASSET)).toBe(true);
  });

  const conf = () => readFileSync(ASSET, "utf8");

  it("loads the loopback module", () => {
    expect(conf()).toContain("libpipewire-module-loopback");
  });

  it("captures the sink monitor rather than an input", () => {
    // Without this the loopback would record the microphone, which is the default source.
    expect(conf()).toContain("stream.capture.sink = true");
  });

  it("follows the default sink so it survives switching output device", () => {
    // A hard-coded node.target breaks the moment the user plugs in headphones or HDMI.
    expect(conf()).toContain("@DEFAULT_AUDIO_SINK@");
  });

  it("publishes a real Audio/Source, which is what makes it selectable as a microphone", () => {
    // PipeWire does not expose a sink's monitor as a standalone node, so browsers list nothing without
    // this - it is the whole reason the file exists.
    expect(conf()).toContain('media.class      = "Audio/Source"');
  });

  it("names the device distinctly, since every app on the machine will list it", () => {
    // Deployed fleet-wide this appears in Zoom/Teams/Slack too; an unqualified "System Audio" invites
    // someone to select it by accident and broadcast their music.
    expect(conf()).toContain('node.description = "System Audio (Diariz)"');
    expect(conf()).toContain('node.name        = "diariz_system_audio"');
  });

  it("warns about echo cancellation, which silently destroys this capture", () => {
    expect(conf().toLowerCase()).toContain("echo cancellation");
  });

  it("documents both install locations, since the same file serves per-user and fleet installs", () => {
    expect(conf()).toContain("~/.config/pipewire/pipewire.conf.d/");
    expect(conf()).toContain("/etc/pipewire/pipewire.conf.d/");
  });

  // Drift guard rather than a behaviour test: the .deb for managed machines must package THIS file, not a
  // copy that quietly ages. If the packaging is repointed elsewhere, the two install routes would start
  // shipping different configuration and only one of them would be tested.
  it("is the file the .deb packages", () => {
    const script = readFileSync(
      join(__dirname, "..", "..", "..", "..", "packaging", "linux", "build-deb.sh"),
      "utf8",
    );
    expect(script).toContain("apps/web/public/linux/99-diariz-system-audio.conf");
    expect(script).toContain("/etc/pipewire/pipewire.conf.d");
  });
});
