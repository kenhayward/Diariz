import { useState } from "react";
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

  /// A controlled numeric field that parses on every keystroke fights the person typing: after "0." the
  /// parse yields 0, the field re-renders as "0", and the decimal point they just pressed disappears. The
  /// row therefore keeps what was TYPED while the field is being edited, and only the parsed value is
  /// emitted upward.
  describe("typing a number", () => {
    function Stateful({ kind = "number" as const, initial = 1 as unknown, onChange = vi.fn() }) {
      const [v, setV] = useState<unknown>(initial);
      return (
        <ParameterRow
          {...base}
          kind={kind}
          value={v as never}
          onChange={(x) => {
            onChange(x);
            setV(x);
          }}
        />
      );
    }

    it("keeps a decimal point that has been typed but not yet completed", () => {
      render(<Stateful />);
      const box = screen.getByRole("textbox") as HTMLInputElement;

      fireEvent.change(box, { target: { value: "0" } });
      fireEvent.change(box, { target: { value: "0." } });

      expect(box.value).toBe("0.");
    });

    it("arrives at the decimal the person typed", () => {
      const onChange = vi.fn();
      render(<Stateful onChange={onChange} />);
      const box = screen.getByRole("textbox") as HTMLInputElement;

      for (const raw of ["0", "0.", "0.3"]) fireEvent.change(box, { target: { value: raw } });

      expect(box.value).toBe("0.3");
      expect(onChange.mock.calls.at(-1)?.[0]).toBe(0.3);
    });

    it("keeps a lone minus sign long enough to type the number after it", () => {
      // -1 is meaningful for max_tokens (unlimited) and top_k (disabled) on some endpoints.
      render(<Stateful kind="integer" />);
      const box = screen.getByRole("textbox") as HTMLInputElement;

      fireEvent.change(box, { target: { value: "-" } });

      expect(box.value).toBe("-");
    });

    it("shows the stored value again once the field is left", () => {
      // A draft that never resolved must not linger as if it had been accepted.
      render(<Stateful />);
      const box = screen.getByRole("textbox") as HTMLInputElement;

      fireEvent.change(box, { target: { value: "not a number" } });
      fireEvent.blur(box);

      expect(box.value).toBe("1");
    });

    it("does not emit anything for a value it cannot parse", () => {
      const onChange = vi.fn();
      render(<Stateful onChange={onChange} />);

      fireEvent.change(screen.getByRole("textbox"), { target: { value: "abc" } });

      expect(onChange).not.toHaveBeenCalled();
    });
  });

  describe("range guidance", () => {
    it("says when a value is outside the range the parameter documents", () => {
      // Advisory, not enforced: Diariz cannot know what any given endpoint accepts, and some take values
      // well outside the OpenAI-documented ranges. Saying so beats silently clamping a deliberate choice.
      render(<ParameterRow {...base} min={0} max={2} value={7} onChange={vi.fn()} />);

      expect(screen.getByText(/0 to 2/)).toBeTruthy();
    });

    it("still emits the out-of-range value the person chose", () => {
      const onChange = vi.fn();
      render(<ParameterRow {...base} min={0} max={2} value={1} onChange={onChange} />);

      fireEvent.change(screen.getByRole("textbox"), { target: { value: "7" } });

      expect(onChange).toHaveBeenCalledWith(7);
    });

    it("says nothing about a value inside the range", () => {
      render(<ParameterRow {...base} min={0} max={2} value={0.7} onChange={vi.fn()} />);

      expect(screen.queryByText(/0 to 2/)).toBeNull();
      expect(screen.getByText(/overridden here/i)).toBeTruthy();
    });
  });
});
