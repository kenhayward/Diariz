import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect } from "vitest";
import { useActiveRecordingId, useActiveSectionId } from "./activeRoute";

function Probe({ hook }: { hook: () => string | null }) {
  return <span data-testid="v">{hook() ?? "null"}</span>;
}

/// Render the hook at a route and read its value, unmounting so successive calls don't collide in the DOM.
function idAt(path: string, hook: () => string | null): string | null {
  const { getByTestId, unmount } = render(
    <MemoryRouter initialEntries={[path]}>
      <Probe hook={hook} />
    </MemoryRouter>,
  );
  const v = getByTestId("v").textContent;
  unmount();
  return v;
}

describe("useActiveRecordingId", () => {
  it("matches the top-level (personal-room) recording route", () => {
    expect(idAt("/recordings/abc", useActiveRecordingId)).toBe("abc");
  });
  it("matches the shared-room recording route", () => {
    expect(idAt("/rooms/r1/recordings/abc", useActiveRecordingId)).toBe("abc");
  });
  it("is null off a recording route", () => {
    expect(idAt("/sections/s1", useActiveRecordingId)).toBe("null");
  });
});

describe("useActiveSectionId", () => {
  it("matches the top-level (personal-room) section route", () => {
    expect(idAt("/sections/s1", useActiveSectionId)).toBe("s1");
  });
  it("matches the shared-room section route", () => {
    expect(idAt("/rooms/r1/sections/s1", useActiveSectionId)).toBe("s1");
  });
  it("is null off a section route", () => {
    expect(idAt("/recordings/abc", useActiveSectionId)).toBe("null");
  });
});
