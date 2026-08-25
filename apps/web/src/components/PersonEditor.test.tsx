import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
    // The Voiceprint tab fetches through this. Its absence used to be the guard that the Profile tab
    // never fetched; that guarantee is now an explicit assertion below, since a missing mock method
    // would fail as a crash rather than as the thing it was protecting.
    getPerson: vi.fn(),
    getPersonAttributions: vi.fn(),
    getPersonDiagnostics: vi.fn(),
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
  mock(api.getPerson).mockResolvedValue({ person: person(), identifiedCount: 0, samples: [] });
  mock(api.getPersonAttributions).mockResolvedValue([]);
  mock(api.getPersonDiagnostics).mockResolvedValue({ samples: [], aloneCount: 0, widestPair: null });
});

describe("PersonEditor", () => {
  /// Profile is the default because the common task is fixing a job title while reading a transcript;
  /// Voiceprint is an audit surface you go looking for.
  it("opens on the Profile tab", () => {
    setup(person());

    expect(screen.getByLabelText("Name")).toBeTruthy();
    expect((screen.getByRole("tab", { name: "Profile" }) as HTMLElement).getAttribute("aria-selected")).toBe("true");
  });

  it("does not fetch the voiceprint until its tab is opened", () => {
    // Opening the directory should not pull a person's training data for every row you click through.
    setup(person());

    expect(api.getPerson).not.toHaveBeenCalled();
  });

  it("switches to the Voiceprint tab", async () => {
    setup(person());

    fireEvent.click(screen.getByRole("tab", { name: "Voiceprint" }));

    // The profile panel is hidden rather than unmounted - see the note on PersonEditor.
    expect(screen.getByTestId("profile-panel").hasAttribute("hidden")).toBe(true);
    expect(screen.getByTestId("voiceprint-panel").hasAttribute("hidden")).toBe(false);
    await waitFor(() => expect(api.getPerson).toHaveBeenCalledWith("p1"));
  });

  it("keeps a half-typed edit when you look at the voiceprint and come back", async () => {
    // Both tabs read `person` from the caller, so the draft lives in the Profile tab and survives its own
    // unmount only if the tab is not remounted from scratch - worth pinning, because losing a half-typed
    // correction to a name is exactly the kind of thing nobody reports as a bug.
    setup(person());
    fireEvent.change(screen.getByLabelText("Job title"), { target: { value: "Countess" } });

    fireEvent.click(screen.getByRole("tab", { name: "Voiceprint" }));
    fireEvent.click(screen.getByRole("tab", { name: "Profile" }));

    await waitFor(() =>
      expect((screen.getByLabelText("Job title") as HTMLInputElement).value).toBe("Countess"));
  });

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

  it("offers two tabs, because the third listed the same recordings as the second", async () => {
    // Diagnostics scored the samples and Voiceprint held the controls, so acting on a flagged recording
    // meant remembering its name and switching tabs - and for a recording whose speaker had moved, there
    // was no row on the other tab to switch to at all.
    setup(person());

    expect(screen.getAllByRole("tab").map((el) => el.textContent)).toEqual(["Profile", "Voiceprint"]);
  });

  it("makes no diagnostics request from the shell", async () => {
    // The diagnosis belongs to the Voiceprint tab now, which is itself deferred until opened. The editor
    // is rendered for every row clicked through in the directory.
    setup(person());

    expect(api.getPersonDiagnostics).not.toHaveBeenCalled();
  });
});