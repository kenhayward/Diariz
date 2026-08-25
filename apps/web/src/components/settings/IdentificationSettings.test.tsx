import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import IdentificationSettings from "./IdentificationSettings";
import { api } from "../../lib/api";

vi.mock("../../lib/api", () => ({
  api: { rescanIdentification: vi.fn() },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function setup(setThreshold = vi.fn()) {
  render(
    <IdentificationSettings
      threshold="0.3"
      setThreshold={setThreshold}
      band="0.4"
      setBand={vi.fn()}
      margin="0.05"
      setMargin={vi.fn()}
      minSpeechMs="3000"
      setMinSpeechMs={vi.fn()}
    />,
  );
  return setThreshold;
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.rescanIdentification).mockResolvedValue({ scanned: 500, applied: 38, suggested: 90 });
});

describe("IdentificationSettings", () => {
  it("reports edits upward rather than holding its own copy", async () => {
    // The settings endpoint takes the whole object, so a panel keeping its own state would overwrite
    // whatever else the form had unsaved.
    const setThreshold = setup();

    await userEvent.clear(screen.getByLabelText(/Accept a match at/));
    await userEvent.type(screen.getByLabelText(/Accept a match at/), "0.35");

    expect(setThreshold).toHaveBeenCalled();
  });

  it("previews before offering to apply", async () => {
    // Naming people across a whole library without being told the number first is not a decision anyone
    // can make.
    setup();
    expect(screen.queryByRole("button", { name: /Apply/ })).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: /Preview/ }));

    await waitFor(() => expect(screen.getByText(/38/)).toBeTruthy());
    expect(screen.getByText(/90/)).toBeTruthy();
    expect(screen.getByRole("button", { name: /Apply/ })).toBeTruthy();
  });

  it("previews without writing anything", async () => {
    setup();

    await userEvent.click(screen.getByRole("button", { name: /Preview/ }));

    await waitFor(() => expect(api.rescanIdentification).toHaveBeenCalledWith(true));
  });

  it("applies for real once confirmed", async () => {
    setup();
    await userEvent.click(screen.getByRole("button", { name: /Preview/ }));
    await screen.findByRole("button", { name: /Apply/ });

    await userEvent.click(screen.getByRole("button", { name: /Apply/ }));

    await waitFor(() => expect(api.rescanIdentification).toHaveBeenCalledWith(false));
  });

  it("withdraws the apply button once the run is done", async () => {
    // Leaving it there invites a second run that would report zero and read as a failure.
    setup();
    await userEvent.click(screen.getByRole("button", { name: /Preview/ }));
    await userEvent.click(await screen.findByRole("button", { name: /Apply/ }));

    await waitFor(() => expect(screen.queryByRole("button", { name: /Apply/ })).toBeNull());
  });

  it("reports a failed re-scan instead of a silent nothing", async () => {
    mock(api.rescanIdentification).mockRejectedValue(new Error("boom"));
    setup();

    await userEvent.click(screen.getByRole("button", { name: /Preview/ }));

    await waitFor(() => expect(screen.getByText(/Could not re-scan/)).toBeTruthy());
  });
});
