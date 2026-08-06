import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { MoveClipboardProvider, useMoveClipboard } from "./moveClipboard";

function Harness() {
  const { cut, cutRecordings, cutFolder, clear } = useMoveClipboard();
  return (
    <div>
      <div data-testid="kind">{cut?.kind ?? "none"}</div>
      <div data-testid="ids">{cut?.ids.join(",") ?? ""}</div>
      <div data-testid="sourceSectionId">{cut?.sourceSectionId ?? "null"}</div>
      <div data-testid="sourceRoomId">{cut?.sourceRoomId ?? "null"}</div>
      <button onClick={() => cutRecordings(["r1", "r2"], "sec-1", "room-1")}>cut-recordings</button>
      <button onClick={() => cutFolder("f1", "sec-parent", "room-2")}>cut-folder</button>
      <button onClick={() => clear()}>clear</button>
    </div>
  );
}

function renderInProvider() {
  return render(
    <MoveClipboardProvider>
      <Harness />
    </MoveClipboardProvider>,
  );
}

describe("MoveClipboardProvider", () => {
  it("cutting recordings stores them", () => {
    renderInProvider();
    fireEvent.click(screen.getByText("cut-recordings"));
    expect(screen.getByTestId("kind").textContent).toBe("recordings");
    expect(screen.getByTestId("ids").textContent).toBe("r1,r2");
    expect(screen.getByTestId("sourceSectionId").textContent).toBe("sec-1");
    expect(screen.getByTestId("sourceRoomId").textContent).toBe("room-1");
  });

  it("cutting a folder replaces a recordings cut", () => {
    renderInProvider();
    fireEvent.click(screen.getByText("cut-recordings"));
    fireEvent.click(screen.getByText("cut-folder"));
    expect(screen.getByTestId("kind").textContent).toBe("folders");
    expect(screen.getByTestId("ids").textContent).toBe("f1");
    expect(screen.getByTestId("sourceSectionId").textContent).toBe("sec-parent");
    expect(screen.getByTestId("sourceRoomId").textContent).toBe("room-2");
  });

  it("clear() empties the clipboard", () => {
    renderInProvider();
    fireEvent.click(screen.getByText("cut-recordings"));
    fireEvent.click(screen.getByText("clear"));
    expect(screen.getByTestId("kind").textContent).toBe("none");
    expect(screen.getByTestId("ids").textContent).toBe("");
  });
});

describe("useMoveClipboard outside a provider", () => {
  it("is inert: default cut is null and the actions are no-ops", () => {
    render(<Harness />);
    expect(screen.getByTestId("kind").textContent).toBe("none");
    fireEvent.click(screen.getByText("cut-recordings"));
    expect(screen.getByTestId("kind").textContent).toBe("none");
    fireEvent.click(screen.getByText("cut-folder"));
    expect(screen.getByTestId("kind").textContent).toBe("none");
    fireEvent.click(screen.getByText("clear"));
    expect(screen.getByTestId("kind").textContent).toBe("none");
  });
});
