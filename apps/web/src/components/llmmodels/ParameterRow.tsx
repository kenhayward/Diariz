import { useTranslation } from "react-i18next";
import type { ParameterKind, ParameterValue } from "./parameterSchema";

/// A single parameter's three-state control.
///
/// The states are the same three instructions they have always been - see `ParameterValue` in
/// `parameterSchema.ts` for what each one means to the resolver - but the affordances are not. The value
/// field IS how a parameter gets set: there is no "Set" button to press first. `↺` returns the row to
/// inherited (emitting `undefined`, never `null`), and `∅` omits it (emitting `null`).
///
/// That asymmetry is the whole design. Three identical buttons made Inherit and Off look like two ways of
/// saying the same thing, when one means "let a lower layer decide" and the other means "send nothing at
/// all"; here they are a different shape, in a different place, and the row says which one is in force.
interface Props {
  name: string;
  /// i18n key in the `account` namespace, not display text.
  label: string;
  kind: ParameterKind;
  /// What this parameter resolves to if left inherited, shown so the admin can see what they would be
  /// overriding. Undefined when no layer below sets it either.
  inherited?: ParameterValue;
  /// True on the Defaults tab, where the layers below are the application's rather than the model's own -
  /// so the sub-line has to credit a different source.
  isBaseGroup: boolean;
  value: ParameterValue;
  onChange: (value: ParameterValue) => void;
  min?: number;
  max?: number;
  hint?: string;
  /// Identifies the control in tests: `param-<group>-<key>`.
  testId?: string;
}

type Mode = "inherit" | "omit" | "set";

function modeOf(value: ParameterValue): Mode {
  if (value === undefined) return "inherit";
  if (value === null) return "omit";
  return "set";
}

const FIELD =
  "w-full rounded-[5px] border border-gray-300 bg-white px-[7px] py-1 text-right text-xs tabular-nums " +
  "text-gray-900 focus:border-indigo-500 focus:outline-none dark:border-gray-700 dark:bg-gray-900 " +
  "dark:text-gray-100";

export default function ParameterRow({
  name, label, kind, inherited, isBaseGroup, value, onChange, min, max, hint, testId,
}: Props) {
  const { t } = useTranslation("account");
  const mode = modeOf(value);
  const id = testId ?? name;

  /// What the inherited value reads as under the control. Booleans and "nothing at all" need words rather
  /// than a bare `true` or a blank, or the admin cannot tell them apart.
  function describeInherited(): string {
    if (inherited === undefined || inherited === null) return t("llmParamNotSet");
    if (typeof inherited === "boolean") return inherited ? t("llmParamOn") : t("llmParamOff");
    return String(inherited);
  }

  function subline(): string {
    if (mode === "set") return hint ? `${t("llmParamOverriddenHere")} · ${t(hint)}` : t("llmParamOverriddenHere");
    if (mode === "omit") return t("llmParamNotSent");
    const value = describeInherited();
    return isBaseGroup ? t("llmParamAppDefault", { value }) : t("llmParamFromDefaults", { value });
  }

  /// The text currently in the box. An inheriting row shows the inherited value rather than a blank, so
  /// that overriding is one edit away from what is already in effect rather than a guess.
  function fieldText(): string {
    const shown = mode === "set" ? value : inherited;
    if (shown === undefined || shown === null) return "";
    return String(shown);
  }

  function type(raw: string) {
    if (kind === "text") {
      onChange(raw);
      return;
    }
    if (raw === "") {
      // An empty box is not a value. Emitting undefined or null would silently change the instruction to
      // "inherit" or "do not send" - neither of which the admin asked for - so hold the field at zero.
      onChange(0);
      return;
    }
    const parsed = kind === "integer" ? parseInt(raw, 10) : parseFloat(raw);
    if (!Number.isNaN(parsed)) onChange(parsed);
  }

  return (
    <div className="grid grid-cols-[minmax(0,1fr)_84px_74px] items-center gap-2 border-b border-gray-100 py-1.5 dark:border-gray-800/60">
      <div className="min-w-0">
        <label htmlFor={id} className="block truncate text-xs text-gray-700 dark:text-gray-300">
          {t(label)}
        </label>
        <span className="block truncate text-[10.5px] text-gray-400 dark:text-gray-500">{subline()}</span>
      </div>

      <div className="min-w-0">
        {mode === "omit" ? (
          <span className="block rounded-[5px] border border-dashed border-red-300 bg-red-50 px-[7px] py-1 text-center text-[11px] text-red-600 dark:border-red-900 dark:bg-red-500/10 dark:text-red-400">
            {t("llmParamOmitted")}
          </span>
        ) : kind === "boolean" ? (
          // A two-option select rather than a free-text "on"/"off" box: it keeps the column width and the
          // look of the other fields, but cannot be given a value that means nothing.
          <select
            id={id}
            data-testid={testId}
            value={(mode === "set" ? value === true : inherited === true) ? "on" : "off"}
            onChange={(e) => onChange(e.target.value === "on")}
            className={FIELD}
          >
            <option value="on">{t("llmParamOn")}</option>
            <option value="off">{t("llmParamOff")}</option>
          </select>
        ) : (
          // Deliberately type=text, not type=number: the spinner and the browser's own validation fight
          // the tabular right-aligned layout, and the parsing below is per ParameterSpec.kind anyway.
          <input
            id={id}
            data-testid={testId}
            type="text"
            inputMode={kind === "text" ? "text" : "decimal"}
            aria-describedby={`${id}-state`}
            value={fieldText()}
            onChange={(e) => type(e.target.value)}
            min={min}
            max={max}
            className={`${FIELD} ${mode === "set" ? "" : "text-gray-400 dark:text-gray-500"}`}
          />
        )}
      </div>

      <div id={`${id}-state`} className="flex items-center justify-end gap-[3px]">
        {mode === "inherit" ? (
          <span className="text-[10.5px] text-gray-400 dark:text-gray-500">{t("llmParamInheritedShort")}</span>
        ) : (
          <button
            type="button"
            title={t("llmParamBackToInherited")}
            aria-label={t("llmParamBackToInherited")}
            onClick={() => onChange(undefined)}
            className="rounded px-1 text-xs text-gray-500 hover:bg-gray-100 hover:text-gray-800 dark:text-gray-400 dark:hover:bg-gray-800 dark:hover:text-gray-100"
          >
            {"↺"}
          </button>
        )}

        <button
          type="button"
          title={mode === "omit" ? t("llmParamSendAgain") : t("llmParamOmitFromRequest")}
          aria-label={mode === "omit" ? t("llmParamSendAgain") : t("llmParamOmitFromRequest")}
          aria-pressed={mode === "omit"}
          onClick={() => onChange(mode === "omit" ? undefined : null)}
          className={
            mode === "omit"
              ? "rounded border border-red-300 bg-red-50 px-1 text-xs text-red-600 dark:border-red-900 dark:bg-red-500/10 dark:text-red-400"
              : "rounded px-1 text-xs text-gray-400 hover:text-red-600 dark:text-gray-500 dark:hover:text-red-400"
          }
        >
          {"∅"}
        </button>
      </div>
    </div>
  );
}
