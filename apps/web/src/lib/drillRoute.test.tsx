import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, useLocation, useNavigate } from "react-router-dom";
import { describe, it, expect } from "vitest";
import { useDrillSectionId } from "./drillRoute";

/// Renders the current drill id plus the router's real location, and exposes the writes - so each test
/// drives the hook the way the panel does rather than asserting on internals.
function Probe() {
  const { sectionId, drillTo, drillOut } = useDrillSectionId();
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <div>
      <span data-testid="at">{sectionId ?? "root"}</span>
      <span data-testid="url">{location.pathname + location.search}</span>
      <button onClick={() => drillTo("ambu")}>drill</button>
      <button onClick={() => drillTo("eu")}>drill deeper</button>
      <button onClick={drillOut}>out</button>
      <button onClick={() => navigate(-1)}>back</button>
    </div>
  );
}

const at = () => screen.getByTestId("at").textContent;
const url = () => screen.getByTestId("url").textContent;

const renderAt = (entry = "/") =>
  render(
    <MemoryRouter initialEntries={[entry]}>
      <Probe />
    </MemoryRouter>,
  );

describe("useDrillSectionId", () => {
  it("is at the root when no ?in= is present", () => {
    renderAt();
    expect(at()).toBe("root");
  });

  it("reads the drill position from ?in=", () => {
    renderAt("/?in=customers");
    expect(at()).toBe("customers");
  });

  it("drills in", () => {
    renderAt();
    fireEvent.click(screen.getByText("drill"));
    expect(at()).toBe("ambu");
  });

  it("drills out to the root", () => {
    renderAt("/?in=customers");
    fireEvent.click(screen.getByText("out"));
    expect(at()).toBe("root");
    expect(url()).not.toContain("in=");
  });

  // The whole reason drill state lives in the URL rather than component state: back pops a level for
  // free, with no hand-rolled history. Driven through the router's own navigate(-1).
  it("pushes history so browser back pops a level", () => {
    renderAt();
    fireEvent.click(screen.getByText("drill"));
    fireEvent.click(screen.getByText("drill deeper"));
    expect(at()).toBe("eu");
    fireEvent.click(screen.getByText("back"));
    expect(at()).toBe("ambu");
    fireEvent.click(screen.getByText("back"));
    expect(at()).toBe("root");
  });

  // Drilling must not disturb which recording is open in the middle panel, nor other params.
  it("keeps the path and other query params intact", () => {
    renderAt("/recordings/rec-1?ts=900");
    fireEvent.click(screen.getByText("drill"));
    expect(url()).toContain("/recordings/rec-1");
    expect(url()).toContain("ts=900");
    expect(at()).toBe("ambu");
  });
});
