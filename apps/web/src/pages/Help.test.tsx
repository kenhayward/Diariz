import { describe, it, expect } from "vitest";
import { render, screen, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Help from "./Help";

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/help" element={<Help />} />
        <Route path="/help/:slug" element={<Help />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("Help page", () => {
  it("lists the article groups in the nav", () => {
    renderAt("/help");
    const nav = screen.getByRole("navigation");
    expect(within(nav).getByText("Getting started")).toBeTruthy();
    expect(within(nav).getByText("Asking questions")).toBeTruthy();
  });

  it("links each article to its own address", () => {
    renderAt("/help");
    const link = screen.getByRole("link", { name: /^formulas$/i }) as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe("/help/formulas");
  });

  it("shows the first article when no slug is given", () => {
    renderAt("/help");
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("What is Diariz");
  });

  it("renders the article named in the address", () => {
    renderAt("/help/formulas");
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Formulas");
    expect(screen.getByRole("main").textContent).toContain("template plus a chosen");
  });

  it("renders the article body as markdown, not raw text", () => {
    renderAt("/help/formulas");
    const main = screen.getByRole("main");
    expect(within(main).getAllByRole("heading", { level: 2 }).length).toBeGreaterThan(0);
    expect(main.textContent).not.toContain("## The template");
  });

  it("shows a not-found message for an unknown slug", () => {
    renderAt("/help/no-such-article");
    expect(screen.getByRole("main").textContent).toContain("Article not found");
  });

  it("still shows the nav on an unknown slug so the user can recover", () => {
    renderAt("/help/no-such-article");
    expect(screen.getByRole("link", { name: /^formulas$/i })).toBeTruthy();
  });

  it("filters the nav to matching articles when searching", () => {
    renderAt("/help");
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "voiceprint" } });
    const nav = screen.getByRole("navigation");
    expect(within(nav).queryByRole("link", { name: /^formulas$/i })).toBeNull();
    expect(within(nav).getByRole("link", { name: /transcripts and speakers/i })).toBeTruthy();
  });

  it("reports when a search matches nothing", () => {
    renderAt("/help");
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "zzzznotathing" } });
    expect(screen.getByRole("navigation").textContent).toContain("No help articles match");
  });

  it("restores the grouped tree when the search is cleared", () => {
    renderAt("/help");
    const box = screen.getByRole("searchbox");
    fireEvent.change(box, { target: { value: "voiceprint" } });
    fireEvent.change(box, { target: { value: "" } });
    expect(within(screen.getByRole("navigation")).getByText("Getting started")).toBeTruthy();
  });

  it("offers a way back to the app", () => {
    renderAt("/help");
    expect(screen.getByRole("link", { name: /back to app/i })).toBeTruthy();
  });
});
