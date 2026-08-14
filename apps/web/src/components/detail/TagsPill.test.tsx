import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import TagsPill from "./TagsPill";

describe("TagsPill", () => {
  it("shows the tag count", () => {
    render(<TagsPill count={3} tags={["a", "b", "c"]} open={false} onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.textContent).toContain("3");
  });

  it("names the first four tags in its hover text", () => {
    render(
      <TagsPill
        count={4}
        tags={["one", "two", "three", "four"]}
        open={false}
        onToggle={() => {}}
      />,
    );
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.getAttribute("title")).toBe("one · two · three · four");
  });

  it("summarises the rest when there are more than four", () => {
    render(
      <TagsPill
        count={6}
        tags={["one", "two", "three", "four", "five", "six"]}
        open={false}
        onToggle={() => {}}
      />,
    );
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.getAttribute("title")).toBe("one · two · three · four · +2 more");
  });

  it("invites a first tag when there are none", () => {
    render(<TagsPill count={0} tags={[]} open={false} onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.getAttribute("title")).toBe("No tags yet - click to add");
    expect(pill.textContent).toContain("0");
  });

  it("reports its popover state and toggles on click", async () => {
    const onToggle = vi.fn();
    render(<TagsPill count={0} tags={[]} open={false} onToggle={onToggle} />);
    const pill = screen.getByRole("button", { name: "Tags" });

    expect(pill.getAttribute("aria-expanded")).toBe("false");
    expect(pill.getAttribute("aria-haspopup")).toBe("dialog");

    await userEvent.click(pill);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it("marks itself expanded while the popover is open", () => {
    render(<TagsPill count={1} tags={["a"]} open onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill.getAttribute("aria-expanded")).toBe("true");
  });
});
