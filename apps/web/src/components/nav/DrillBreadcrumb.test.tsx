import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi } from "vitest";
import DrillBreadcrumb from "./DrillBreadcrumb";
import type { SectionDto } from "../../lib/types";

const section = (id: string, name: string, parentId: string | null = null): SectionDto =>
  ({ id, name, parentId, position: 0 }) as SectionDto;

const sections = [section("customers", "Customers"), section("ambu", "Ambu", "customers")];

function renderCrumb(sectionId: string | null, onDrill = vi.fn()) {
  render(
    <MemoryRouter initialEntries={[sectionId ? `/?in=${sectionId}` : "/"]}>
      <DrillBreadcrumb sections={sections} sectionId={sectionId} basePath="" onDrill={onDrill} />
    </MemoryRouter>,
  );
  return onDrill;
}

describe("DrillBreadcrumb", () => {
  // At the room's top level there is nowhere to go back to and no folder page to open.
  it("renders nothing at the root", () => {
    const { container } = render(
      <MemoryRouter>
        <DrillBreadcrumb sections={sections} sectionId={null} basePath="" onDrill={vi.fn()} />
      </MemoryRouter>,
    );
    expect(container.innerHTML).toBe("");
  });

  it("shows the current folder over its parent label", () => {
    renderCrumb("ambu");
    expect(screen.getByText("Ambu")).toBeTruthy();
    expect(screen.getByText("Customers")).toBeTruthy();
  });

  it("labels the parent of a top-level folder as all sections", () => {
    renderCrumb("customers");
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText(/all sections/i)).toBeTruthy();
  });

  it("back pops to the parent", () => {
    const onDrill = renderCrumb("ambu");
    fireEvent.click(screen.getByRole("button", { name: /back/i }));
    expect(onDrill).toHaveBeenCalledWith("customers");
  });

  it("back from a top-level folder pops to the root", () => {
    const onDrill = renderCrumb("customers");
    fireEvent.click(screen.getByRole("button", { name: /back/i }));
    expect(onDrill).toHaveBeenCalledWith(null);
  });

  // The design's two distinct targets: the row body browses deeper, this link opens the page.
  it("links Open section page to the folder's page, not a drill", () => {
    const onDrill = renderCrumb("ambu");
    const link = screen.getByRole("link", { name: /open section page/i });
    expect(link.getAttribute("href")).toContain("/sections/ambu");
    expect(onDrill).not.toHaveBeenCalled();
  });

  // Opening the page must not throw away where you were browsing: the drill lives in ?in=, and a bare
  // `to="/sections/:id"` drops the query, popping the panel back to the root behind the page you opened.
  it("keeps the drill position when opening the folder page", () => {
    renderCrumb("ambu");
    expect(screen.getByRole("link", { name: /open section page/i }).getAttribute("href")).toBe(
      "/sections/ambu?in=ambu",
    );
  });

  it("keeps the room prefix on the section page link in a shared room", () => {
    render(
      <MemoryRouter>
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="/rooms/r1" onDrill={vi.fn()} />
      </MemoryRouter>,
    );
    expect(screen.getByRole("link", { name: /open section page/i }).getAttribute("href")).toBe(
      "/rooms/r1/sections/ambu",
    );
  });

  // Drilled into a folder that was deleted underneath us: don't crash, offer a way back out.
  it("still offers a way out for an unknown folder", () => {
    const onDrill = renderCrumb("gone");
    fireEvent.click(screen.getByRole("button", { name: /back/i }));
    expect(onDrill).toHaveBeenCalledWith(null);
  });
});
