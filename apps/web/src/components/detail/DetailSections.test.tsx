import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import DetailSections, { type DetailSection } from "./DetailSections";

const sections: DetailSection[] = [
  { key: "transcript", label: "Transcript", toolbar: <button type="button">Merge rows</button>, meta: "142 segments", content: <p>the transcript body</p> },
  { key: "speakers", label: "Speakers", content: <p>the speakers body</p> },
];

const renderAt = (active: DetailSection["key"], onSelect = vi.fn()) => {
  render(<DetailSections sections={sections} active={active} onSelect={onSelect} hub={<p>the hub</p>} />);
  return onSelect;
};

describe("DetailSections", () => {
  it("shows the hub as the landing view", () => {
    renderAt("hub");
    expect(screen.getByText("the hub")).toBeTruthy();
    expect(screen.queryByText("the transcript body")).toBeNull();
  });

  it("shows the drilled-in section, its toolbar, and its breadcrumb meta", () => {
    renderAt("transcript");
    expect(screen.getByText("the transcript body")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Merge rows" })).toBeTruthy();
    expect(screen.getByText("142 segments")).toBeTruthy();
  });

  it("renders only the active section, so an off-screen section's effects never mount", () => {
    renderAt("transcript");
    expect(screen.queryByText("the speakers body")).toBeNull();
    expect(screen.queryByText("the hub")).toBeNull();
  });

  it("goes back to the hub from the breadcrumb", () => {
    const onSelect = renderAt("transcript");
    fireEvent.click(screen.getByRole("button", { name: /Overview/ }));
    expect(onSelect).toHaveBeenCalledWith("hub");
  });

  it("falls back to the hub rather than blanking when the active section isn't on offer", () => {
    render(
      <DetailSections sections={[]} active="formulas" onSelect={vi.fn()} hub={<p>the hub</p>} />,
    );
    expect(screen.getByText("the hub")).toBeTruthy();
  });
});
