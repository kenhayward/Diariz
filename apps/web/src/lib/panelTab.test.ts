import { describe, it, expect, beforeEach } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { usePanelTab, setPanelTab, PANEL_TAB_KEY } from "./panelTab";

describe("panelTab", () => {
  beforeEach(() => localStorage.clear());

  it("starts on the list tab", () => {
    const { result } = renderHook(() => usePanelTab());
    expect(result.current).toBe("list");
  });

  it("reads a persisted tab", () => {
    localStorage.setItem(PANEL_TAB_KEY, "calendar");
    const { result } = renderHook(() => usePanelTab());
    expect(result.current).toBe("calendar");
  });

  it("falls back to list for a value that is not a tab", () => {
    localStorage.setItem(PANEL_TAB_KEY, "transcript");
    const { result } = renderHook(() => usePanelTab());
    expect(result.current).toBe("list");
  });

  /// The whole point of the store: the recordings panel owns the tab strip, but the recording detail page
  /// (a sibling, not a child) has to be able to pull the panel back to the list when a folder chip is
  /// clicked. A plain useState seeded from localStorage cannot see that write.
  it("re-renders every subscriber when another component sets the tab", () => {
    localStorage.setItem(PANEL_TAB_KEY, "actions");
    const { result } = renderHook(() => usePanelTab());
    expect(result.current).toBe("actions");

    act(() => setPanelTab("list"));

    expect(result.current).toBe("list");
  });

  it("persists what it is set to", () => {
    act(() => setPanelTab("tags"));
    expect(localStorage.getItem(PANEL_TAB_KEY)).toBe("tags");
  });
});
