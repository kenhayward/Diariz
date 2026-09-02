import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect } from "vitest";
import AboutModal from "./AboutModal";
import { APP_VERSION } from "../lib/version";

describe("AboutModal", () => {
  it("shows identity, version, links, disclaimers and copyright", () => {
    render(
      <MemoryRouter>
        <AboutModal onClose={() => {}} />
      </MemoryRouter>,
    );

    const dialog = screen.getByRole("dialog", { name: /about diariz/i });
    expect(within(dialog).getByText("Diariz")).toBeTruthy();
    expect(within(dialog).getByText(/smart meeting transcription/i)).toBeTruthy();
    // Version is injected from version.json at build/test time.
    expect(within(dialog).getByText(new RegExp(`version ${APP_VERSION.replace(/\./g, "\\.")}`, "i"))).toBeTruthy();

    // Help and the release notes are app routes and must navigate IN PLACE. As new tabs they left the
    // desktop shell and the installed PWA for the system browser, where nobody is signed in and both
    // pages sit behind the login. The absence of target="_blank" is the whole point of the assertion.
    const notes = within(dialog).getByRole("link", { name: /release notes/i });
    expect(notes.getAttribute("href")).toBe("/release-notes");
    expect(notes.getAttribute("target")).toBeNull();
    const help = within(dialog).getByRole("link", { name: /browse help/i });
    expect(help.getAttribute("href")).toBe("/help");
    expect(help.getAttribute("target")).toBeNull();
    // GitHub is genuinely external, so it keeps its new tab.
    const github = within(dialog).getByRole("link", { name: /github/i });
    expect(github.getAttribute("href")).toMatch(/github\.com/);
    expect(github.getAttribute("target")).toBe("_blank");

    // Key disclaimers + copyright.
    expect(within(dialog).getByText(/non-commercial/i)).toBeTruthy();
    // The LLM disclaimer (distinct from the capabilities blurb, which also mentions the endpoint).
    expect(within(dialog).getByText(/Summaries and chat use an OpenAI-compatible LLM endpoint/i)).toBeTruthy();
    expect(within(dialog).getByText(/ken hayward/i)).toBeTruthy();
  });
});
