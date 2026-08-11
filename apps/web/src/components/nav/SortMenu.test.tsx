import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import SortMenu from "./SortMenu";
import type { ListSort } from "../../lib/listSort";

function renderMenu(sort: ListSort) {
  const onChange = vi.fn();
  render(<SortMenu sort={sort} onChange={onChange} />);
  return onChange;
}

describe("SortMenu", () => {
  it("offers manual, date, name and duration", () => {
    renderMenu({ key: "manual", dir: "asc" });
    const select = screen.getByRole("combobox", { name: /sort by/i }) as HTMLSelectElement;
    expect([...select.options].map((o) => o.value)).toEqual(["manual", "date", "name", "duration"]);
  });

  it("shows the current key as the selection", () => {
    renderMenu({ key: "duration", dir: "asc" });
    expect((screen.getByRole("combobox", { name: /sort by/i }) as HTMLSelectElement).value).toBe("duration");
  });

  it("reports a key change, keeping the direction", () => {
    const onChange = renderMenu({ key: "manual", dir: "desc" });
    fireEvent.change(screen.getByRole("combobox", { name: /sort by/i }), { target: { value: "name" } });
    expect(onChange).toHaveBeenCalledWith({ key: "name", dir: "desc" });
  });

  /// There is no direction for "the order you arranged them in", and a live control that changes nothing
  /// is worse than an absent one.
  it("hides the direction toggle under manual", () => {
    renderMenu({ key: "manual", dir: "asc" });
    expect(screen.queryByRole("button", { name: /ascending|descending/i })).toBeNull();
  });

  it("shows the direction toggle once a key is chosen", () => {
    renderMenu({ key: "name", dir: "asc" });
    expect(screen.getByRole("button", { name: /ascending/i })).toBeTruthy();
  });

  it("flips the direction, keeping the key", () => {
    const onChange = renderMenu({ key: "name", dir: "asc" });
    fireEvent.click(screen.getByRole("button", { name: /ascending/i }));
    expect(onChange).toHaveBeenCalledWith({ key: "name", dir: "desc" });
  });

  it("flips back from descending", () => {
    const onChange = renderMenu({ key: "date", dir: "desc" });
    fireEvent.click(screen.getByRole("button", { name: /descending/i }));
    expect(onChange).toHaveBeenCalledWith({ key: "date", dir: "asc" });
  });
});
