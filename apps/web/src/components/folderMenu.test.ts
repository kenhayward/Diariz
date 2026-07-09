import { describe, it, expect, vi } from "vitest";
import i18n from "../lib/i18n";
import { folderMenu } from "./folderMenu";

const t = i18n.getFixedT("en");

describe("folderMenu", () => {
  it("exposes exactly Rename and Copy link", () => {
    const labels = folderMenu({ onRename: vi.fn(), onCopyLink: vi.fn() }, t).map((a) => a.label);
    expect(labels).toEqual(["Rename", "Copy link"]);
  });

  it("wires each action to its handler", () => {
    const onRename = vi.fn();
    const onCopyLink = vi.fn();
    const menu = folderMenu({ onRename, onCopyLink }, t);
    menu.find((a) => a.label === "Rename")!.onClick();
    menu.find((a) => a.label === "Copy link")!.onClick();
    expect(onRename).toHaveBeenCalledOnce();
    expect(onCopyLink).toHaveBeenCalledOnce();
  });
});
