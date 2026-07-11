import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import LinkifiedText from "./LinkifiedText";

describe("LinkifiedText", () => {
  it("turns an embedded http(s) URL into a new-tab link and keeps the surrounding text", () => {
    render(
      <LinkifiedText text="Join here https://3ds.zoom.us/j/83845281617?pwd=VOSGb6qHqxH2zJqonR9v45l9sJanSp.1 before 3pm" />,
    );
    const link = screen.getByRole("link", { name: /zoom\.us\/j\/83845281617/ });
    expect(link.getAttribute("href")).toBe(
      "https://3ds.zoom.us/j/83845281617?pwd=VOSGb6qHqxH2zJqonR9v45l9sJanSp.1",
    );
    expect(link.getAttribute("target")).toBe("_blank");
    expect(link.getAttribute("rel")).toBe("noopener noreferrer");
    // Surrounding words survive as text.
    expect(screen.getByText(/Join here/)).toBeTruthy();
    expect(screen.getByText(/before 3pm/)).toBeTruthy();
  });

  it("does not include a trailing sentence period in the link", () => {
    render(<LinkifiedText text="See https://example.com/page." />);
    expect(screen.getByRole("link").getAttribute("href")).toBe("https://example.com/page");
  });

  it("renders plain text with no links when there is no URL", () => {
    render(<LinkifiedText text="No links in this description." />);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByText("No links in this description.")).toBeTruthy();
  });

  it("links multiple URLs", () => {
    render(<LinkifiedText text="https://a.example and https://b.example" />);
    expect(screen.getAllByRole("link")).toHaveLength(2);
  });
});
