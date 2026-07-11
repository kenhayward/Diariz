import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import IconColorPicker from "./IconColorPicker";

describe("IconColorPicker", () => {
  it("reports an icon choice and marks it pressed", () => {
    const onChange = vi.fn();
    const { rerender } = render(
      <IconColorPicker icon={null} color="#123456" onChange={onChange} colorLabel="Colour" />,
    );

    const star = screen.getByRole("button", { name: "star" });
    expect(star.getAttribute("aria-pressed")).toBe("false");
    fireEvent.click(star);
    expect(onChange).toHaveBeenCalledWith({ icon: "star" });

    rerender(<IconColorPicker icon="star" color="#123456" onChange={onChange} colorLabel="Colour" />);
    expect(screen.getByRole("button", { name: "star" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("reports a colour change", () => {
    const onChange = vi.fn();
    render(<IconColorPicker icon={null} color="#123456" onChange={onChange} colorLabel="Colour" />);

    fireEvent.change(screen.getByLabelText("Colour"), { target: { value: "#abcdef" } });
    expect(onChange).toHaveBeenCalledWith({ color: "#abcdef" });
  });
});
