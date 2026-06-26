import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

const setSession = vi.fn();
vi.mock("../auth", () => ({ useAuth: () => ({ setSession }) }));
vi.mock("../lib/api", () => ({
  api: { validateSetup: vi.fn(), setup: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import Setup from "./Setup";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;
const render_ = (search = "?email=new@x.test&token=tok") =>
  render(<MemoryRouter initialEntries={[`/setup${search}`]}><Setup /></MemoryRouter>);

describe("Setup", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows an error for an invalid link", async () => {
    mock(api.validateSetup).mockResolvedValue({ valid: false, email: null });
    render_();
    expect(await screen.findByText(/invalid or has expired/i)).toBeTruthy();
  });

  it("validates, then submits full name + password and signs in", async () => {
    mock(api.validateSetup).mockResolvedValue({ valid: true, email: "new@x.test" });
    mock(api.setup).mockResolvedValue({ accessToken: "jwt", expiresAt: "" });
    render_();

    fireEvent.change(await screen.findByPlaceholderText(/full name/i), { target: { value: "New User" } });
    fireEvent.change(screen.getByPlaceholderText(/^password$/i), { target: { value: "ChangeMe123!" } });
    fireEvent.change(screen.getByPlaceholderText(/confirm password/i), { target: { value: "ChangeMe123!" } });
    fireEvent.click(screen.getByRole("button", { name: /create account/i }));

    await waitFor(() =>
      expect(api.setup).toHaveBeenCalledWith({
        email: "new@x.test", token: "tok", fullName: "New User", password: "ChangeMe123!",
      }),
    );
    expect(setSession).toHaveBeenCalledWith("jwt");
  });

  it("blocks mismatched passwords", async () => {
    mock(api.validateSetup).mockResolvedValue({ valid: true, email: "new@x.test" });
    render_();

    fireEvent.change(await screen.findByPlaceholderText(/full name/i), { target: { value: "New User" } });
    fireEvent.change(screen.getByPlaceholderText(/^password$/i), { target: { value: "ChangeMe123!" } });
    fireEvent.change(screen.getByPlaceholderText(/confirm password/i), { target: { value: "different" } });
    fireEvent.click(screen.getByRole("button", { name: /create account/i }));

    expect(await screen.findByText(/don't match/i)).toBeTruthy();
    expect(api.setup).not.toHaveBeenCalled();
  });
});
