import { useTranslation } from "react-i18next";
import ParameterField, { type ParameterValue } from "./ParameterField";
import { PARAMETERS } from "./parameterSchema";

/// One group's worth of parameters. A layer is held as a plain object where a key's ABSENCE and a key's
/// null value mean different things, so this never fills in missing keys - `layer[key]` returning undefined
/// is the "inherit" state and has to stay distinguishable from an explicit null.
export type ParameterLayer = Record<string, ParameterValue>;

interface Props {
  groupKey: string;
  /// i18n key in the `account` namespace, not display text.
  label: string;
  layer: ParameterLayer;
  /// What each parameter resolves to from the layers below this one, so the admin sees what Inherit means
  /// here. For a group panel that is the model's own Defaults; for the Defaults panel it is the app default.
  inherited: ParameterLayer;
  onChange: (layer: ParameterLayer) => void;
}

export default function ParameterPanel({ groupKey, label, layer, inherited, onChange }: Props) {
  const { t } = useTranslation("account");

  function set(key: string, value: ParameterValue) {
    const next = { ...layer };
    // Undefined is not stored as a key - it IS the absence of one. Assigning `next[key] = undefined` would
    // survive into JSON.stringify as a dropped key by luck rather than intent, and would still show up in
    // Object.keys, so delete it explicitly.
    if (value === undefined) delete next[key];
    else next[key] = value;
    onChange(next);
  }

  const overrides = Object.keys(layer).length;

  return (
    <section className="rounded border p-3 dark:border-gray-800">
      <header className="mb-1 flex items-baseline justify-between">
        <h3 className="text-sm font-medium text-gray-800 dark:text-gray-100">{t(label)}</h3>
        <span className="text-xs text-gray-400 dark:text-gray-500">
          {overrides === 0 ? t("llmParamNoOverrides") : t("llmParamCountSet", { count: overrides })}
        </span>
      </header>

      {PARAMETERS.map((p) => (
        <ParameterField
          key={p.key}
          name={p.key}
          label={p.label}
          kind={p.kind}
          min={p.min}
          max={p.max}
          hint={p.hint}
          inherited={inherited[p.key]}
          value={layer[p.key]}
          onChange={(v) => set(p.key, v)}
          testId={`param-${groupKey}-${p.key}`}
        />
      ))}
    </section>
  );
}
