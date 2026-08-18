import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ParameterRow from "./ParameterRow";

/// The redesigned three-state control. The states are unchanged - they are three different instructions to
/// the resolver - but the affordances are not: the value field itself is how a parameter gets set, `↺`
/// returns it to inherited, and `∅` omits it.
///
///   Inherit - the key is absent, so the next layer down decides
///   Omit    - the key is present with a null value, so the parameter is left out of the request entirely
///   a value - the key is present with that value
describe("ParameterRow", () => {
  const base = {
    name: "temperature",
    label: "llmParamTemperature",
    kind: "number" as const,
    inherited: 0.3,
    isBaseGroup: false,
  };

  it("shows what the row inherits when nothing is set here", () => {
    render(<ParameterRow {...base} value={undefined} onChange={vi.fn()} />);
    // The admin has to be able to see what they are inheriting before deciding to override it.
    expect(screen.getByText(/0\.3/)).toBeTruthy();
  });

  it("sets the parameter by typing in the value field, with no separate Set button", () => {
    // The whole point of the redesign: the field is the affordance. An inheriting row is still editable.
    const onChange = vi.fn();
    render(<ParameterRow {...base} value={undefined} onChange={onChange} />);

    fireEvent.change(screen.getByRole("textbox"), { target: { value: "0.7" } });

    expect(onChange).toHaveBeenCalledWith(0.7);
  });

  it("emits null when omitted", () => {
    const onChange = vi.fn();
    render(<ParameterRow {...base} value={undefined} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /omit/i }));

    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("returns to inherit by removing the key, not by sending null", () => {
    // The important one. null means "send nothing" to the resolver, so emitting null here would silently
    // turn an inherited 0.3 into an omitted parameter - a behaviour change the admin never asked for.
    const onChange = vi.fn();
    render(<ParameterRow {...base} value={0.5} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /inherited/i }));

    expect(onChange).toHaveBeenCalledWith(undefined);
  });

  it("returns an omitted row to inherit rather than omitting it twice", () => {
    const onChange = vi.fn();
    render(<ParameterRow {...base} value={null} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /send this parameter again/i }));

    expect(onChange).toHaveBeenCalledWith(undefined);
  });

  it("replaces the value field with an omitted marker rather than showing an empty box", () => {
    // An omitted parameter that rendered as a blank input would be indistinguishable from one set to
    // nothing, which is precisely the distinction this control exists to expose.
    render(<ParameterRow {...base} value={null} onChange={vi.fn()} />);

    expect(screen.queryByRole("textbox")).toBeNull();
    expect(screen.getByText(/omitted/i)).toBeTruthy();
  });

  it("says a set row is overridden here, not what it inherits", () => {
    render(<ParameterRow {...base} value={0.5} onChange={vi.fn()} />);

    expect(screen.getByText(/overridden here/i)).toBeTruthy();
    expect(screen.queryByText(/from Defaults/i)).toBeNull();
  });

  it("credits the application defaults on the Defaults tab, not the model's own", () => {
    // On ModelBase there is no model layer below, so "from Defaults" would name the wrong thing.
    render(<ParameterRow {...base} isBaseGroup value={undefined} onChange={vi.fn()} />);

    expect(screen.getByText(/app default/i)).toBeTruthy();
  });

  it("emits a boolean for a boolean parameter rather than a string", () => {
    // tools_supported reaching the API as "true" would fail the layer merge's type check and be ignored.
    const onChange = vi.fn();
    render(
      <ParameterRow
        name="tools_supported"
        label="llmParamToolsSupported"
        kind="boolean"
        inherited={true}
        isBaseGroup={false}
        value={false}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "on" } });

    expect(onChange).toHaveBeenCalledWith(true);
  });

  it("emits free text for reasoning effort rather than constraining it to a list", () => {
    // gpt-oss takes low/medium/high, qwen3 also takes xhigh, and the next model will take something else.
    const onChange = vi.fn();
    render(
      <ParameterRow
        name="reasoning_effort"
        label="llmParamReasoningEffort"
        kind="text"
        inherited="medium"
        isBaseGroup={false}
        value="high"
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByRole("textbox"), { target: { value: "xhigh" } });

    expect(onChange).toHaveBeenCalledWith("xhigh");
  });

  it("holds an emptied numeric field at zero rather than silently omitting the parameter", () => {
    // An empty box is not a value. Emitting undefined or null would change the instruction behind the
    // admin's back - to "inherit" or "do not send", neither of which they asked for.
    const onChange = vi.fn();
    render(<ParameterRow {...base} value={0.5} onChange={onChange} />);

    fireEvent.change(screen.getByRole("textbox"), { target: { value: "" } });

    expect(onChange).toHaveBeenCalledWith(0);
  });
});
