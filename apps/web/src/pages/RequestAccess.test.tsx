import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { requestAccess: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RequestAccess from "./RequestAccess";

const render_ = () => render(<MemoryRouter><RequestAccess /></MemoryRouter>);
const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

describe("RequestAccess", () => {
  beforeEach(() => vi.clearAllMocks());

  it("submits the email and shows the neutral confirmation", async () => {
    mock(api.requestAccess).mockResolvedValue(undefined);
    render_();

    fireEvent.change(screen.getByPlaceholderText(/your name/i), { target: { value: "New Person" } });
    fireEvent.change(screen.getByPlaceholderText(/your email/i), { target: { value: "new@x.test" } });
    fireEvent.click(screen.getByRole("button", { name: /request access/i }));

    await waitFor(() => expect(api.requestAccess).toHaveBeenCalledWith("new@x.test", "New Person"));
    expect(await screen.findByText(/an administrator will review/i)).toBeTruthy();
  });
});
