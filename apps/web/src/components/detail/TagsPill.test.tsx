import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import TagsPill from "./TagsPill";

describe("TagsPill", () => {
  it("shows the tag count", () => {
    render(<TagsPill count={3} tags={["a", "b", "c"]} open={false} onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill).toHaveTextContent("3");
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
    expect(screen.getByRole("button", { name: "Tags" })).toHaveAttribute(
      "title",
      "one · two · three · four",
    );
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
    expect(screen.getByRole("button", { name: "Tags" })).toHaveAttribute(
      "title",
      "one · two · three · four · +2 more",
    );
  });

  it("invites a first tag when there are none", () => {
    render(<TagsPill count={0} tags={[]} open={false} onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill).toHaveAttribute("title", "No tags yet - click to add");
    expect(pill).toHaveTextContent("0");
  });

  it("reports its popover state and toggles on click", async () => {
    const onToggle = vi.fn();
    render(<TagsPill count={0} tags={[]} open={false} onToggle={onToggle} />);
    const pill = screen.getByRole("button", { name: "Tags" });

    expect(pill).toHaveAttribute("aria-expanded", "false");
    expect(pill).toHaveAttribute("aria-haspopup", "dialog");

    await userEvent.click(pill);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it("marks itself expanded while the popover is open", () => {
    render(<TagsPill count={1} tags={["a"]} open onToggle={() => {}} />);
    expect(screen.getByRole("button", { name: "Tags" })).toHaveAttribute("aria-expanded", "true");
  });
});
