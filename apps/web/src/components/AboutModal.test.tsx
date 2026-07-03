import { render, screen, within } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import AboutModal from "./AboutModal";
import { APP_VERSION } from "../lib/version";

describe("AboutModal", () => {
  it("shows identity, version, links, disclaimers and copyright", () => {
    render(<AboutModal onClose={() => {}} />);

    const dialog = screen.getByRole("dialog", { name: /about diariz/i });
    expect(within(dialog).getByText("Diariz")).toBeTruthy();
    expect(within(dialog).getByText(/smart meeting transcription/i)).toBeTruthy();
    // Version is injected from version.json at build/test time.
    expect(within(dialog).getByText(new RegExp(`version ${APP_VERSION.replace(/\./g, "\\.")}`, "i"))).toBeTruthy();

    // Release notes opens in a new tab; GitHub link present.
    const notes = within(dialog).getByRole("link", { name: /release notes/i });
    expect(notes.getAttribute("href")).toBe("/release-notes");
    expect(notes.getAttribute("target")).toBe("_blank");
    expect(within(dialog).getByRole("link", { name: /github/i }).getAttribute("href")).toMatch(/github\.com/);

    // Key disclaimers + copyright.
    expect(within(dialog).getByText(/non-commercial/i)).toBeTruthy();
    // The LLM disclaimer (distinct from the capabilities blurb, which also mentions the endpoint).
    expect(within(dialog).getByText(/Summaries and chat use an OpenAI-compatible LLM endpoint/i)).toBeTruthy();
    expect(within(dialog).getByText(/ken hayward/i)).toBeTruthy();
  });
});
