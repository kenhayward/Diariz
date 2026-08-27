import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../auth", () => ({ useAuth: () => ({ setSession: vi.fn() }) }));
vi.mock("../language", () => ({
  useLanguage: () => ({ available: [{ code: "en", englishName: "English", nativeName: "English" }], setLanguage: vi.fn() }),
}));
vi.mock("../theme", () => ({ useTheme: () => ({ theme: "auto", setTheme: vi.fn() }) }));
vi.mock("../lib/api", () => ({
  api: { getProfile: vi.fn(), updateProfile: vi.fn() },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));
vi.mock("../lib/languages", () => ({
  fetchLanguages: () =>
    Promise.resolve([
      { code: "en", englishName: "English", nativeName: "English", rtl: false },
      { code: "de", englishName: "German", nativeName: "Deutsch", rtl: false },
    ]),
}));

import { api } from "../lib/api";
import ProfileSection from "./ProfileSection";

const PROFILE = {
  email: "a@b.test", fullName: "A B", nativeLanguage: null, uiLanguage: null, transcriptionLanguage: null,
  googleConnected: false, googleCalendar: false, jobTitle: null, companyName: null, jobDescription: null,
  companyDescription: null, linkedIn: null, theme: "auto", person: null,
};

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ProfileSection />
    </QueryClientProvider>,
  );
}

const picker = () => screen.getByLabelText(/transcription language/i) as HTMLSelectElement;

describe("ProfileSection transcription language", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue(PROFILE);
    (api.updateProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ accessToken: "t" });
  });

  // A user who always records in the same language sets it once here instead of per recording; it is a
  // separate setting from the native language, which is the translation target and is often different.
  it("saves the chosen default transcription language", async () => {
    renderSection();
    // The options come from the languages query - selecting one before it resolves would be a no-op.
    await waitFor(() => expect(picker().options.length).toBeGreaterThan(1));

    fireEvent.change(picker(), { target: { value: "de" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() =>
      expect(api.updateProfile).toHaveBeenCalledWith(expect.objectContaining({ transcriptionLanguage: "de" })),
    );
  });

  it("shows the saved language when the profile has one", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ ...PROFILE, transcriptionLanguage: "de" });
    renderSection();

    await waitFor(() => expect(picker().value).toBe("de"));
  });

  it("defaults to auto-detect and saves it as cleared", async () => {
    renderSection();
    await waitFor(() => expect(picker()).toBeTruthy());
    expect(picker().value).toBe("");

    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() =>
      expect(api.updateProfile).toHaveBeenCalledWith(expect.objectContaining({ transcriptionLanguage: null })),
    );
  });

  describe("you in transcripts", () => {
    const withPerson = (person: Record<string, unknown>) =>
      (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ ...PROFILE, person });

    it("shows the linked person and its sample count", async () => {
      withPerson({ id: "p1", name: "Ada Lovelace", hasVoiceprint: true, sampleCount: 3, voiceprintOptOut: false });
      renderSection();

      expect(await screen.findByText("Ada Lovelace")).toBeTruthy();
      expect(screen.getByText(/3 samples/i)).toBeTruthy();
    });

    it("tells a user with no voiceprint how to get one", async () => {
      withPerson({ id: "p1", name: "Ada Lovelace", hasVoiceprint: false, sampleCount: 0, voiceprintOptOut: false });
      renderSection();

      expect(await screen.findByText(/no voiceprint yet/i)).toBeTruthy();
      expect(screen.queryByText(/samples/i)).toBeNull();
    });

    it("says so when the user has opted out of voice-printing", async () => {
      withPerson({ id: "p1", name: "Ada Lovelace", hasVoiceprint: false, sampleCount: 0, voiceprintOptOut: true });
      renderSection();

      expect(await screen.findByText(/opted out of voice-printing/i)).toBeTruthy();
      expect(screen.queryByText(/no voiceprint yet/i)).toBeNull();
    });

    /// A server older than this field sends no person at all; the block simply does not render.
    it("renders nothing when the server sends no person", async () => {
      renderSection();
      await waitFor(() => expect(picker()).toBeTruthy());

      expect(screen.queryByText(/you in transcripts/i)).toBeNull();
    });
  });
});
