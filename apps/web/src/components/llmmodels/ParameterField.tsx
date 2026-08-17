import { useTranslation } from "react-i18next";
import type { ParameterKind } from "./parameterSchema";

/// A single parameter's three-state control.
///
/// The three states are three different instructions, and the last two are the ones easily confused:
///
///   `undefined` - Inherit. The key is absent from this layer, so the next layer down decides.
///   `null`      - Off. The key is present with a null value, so the parameter is omitted from the request
///                 body entirely. This is NOT the same as inheriting - it actively suppresses whatever a
///                 lower layer would have supplied.
///   a value     - the key is present with that value.
///
/// Switching back to Inherit therefore has to emit `undefined`, not `null`. Emitting null would turn an
/// inherited 0.3 into an omitted parameter - a behaviour change the administrator never asked for, and one
/// they could not see afterwards without reading the JSON.
export type ParameterValue = string | number | boolean | null | undefined;

interface Props {
  name: string;
  /// i18n key in the `account` namespace, not display text.
  label: string;
  kind: ParameterKind;
  /// What this parameter resolves to if left on Inherit, shown so the admin can see what they would be
  /// overriding. Undefined when no layer below sets it either.
  inherited?: ParameterValue;
  value: ParameterValue;
  onChange: (value: ParameterValue) => void;
  min?: number;
  max?: number;
  hint?: string;
  /// Identifies the control in tests: `param-<group>-<key>`.
  testId?: string;
}

type Mode = "inherit" | "off" | "set";

function modeOf(value: ParameterValue): Mode {
  if (value === undefined) return "inherit";
  if (value === null) return "off";
  return "set";
}



export default function ParameterField({
  name, label, kind, inherited, value, onChange, min, max, hint, testId,
}: Props) {
  const { t } = useTranslation("account");
  const mode = modeOf(value);

  /// What the inherited value reads as under the control. Booleans and "nothing at all" need words
  /// rather than a bare `true` / blank, or the admin cannot tell them apart.
  function describeInherited(): string {
    if (inherited === undefined || inherited === null) return t("llmParamNotSet");
    if (typeof inherited === "boolean") return inherited ? t("llmParamOn") : t("llmParamOff");
    return String(inherited);
  }

  /// Leaving "set" needs a starting value, or the input renders empty and the admin has to type before the
  /// control means anything. Falling back to the inherited value makes overriding a two-click operation
  /// that starts from what is already in effect.
  function startSetting() {
    if (mode === "set") return;
    if (kind === "boolean") onChange(typeof inherited === "boolean" ? inherited : true);
    else if (kind === "text") onChange(typeof inherited === "string" ? inherited : "");
    else onChange(typeof inherited === "number" ? inherited : 0);
  }

  return (
    <div className="border-b py-2 last:border-b-0 dark:border-gray-800">
      <div className="flex items-center justify-between gap-3">
        <label className="text-sm text-gray-700 dark:text-gray-200" htmlFor={testId ?? name}>
          {t(label)}
        </label>
        <div className="flex shrink-0 gap-1 text-xs">
          <button
            type="button"
            aria-pressed={mode === "inherit"}
            onClick={() => onChange(undefined)}
            className={`rounded px-2 py-0.5 ${
              mode === "inherit"
                ? "bg-indigo-600 text-white"
                : "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300"
            }`}
          >
            {t("llmParamInherit")}
          </button>
          <button
            type="button"
            aria-pressed={mode === "off"}
            onClick={() => onChange(null)}
            className={`rounded px-2 py-0.5 ${
              mode === "off"
                ? "bg-indigo-600 text-white"
                : "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300"
            }`}
          >
            {t("llmParamOff")}
          </button>
          <button
            type="button"
            aria-pressed={mode === "set"}
            onClick={startSetting}
            className={`rounded px-2 py-0.5 ${
              mode === "set"
                ? "bg-indigo-600 text-white"
                : "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-300"
            }`}
          >
            {t("llmParamSet")}
          </button>
        </div>
      </div>

      {mode === "set" && (
        <div className="mt-1">
          {kind === "boolean" ? (
            <input
              id={testId ?? name}
              data-testid={testId}
              type="checkbox"
              checked={value === true}
              onChange={(e) => onChange(e.target.checked)}
            />
          ) : kind === "text" ? (
            <input
              id={testId ?? name}
              data-testid={testId}
              type="text"
              value={typeof value === "string" ? value : ""}
              onChange={(e) => onChange(e.target.value)}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-900"
            />
          ) : (
            <input
              id={testId ?? name}
              data-testid={testId}
              type="number"
              step={kind === "integer" ? 1 : "any"}
              min={min}
              max={max}
              value={typeof value === "number" ? String(value) : ""}
              onChange={(e) => {
                const raw = e.target.value;
                if (raw === "") {
                  // An empty box is not a value. Emitting NaN would serialise as null and silently mean
                  // "omit", so hold the field at zero rather than change the instruction behind their back.
                  onChange(0);
                  return;
                }
                const parsed = kind === "integer" ? parseInt(raw, 10) : parseFloat(raw);
                if (!Number.isNaN(parsed)) onChange(parsed);
              }}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-900"
            />
          )}
        </div>
      )}

      <p className="mt-0.5 text-xs text-gray-400 dark:text-gray-500">
        {mode === "inherit"
          ? t("llmParamInheriting", { value: describeInherited() })
          : mode === "off"
            ? t("llmParamNotSent")
            : hint
              ? t(hint)
              : ""}
      </p>
    </div>
  );
}
