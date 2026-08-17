import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ParameterField from "./ParameterField";

/// The tri-state control. Three states that look similar in a UI but are three different instructions to
/// the resolver, and confusing the last two silently changes what gets sent to the model:
///
///   Inherit - the key is absent, so the next layer down decides
///   Off     - the key is present with a null value, so the parameter is omitted from the request entirely
///   a value - the key is present with that value
describe("ParameterField", () => {
  const base = { name: "temperature", label: "llmParamTemperature", kind: "number" as const, inherited: 0.3 };

  it("shows the inherited value when nothing is set here", () => {
    render(<ParameterField {...base} value={undefined} onChange={vi.fn()} />);
    // The admin has to be able to see what they are inheriting before deciding to override it.
    expect(screen.getByText(/0\.3/)).toBeTruthy();
  });

  it("emits null when switched to Off", () => {
    const onChange = vi.fn();
    render(<ParameterField {...base} value={undefined} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: /off/i }));
    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("emits the number when given a value", () => {
    const onChange = vi.fn();
    render(<ParameterField {...base} value={0.5} onChange={onChange} />);
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "0.7" } });
    expect(onChange).toHaveBeenCalledWith(0.7);
  });

  it("switches back to Inherit by removing the key, not by sending null", () => {
    // The important one. null means "send nothing" to the resolver, so emitting null here would silently
    // turn an inherited 0.3 into an omitted parameter - a behaviour change the admin never asked for.
    const onChange = vi.fn();
    render(<ParameterField {...base} value={0.5} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: /inherit/i }));
    expect(onChange).toHaveBeenCalledWith(undefined);
  });

  it("distinguishes Off from Inherit in what it renders", () => {
    // If both states looked the same the admin could not tell an omitted parameter from an inherited one,
    // which is precisely the distinction this control exists to expose.
    const { container: off } = render(
      <ParameterField {...base} value={null} onChange={vi.fn()} />,
    );
    const { container: inherit } = render(
      <ParameterField {...base} value={undefined} onChange={vi.fn()} />,
    );
    expect(off.innerHTML).not.toBe(inherit.innerHTML);
  });

  it("emits a boolean for a boolean parameter rather than a string", () => {
    // tools_supported reaching the API as "true" would fail the layer merge's type check and be ignored.
    const onChange = vi.fn();
    render(
      <ParameterField
        name="tools_supported"
        label="llmParamToolsSupported"
        kind="boolean"
        inherited={true}
        value={false}
        onChange={onChange}
      />,
    );
    fireEvent.click(screen.getByRole("checkbox"));
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it("emits free text for reasoning effort rather than constraining it to a list", () => {
    // gpt-oss takes low/medium/high, qwen3 also takes xhigh, and the next model will take something else.
    const onChange = vi.fn();
    render(
      <ParameterField
        name="reasoning_effort"
        label="llmParamReasoningEffort"
        kind="text"
        inherited="medium"
        value="high"
        onChange={onChange}
      />,
    );
    fireEvent.change(screen.getByRole("textbox"), { target: { value: "xhigh" } });
    expect(onChange).toHaveBeenCalledWith("xhigh");
  });
});
