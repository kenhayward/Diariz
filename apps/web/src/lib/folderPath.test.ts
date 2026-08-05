import { describe, it, expect } from "vitest";
import { collapsePath, type PathCrumb } from "./folderPath";

const crumb = (id: string): PathCrumb => ({ id, name: id.toUpperCase() });
const path = (n: number) => Array.from({ length: n }, (_, i) => crumb(`c${i}`));

describe("collapsePath", () => {
  it("leaves a path that fits untouched, with no ellipsis", () => {
    const p = path(3);
    expect(collapsePath(p, 3)).toEqual(p);
  });

  it("leaves a path shorter than the budget untouched", () => {
    const p = path(2);
    expect(collapsePath(p, 4)).toEqual(p);
  });

  it("keeps the first crumb and the tail, collapsing the middle", () => {
    const p = path(5); // c0 c1 c2 c3 c4
    expect(collapsePath(p, 3)).toEqual([p[0], "ellipsis", p[3], p[4]]);
  });

  it("always keeps the current folder, however tight the budget", () => {
    const p = path(5);
    expect(collapsePath(p, 1)).toEqual([p[4]]);
  });

  it("returns an empty path unchanged", () => {
    expect(collapsePath([], 3)).toEqual([]);
  });

  it("treats a zero or negative budget as one crumb", () => {
    const p = path(4);
    expect(collapsePath(p, 0)).toEqual([p[3]]);
  });
});
