import { describe, it, expect } from "vitest";
import { EPOCHS, ARCHIVED_SPINE } from "./epochs";
import { ARCHIVE } from "./archive";
import { RECENT } from "./current";

/// Epochs are the default view of the release-notes page: ~30 named spans standing in for ~470
/// individual releases. The whole design rests on them **tiling** the archive - every archived release
/// belonging to exactly one epoch, with no gaps and no overlaps - because a release that falls between
/// two epochs is unreachable from the page entirely. It does not 404 or look broken; it is simply never
/// listed, which is the kind of loss nobody notices.
///
/// So the tiling is asserted as one equality over the whole archive rather than as per-epoch checks.
/// Per-epoch checks are the trap here: each epoch can be individually well-formed while the set of them
/// leaves a hole.
const indexOfVersion = (version: string) => ARCHIVE.findIndex((r) => r.version === version);

describe("release epochs", () => {
  it("names a real archived release at both ends of every range", () => {
    const dangling = EPOCHS.filter((e) => indexOfVersion(e.from) === -1 || indexOfVersion(e.to) === -1);
    expect(dangling.map((e) => e.id)).toEqual([]);
  });

  it("tiles the archive exactly: every release in one epoch, no gaps, no overlaps", () => {
    // ARCHIVE is newest first, so an epoch's `to` (its newest release) sits at the lower index.
    const tiled = EPOCHS.flatMap((e) => ARCHIVE.slice(indexOfVersion(e.to), indexOfVersion(e.from) + 1));

    expect(tiled.map((r) => r.version)).toEqual(ARCHIVE.map((r) => r.version));
  });

  it("runs newest first, starting where the current list leaves off", () => {
    // The open epoch is whatever is in current.ts, so the newest closed epoch must end exactly at the
    // top of the archive - otherwise releases fall into the gap between "current" and "epoch 1".
    expect(EPOCHS[0].to).toBe(ARCHIVE[0].version);
    expect(EPOCHS[EPOCHS.length - 1].from).toBe(ARCHIVE[ARCHIVE.length - 1].version);
    expect(RECENT[RECENT.length - 1].date >= ARCHIVE[0].date).toBe(true);
  });

  it("gives every epoch a unique, URL-safe id", () => {
    const ids = EPOCHS.map((e) => e.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids.filter((id) => !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(id))).toEqual([]);
  });

  it("gives every epoch a title and a summary", () => {
    const empty = EPOCHS.filter((e) => e.title.trim().length === 0 || e.summary.trim().length === 0);
    expect(empty.map((e) => e.id)).toEqual([]);
  });

  it("mirrors the archive in the spine the epoch list reads instead of the archive", () => {
    // The epoch cards need each archived release's version and date to derive their span and count, but
    // loading the archive to get them would defeat the split. The spine is that data, and the only
    // stored derivation of something the archive already knows - so it is pinned to it exactly.
    expect(ARCHIVED_SPINE).toEqual(ARCHIVE.map((r) => ({ version: r.version, date: r.date })));
  });
});
