import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import Avatar from "./Avatar";

describe("Avatar", () => {
  it("shows initials when no picture is provided", () => {
    const { container } = render(<Avatar initials="AL" />);
    expect(container.textContent).toBe("AL");
    expect(container.querySelector("img")).toBeNull();
  });

  it("renders the profile picture when a URL is provided", () => {
    render(<Avatar initials="AL" pictureUrl="https://pic/a.png" />);
    const img = screen.getByRole("presentation") as HTMLImageElement;
    expect(img.getAttribute("src")).toBe("https://pic/a.png");
  });

  it("falls back to initials if the image fails to load", () => {
    const { container } = render(<Avatar initials="AL" pictureUrl="https://pic/broken.png" />);
    fireEvent.error(container.querySelector("img")!);
    expect(container.textContent).toBe("AL");
    expect(container.querySelector("img")).toBeNull();
  });
});
