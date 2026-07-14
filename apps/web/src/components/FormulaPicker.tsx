import { useTranslation } from "react-i18next";
import type { Formula } from "../lib/types";

/// The formulas a meeting type of this scope is ALLOWED to reference.
///
/// Minutes generate as the recording owner, and a Personal formula can only be run by its owner - so a shared
/// (Platform) meeting type pointing at someone's Personal formula would produce no minutes for everyone else.
/// The server refuses that at save; this stops it being offered in the first place, so the rule is visible
/// rather than a surprise 400.
export function referenceable(formulas: Formula[], isPlatform: boolean): Formula[] {
  return formulas.filter((f) =>
    isPlatform ? f.scope !== "Personal" && f.enabled : f.scope === "Personal" || f.enabled);
}

/// The formula whose template generates the minutes.
export function PrimaryFormulaPicker({
  formulas,
  value,
  isPlatform,
  disabled,
  onChange,
}: {
  formulas: Formula[];
  value: string | null;
  isPlatform: boolean;
  disabled?: boolean;
  onChange: (id: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const options = referenceable(formulas, isPlatform);

  return (
    <label className="block text-sm">
      <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("mtPrimaryFormula")}</span>
      <select
        value={value ?? ""}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value || null)}
        className="w-full rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      >
        <option value="">{t("mtPrimaryFormulaNone")}</option>
        {options.map((f) => (
          <option key={f.id} value={f.id}>
            {f.name}
          </option>
        ))}
      </select>
      <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("mtPrimaryFormulaHint")}</span>
    </label>
  );
}

/// Formulas run alongside the minutes whenever this type generates them; their results land in the Formulas tab.
export function AdditionalFormulasPicker({
  formulas,
  value,
  isPlatform,
  disabled,
  onChange,
}: {
  formulas: Formula[];
  value: string[];
  isPlatform: boolean;
  disabled?: boolean;
  onChange: (ids: string[]) => void;
}) {
  const { t } = useTranslation("workspace");
  const options = referenceable(formulas, isPlatform);

  const toggle = (id: string) =>
    onChange(value.includes(id) ? value.filter((x) => x !== id) : [...value, id]);

  return (
    <fieldset className="block text-sm">
      <legend className="mb-1 block text-gray-600 dark:text-gray-300">{t("mtAdditionalFormulas")}</legend>
      <div className="max-h-40 space-y-1 overflow-y-auto rounded border p-2 dark:border-gray-700">
        {options.length === 0 && (
          <p className="text-xs text-gray-400 dark:text-gray-500">{t("mtNoFormulas")}</p>
        )}
        {options.map((f) => (
          <label key={f.id} className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={value.includes(f.id)}
              disabled={disabled}
              onChange={() => toggle(f.id)}
            />
            <span className="text-gray-700 dark:text-gray-200">{f.name}</span>
          </label>
        ))}
      </div>
      <span className="mt-1 block text-xs text-gray-400 dark:text-gray-500">{t("mtAdditionalFormulasHint")}</span>
    </fieldset>
  );
}
