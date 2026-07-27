import { describe, it, expect } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import HelpButton from "./HelpButton";
import { HelpProvider } from "../lib/help/HelpContext";

function renderButton(topic = "formulas") {
  return render(
    <MemoryRouter>
      <HelpProvider>
        <HelpButton topic={topic} />
      </HelpProvider>
    </MemoryRouter>,
  );
}

describe("HelpButton", () => {
  it("renders a button named after the article it explains", () => {
    renderButton();
    expect(screen.getByRole("button", { name: /help: formulas/i })).toBeTruthy();
  });

  it("shows nothing until it is clicked", () => {
    renderButton();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("opens a popover with the article title and summary", () => {
    renderButton();
    fireEvent.click(screen.getByRole("button", { name: /help: formulas/i }));
    const dialog = screen.getByRole("dialog");
    expect(dialog.textContent).toContain("Formulas");
    expect(dialog.textContent).toContain("template plus the context");
  });

  it("links through to the full article in a new tab", () => {
    renderButton();
    fireEvent.click(screen.getByRole("button", { name: /help: formulas/i }));
    const link = screen.getByRole("link", { name: /read more/i }) as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe("/help/formulas");
    expect(link.getAttribute("target")).toBe("_blank");
  });

  it("closes on Escape", () => {
    renderButton();
    fireEvent.click(screen.getByRole("button", { name: /help: formulas/i }));
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes when the backdrop is clicked", () => {
    renderButton();
    fireEvent.click(screen.getByRole("button", { name: /help: formulas/i }));
    fireEvent.click(screen.getByTestId("help-popover-backdrop"));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes when the same button is clicked again", () => {
    renderButton();
    const btn = screen.getByRole("button", { name: /help: formulas/i });
    fireEvent.click(btn);
    fireEvent.click(btn);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("renders nothing at all for a topic with no article", () => {
    renderButton("no-such-topic");
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("keeps only one popover open at a time", () => {
    render(
      <MemoryRouter>
        <HelpProvider>
          <HelpButton topic="formulas" />
          <HelpButton topic="action-items" />
        </HelpProvider>
      </MemoryRouter>,
    );
    fireEvent.click(screen.getByRole("button", { name: /help: formulas/i }));
    fireEvent.click(screen.getByRole("button", { name: /help: action items/i }));
    const dialogs = screen.getAllByRole("dialog");
    expect(dialogs).toHaveLength(1);
    expect(dialogs[0].textContent).toContain("Action items");
  });
});
