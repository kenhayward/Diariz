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

    // All three open a window of their own. For Help and the release notes that is only correct because
    // the desktop shell now keeps same-origin popups instead of handing them to the system browser
    // (PR #732) - and it is necessary because Help no longer has a "back to app" link, so navigating in
    // place would strand the reader on a full-screen page.
    const notes = within(dialog).getByRole("link", { name: /release notes/i });
    expect(notes.getAttribute("href")).toBe("/release-notes");
    expect(notes.getAttribute("target")).toBe("_blank");
    const help = within(dialog).getByRole("link", { name: /browse help/i });
    expect(help.getAttribute("href")).toBe("/help");
    expect(help.getAttribute("target")).toBe("_blank");
    const github = within(dialog).getByRole("link", { name: /github/i });
    expect(github.getAttribute("href")).toMatch(/github\.com/);
    expect(github.getAttribute("target")).toBe("_blank");

    // The trailing arrow means "this leaves Diariz". Only GitHub does now.
    expect(notes.textContent).not.toContain("→");
    expect(github.textContent).toContain("→");

    // Key disclaimers + copyright.
    expect(within(dialog).getByText(/non-commercial/i)).toBeTruthy();
    // The LLM disclaimer (distinct from the capabilities blurb, which also mentions the endpoint).
    expect(within(dialog).getByText(/Summaries and chat use an OpenAI-compatible LLM endpoint/i)).toBeTruthy();
    expect(within(dialog).getByText(/ken hayward/i)).toBeTruthy();
  });
});
