import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import userEvent from "@testing-library/user-event";
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

// Captures the router's location so a test can assert where `navigate()` landed, mirroring the
// `PathSpy` pattern used in RecordingDetail.test.tsx.
function LocationSpy({ onChange }: { onChange: (loc: { pathname: string; search: string }) => void }) {
  const loc = useLocation();
  onChange({ pathname: loc.pathname, search: loc.search });
  return null;
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
    // The dropdown does not exist at the root, so neither may the button.
    expect(screen.queryByRole("link", { name: "View folder page" })).toBeNull();
  });

  it("shows the current folder along with its parent", () => {
    renderCrumb("ambu");
    expect(screen.getByText("Ambu")).toBeTruthy();
    expect(screen.getByText("Customers")).toBeTruthy();
  });

  // The old design had a dedicated "All sections" label for a top-level folder's parent slot; the
  // collapsing path has no such slot - a top-level folder is just a chain of one, and where "back"
  // leads is covered by the dedicated back-button tests below.
  it("shows a single crumb for a top-level folder", () => {
    renderCrumb("customers");
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.queryByText(/all sections/i)).toBeNull();
  });

  it("shows the whole ancestor path, not just the parent", () => {
    const deepSections = [
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
      { id: "falcon", name: "Project Falcon", parentId: "acme", position: 0 },
    ] as SectionDto[];

    render(
      <MemoryRouter>
        <DrillBreadcrumb sections={deepSections} sectionId="falcon" basePath="" onDrill={() => {}} />
      </MemoryRouter>,
    );

    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Project Falcon")).toBeTruthy();
  });

  it("drills to an ancestor when its crumb is clicked", async () => {
    const onDrill = vi.fn();
    const deepSections = [
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
    ] as SectionDto[];

    render(
      <MemoryRouter>
        <DrillBreadcrumb sections={deepSections} sectionId="acme" basePath="" onDrill={onDrill} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByText("Customers"));

    expect(onDrill).toHaveBeenCalledWith("customers");
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

  // The design's two distinct targets: a crumb browses deeper, this button opens the page. It is a
  // button in the row now, not an entry buried in the menu.
  it("opens the folder's page from the button, not a drill", async () => {
    const onDrill = vi.fn();
    let location = { pathname: "", search: "" };
    render(
      <MemoryRouter initialEntries={["/?in=ambu"]}>
        <LocationSpy onChange={(loc) => (location = loc)} />
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="" onDrill={onDrill} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole("link", { name: "View folder page" }));

    expect(location.pathname).toBe("/sections/ambu");
    expect(onDrill).not.toHaveBeenCalled();
  });

  // Opening the page must not throw away where you were browsing: the drill lives in ?in=, and a bare
  // navigate to "/sections/:id" drops the query, popping the panel back to the root behind the page you
  // opened.
  it("keeps the drill position when opening the folder page", async () => {
    let location = { pathname: "", search: "" };
    render(
      <MemoryRouter initialEntries={["/?in=ambu"]}>
        <LocationSpy onChange={(loc) => (location = loc)} />
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="" onDrill={vi.fn()} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole("link", { name: "View folder page" }));

    expect(location.pathname + location.search).toBe("/sections/ambu?in=ambu");
  });

  it("keeps the room prefix on the folder page button in a shared room", () => {
    render(
      <MemoryRouter initialEntries={["/?in=ambu"]}>
        <DrillBreadcrumb sections={sections} sectionId="ambu" basePath="/rooms/r1" onDrill={vi.fn()} />
      </MemoryRouter>,
    );

    const link = screen.getByRole("link", { name: "View folder page" });
    expect(link.getAttribute("href")).toBe("/rooms/r1/sections/ambu?in=ambu");
  });

  // Promoting the button out of the menu means taking it OUT of the menu - one action, one control.
  it("leaves the menu as nothing but the ancestor chain", async () => {
    renderCrumb("ambu");

    await userEvent.click(screen.getByLabelText("Show full folder path"));

    const items = screen.getAllByRole("menuitem").map((el) => el.textContent);
    expect(items).toEqual(["Customers", "Ambu"]);
  });

  // Drilled into a folder that was deleted underneath us: don't crash, offer a way back out.
  it("still offers a way out for an unknown folder", () => {
    const onDrill = renderCrumb("gone");
    // The folder was deleted underneath us - offer the way out, not a link to a page that is gone.
    expect(screen.queryByRole("link", { name: "View folder page" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /back/i }));
    expect(onDrill).toHaveBeenCalledWith(null);
  });
});
