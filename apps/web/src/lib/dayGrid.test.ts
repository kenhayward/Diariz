import { describe, it, expect } from "vitest";
import {
  HOUR_HEIGHT, MIN_BLOCK_HEIGHT, BADGED_MIN_HEIGHT, layoutDay, hourToPx, initialScrollTop,
  type DayGridInput,
} from "./dayGrid";

/// One item on the axis. Hours are fractional local hours, as `dayItemSpan` returns them.
const at = (key: string, startHour: number, endHour: number, over: Partial<DayGridInput> = {}): DayGridInput => ({
  key, startHour, endHour, ...over,
});

const byKey = (layout: { blocks: { item: DayGridInput }[] }) => layout.blocks.map((b) => b.item.key);
const block = (layout: { blocks: { item: DayGridInput }[] }, key: string) =>
  // eslint-disable-next-line @typescript-eslint/no-non-null-assertion
  layout.blocks.find((b) => b.item.key === key)!;

describe("layoutDay geometry", () => {
  it("places an item at its hour and sizes it by its length", () => {
    const layout = layoutDay([at("a", 10, 10.5)]);
    const b = block(layout, "a");
    expect(b.top).toBe((10 - 6) * HOUR_HEIGHT); // 176
    expect(b.height).toBe(0.5 * HOUR_HEIGHT); // 22
  });

  it("spans 06:00-23:00 by default, one rule per hour", () => {
    const layout = layoutDay([at("a", 10, 10.5)]);
    expect(layout.startHour).toBe(6);
    expect(layout.endHour).toBe(23);
    expect(layout.hours).toEqual([6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22]);
    expect(layout.totalHeight).toBe(17 * HOUR_HEIGHT); // 748
  });

  it("still renders the axis on an empty day", () => {
    const layout = layoutDay([]);
    expect(layout.blocks).toEqual([]);
    expect(layout.hours).toHaveLength(17);
    expect(layout.totalHeight).toBe(748);
  });
});

describe("layoutDay block floors", () => {
  it("floors a very short item so it stays clickable, and collapses it to one line", () => {
    const b = block(layoutDay([at("tiny", 10, 10 + 9 / 3600)]), "tiny"); // a 9-second recording
    expect(b.height).toBe(MIN_BLOCK_HEIGHT);
    expect(b.tall).toBe(false);
  });

  // A status pill must never be the thing that gets dropped, so a badged item asks for the taller floor -
  // which puts it over the two-line threshold by construction.
  it("keeps a badged item tall enough for its second line", () => {
    const b = block(layoutDay([at("badged", 10, 10.01, { minHeight: BADGED_MIN_HEIGHT })]), "badged");
    expect(b.height).toBe(BADGED_MIN_HEIGHT);
    expect(b.tall).toBe(true);
  });
});

describe("layoutDay window", () => {
  it("extends the start to cover an early item", () => {
    const layout = layoutDay([at("early", 5.25, 6)]);
    expect(layout.startHour).toBe(5);
    expect(layout.endHour).toBe(23);
    expect(layout.totalHeight).toBe(18 * HOUR_HEIGHT); // 792
  });

  it("extends the end to cover a late item", () => {
    const layout = layoutDay([at("late", 23.5, 24)]);
    expect(layout.startHour).toBe(6);
    expect(layout.endHour).toBe(24);
  });

  // The contract that removes the "invisible meeting" case entirely: the window grows to fit, so nothing
  // can ever sit outside it.
  it("keeps the last pixel of a minute-long item at 23:50 inside the grid", () => {
    const layout = layoutDay([at("last", 23 + 50 / 60, 23 + 51 / 60)]);
    const b = block(layout, "last");
    expect(layout.totalHeight).toBeGreaterThanOrEqual(b.top + b.height);
  });

  it("clamps an item running past midnight and flags the cut edge", () => {
    const b = block(layoutDay([at("night", 23.5, 24.5)]), "night");
    expect(b.clippedEnd).toBe(true);
    expect(b.clippedStart).toBe(false);
    expect(b.top + b.height).toBeLessThanOrEqual(layoutDay([at("night", 23.5, 24.5)]).totalHeight);
  });
});

describe("layoutDay columns", () => {
  it("gives an item with nothing beside it the full width", () => {
    const layout = layoutDay([at("a", 9, 10), at("b", 14, 15)]);
    for (const b of layout.blocks) {
      expect(b.leftPct).toBe(0);
      expect(b.widthPct).toBe(100);
      expect(b.columnCount).toBe(1);
    }
  });

  it("splits two overlapping items down the middle", () => {
    const layout = layoutDay([at("a", 10, 11), at("b", 10.5, 11.5)]);
    expect(block(layout, "a")).toMatchObject({ leftPct: 0, widthPct: 50, columnIndex: 0, columnCount: 2 });
    expect(block(layout, "b")).toMatchObject({ leftPct: 50, widthPct: 50, columnIndex: 1, columnCount: 2 });
  });

  // Three items in one cluster do NOT mean three columns: the third starts after the first has finished, so
  // it reuses that column. Pinning this stops the naive "n items => n columns" shredding of the grid.
  it("reuses a free column rather than opening a new one", () => {
    const layout = layoutDay([at("a", 9, 10), at("b", 9.5, 10.5), at("c", 10.25, 11)]);
    expect(layout.blocks.every((b) => b.columnCount === 2)).toBe(true);
    expect(block(layout, "c").columnIndex).toBe(0);
  });

  // Two items twenty seconds apart do not overlap in *time*, but their 20px floors sit on top of each
  // other. Overlap is therefore tested against the height a block actually occupies.
  it("treats two floored blocks that would visually collide as overlapping", () => {
    const layout = layoutDay([at("rec", 10, 10 + 9 / 3600), at("mtg", 10 + 20 / 3600, 10 + 40 / 3600)]);
    expect(layout.blocks.every((b) => b.columnCount === 2)).toBe(true);
  });

  it("caps a cluster at three columns and stacks the rest behind a chip", () => {
    const layout = layoutDay([at("a", 10, 12), at("b", 10.1, 12), at("c", 10.2, 12), at("d", 10.3, 12)]);
    expect(byKey(layout)).toEqual(["a", "b", "c"]);
    expect(layout.blocks.every((b) => b.columnCount === 3)).toBe(true);
    expect(layout.overflow).toHaveLength(1);
    expect(layout.overflow[0].count).toBe(1);
    expect(layout.overflow[0].items.map((i) => i.key)).toEqual(["d"]);
  });
});

describe("layoutDay ordering", () => {
  // The blocks array IS the DOM order, and therefore the keyboard focus order. It must be time order
  // whatever order the caller happened to hand the items over in, and must not be re-sorted by column.
  it("returns blocks in time order regardless of input order", () => {
    const items = [at("noon", 12, 13), at("dawn", 7, 8), at("dusk", 18, 19)];
    expect(byKey(layoutDay(items))).toEqual(["dawn", "noon", "dusk"]);
    expect(byKey(layoutDay([...items].reverse()))).toEqual(["dawn", "noon", "dusk"]);
  });

  it("puts the longer of two items starting together first, so the longest sits leftmost", () => {
    expect(byKey(layoutDay([at("short", 10, 10.5), at("long", 10, 12)]))).toEqual(["long", "short"]);
  });
});

describe("layoutDay all-day items", () => {
  it("lifts an all-day item off the axis without widening the window", () => {
    const layout = layoutDay([at("holiday", 0, 24, { allDay: true }), at("mtg", 10, 11)]);
    expect(layout.allDay.map((i) => i.key)).toEqual(["holiday"]);
    expect(byKey(layout)).toEqual(["mtg"]);
    expect(layout.startHour).toBe(6);
    expect(layout.endHour).toBe(23);
  });
});

describe("hourToPx", () => {
  it("measures from the window's start, not from midnight", () => {
    expect(hourToPx(8, { startHour: 6 })).toBe(88);
    expect(hourToPx(8, { startHour: 5 })).toBe(132);
  });
});

describe("initialScrollTop", () => {
  const layout = layoutDay([at("a", 10, 11)]);

  it("opens at 08:00 on a day that is not today", () => {
    expect(initialScrollTop(layout, null)).toBe(88);
  });

  it("opens an hour before now when the day is today", () => {
    expect(initialScrollTop(layout, 14 + 20 / 60)).toBeCloseTo((13 + 20 / 60 - 6) * HOUR_HEIGHT, 5);
  });

  it("never opens earlier than 08:00, however early it is", () => {
    expect(initialScrollTop(layout, 7)).toBe(88);
  });

  it("measures from an extended window's start", () => {
    expect(initialScrollTop(layoutDay([at("early", 5.25, 6)]), null)).toBe(132);
  });

  it("never returns a negative offset", () => {
    expect(initialScrollTop(layoutDay([at("late", 23.5, 24)]), 0)).toBeGreaterThanOrEqual(0);
  });
});
