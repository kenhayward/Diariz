import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import PersonEditor from "./PersonEditor";
import { api } from "../lib/api";
import type { Person } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    updatePerson: vi.fn(),
    deletePerson: vi.fn(),
    deleteVoiceprint: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function person(over: Partial<Person> = {}): Person {
  return {
    id: "p1", name: "Ada Lovelace", title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: true, sampleCount: 2,
    linkedUserId: null, isSelf: false, canManageBiometrics: true,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z", ...over,
  };
}

function setup(p: Person, canManagePeople = true) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <PersonEditor person={p} canManagePeople={canManagePeople} onClose={() => {}} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.updatePerson).mockResolvedValue(undefined);
  mock(api.deleteVoiceprint).mockResolvedValue(undefined);
  mock(api.deletePerson).mockResolvedValue(undefined);
});

describe("PersonEditor", () => {
  it("saves the contact fields", async () => {
    setup(person());

    fireEvent.change(screen.getByLabelText("Job title"), { target: { value: "Engineer" } });
    fireEvent.change(screen.getByLabelText("Company"), { target: { value: "Analytical Engines" } });
    fireEvent.change(screen.getByLabelText("Email address"), { target: { value: "ada@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(api.updatePerson).toHaveBeenCalledWith("p1", expect.objectContaining({
        title: "Engineer", companyName: "Analytical Engines", email: "ada@example.com",
      })));
  });

  it("refuses an empty name", async () => {
    setup(person());

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(/name is required/i)).toBeTruthy();
    expect(api.updatePerson).not.toHaveBeenCalled();
  });

  it("refuses an email with no @", async () => {
    setup(person());

    fireEvent.change(screen.getByLabelText("Email address"), { target: { value: "not-an-email" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(/does not look like an email/i)).toBeTruthy();
    expect(api.updatePerson).not.toHaveBeenCalled();
  });

  /// Name and email follow the account for a linked person, so editing them here would be undone by the
  /// next sync - the field is disabled and says why rather than failing on save.
  it("locks name and email for a person linked to an account", () => {
    setup(person({ linkedUserId: "u1" }));

    expect((screen.getByLabelText("Name") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText("Email address") as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText(/come from their account/i)).toBeTruthy();
  });

  // ---- The biometric gate. Asymmetric on purpose; easy to "tidy" into one branch later. ----

  it("shows the opt-out state even when the viewer cannot change it", () => {
    setup(person({ voiceprintOptOut: true, canManageBiometrics: false }));

    const box = screen.getByLabelText(/opted out of voice-printing/i) as HTMLInputElement;
    // Rendered AND checked AND disabled: a viewer needs to see that someone opted out even when they
    // cannot change it. Hiding it would make an opted-out person look like an ordinary one.
    expect(box.checked).toBe(true);
    expect(box.disabled).toBe(true);
  });

  it("does nothing when the disabled opt-out is clicked", () => {
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    setup(person({ canManageBiometrics: false }));

    fireEvent.click(screen.getByLabelText(/opted out of voice-printing/i));

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(api.updatePerson).not.toHaveBeenCalled();
    confirmSpy.mockRestore();
  });

  it("hides erase-voiceprint entirely without the permission", () => {
    setup(person({ canManageBiometrics: false }));

    // Hidden rather than disabled: an action you cannot perform should not advertise itself, whereas a
    // state you cannot change should still be visible. That difference is deliberate.
    expect(screen.queryByRole("button", { name: /erase voiceprint/i })).toBeNull();
  });

  it("offers erase-voiceprint with the permission", () => {
    setup(person());

    expect(screen.getByRole("button", { name: /erase voiceprint/i })).toBeTruthy();
  });

  it("confirms before opting someone out, and cancelling changes nothing", () => {
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(false);
    setup(person());

    fireEvent.click(screen.getByLabelText(/opted out of voice-printing/i));

    expect(confirmSpy).toHaveBeenCalledWith(expect.stringMatching(/erase/i));
    expect((screen.getByLabelText(/opted out of voice-printing/i) as HTMLInputElement).checked).toBe(false);
    expect(api.updatePerson).not.toHaveBeenCalled();
    confirmSpy.mockRestore();
  });

  it("opts someone out once confirmed", async () => {
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    setup(person());

    fireEvent.click(screen.getByLabelText(/opted out of voice-printing/i));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(api.updatePerson).toHaveBeenCalledWith("p1", expect.objectContaining({ voiceprintOptOut: true })));
    confirmSpy.mockRestore();
  });

  it("hides delete without managePeople", () => {
    setup(person(), false);

    expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
  });
});
