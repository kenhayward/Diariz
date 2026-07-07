import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import GoogleSignInButton from "./GoogleSignInButton";

describe("GoogleSignInButton", () => {
  it("renders an anchor to the server start endpoint by default (web)", () => {
    render(<GoogleSignInButton label="Sign in with Google" />);
    const link = screen.getByRole("link", { name: /sign in with google/i });
    expect(link.getAttribute("href")).toBe("/api/auth/google/start");
  });

  it("renders a button that calls onClick when provided (desktop)", () => {
    const onClick = vi.fn();
    render(<GoogleSignInButton label="Sign in with Google" onClick={onClick} />);
    expect(screen.queryByRole("link")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /sign in with google/i }));
    expect(onClick).toHaveBeenCalledOnce();
  });
});
