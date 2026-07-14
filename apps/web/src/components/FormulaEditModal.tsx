import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { normalizeBreaks, contentError, emptyContent } from "../lib/meetingTypeDraft";
import TemplateContentEditor from "./TemplateContentEditor";
import { FormulaContextBits, type Formula, type FormulaScope } from "../lib/types";
import FlaskIcon from "./FlaskIcon";

// Attachments (FormulaContextBits.Attachments) is intentionally omitted until attachment extraction
// ships in a later phase - FormulaContextBuilder ignores the flag today, so surfacing it would be a no-op.
const CONTEXT_OPTIONS: { bit: number; key: string }[] = [
  { bit: FormulaContextBits.Transcript, key: "contextTranscript" },
  { bit: FormulaContextBits.Notes, key: "contextNotes" },
  { bit: FormulaContextBits.Summary, key: "contextSummary" },
  { bit: FormulaContextBits.Minutes, key: "contextMinutes" },
  { bit: FormulaContextBits.Actions, key: "contextActions" },
];

/// Create/edit a formula (form-as-dialog). Escape or Cancel close it - deliberately NOT a backdrop click,
/// so an in-progress prompt isn't lost to a stray click. Save is disabled until the required fields (name,
/// prompt) are filled. Context is a [Flags] bitmask - each checkbox XORs its bit (see FormulaContextBits).
/// When `formula` is omitted a new formula is created in `scope` (default "Personal" - Preferences ->
/// Formulas creates only Personal formulas; the admin Manage Formulas popup passes "Platform"). When
/// `formula` is given, its own (immutable) scope is edited - `scope` is ignored.
export default function FormulaEditModal({
  formula,
  scope = "Personal",
  onClose,
  onSaved,
}: {
  formula?: Formula | null;
  scope?: FormulaScope;
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t } = useTranslation("account");
  const [name, setName] = useState(formula?.name ?? "");
  const [description, setDescription] = useState(formula?.description ?? "");
  // A formula IS a template: it is authored with the same structured block editor meeting-minutes templates
  // use. (A formula that was just a prompt shows as one headless section holding one prompt block.)
  const [content, setContent] = useState(() =>
    formula ? normalizeBreaks(formula.content) : emptyContent());
  const [context, setContext] = useState(formula?.context ?? 0);
  const [shared, setShared] = useState(formula?.shared ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isPersonal = formula ? formula.scope === "Personal" : scope === "Personal";

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  function toggle(bit: number) {
    setContext((c) => c ^ bit);
  }

  // A formula with no sections would generate nothing, so it is not saveable - the same bar the meeting-type
  // editor applied to a template.
  const canSave = !!name.trim() && content.sections.length > 0 && contentError(content) === null;

  async function save(e: React.FormEvent) {
    e.preventDefault();
    if (!canSave) return;
    setBusy(true);
    setError(null);
    try {
      if (formula) {
        await api.updateFormula(formula.id, {
          name: name.trim(),
          description: description.trim() || null,
          content,
          context,
          shared,
        });
      } else {
        await api.createFormula({
          scope,
          name: name.trim(),
          description: description.trim() || null,
          content,
          context,
          shared,
        });
      }
      onSaved();
      onClose();
    } catch (err) {
      setError(apiErrorMessage(err));
      setBusy(false);
    }
  }

  const field = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
  const labelSpan = "mb-1 block text-gray-600 dark:text-gray-300";
  const title = formula ? t("editFormulaTitle") : scope !== "Personal" ? t("newPlatformFormulaTitle") : t("newFormulaTitle");

  // Does NOT close on a backdrop click (Escape or Cancel only) - avoids losing an in-progress edit.
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <form
        role="dialog"
        aria-label={title}
        className="flex max-h-[85vh] w-full max-w-4xl flex-col space-y-3 overflow-y-auto rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onSubmit={save}
      >
        <h2 className="flex items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {title}
        </h2>

        <label className="block text-sm">
          <span className={labelSpan}>{t("formulaName")}</span>
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} className={field} />
        </label>

        <label className="block text-sm">
          <span className={labelSpan}>{t("formulaDescription")}</span>
          <input value={description} onChange={(e) => setDescription(e.target.value)} className={field} />
        </label>

        <div className="block text-sm">
          <span className={labelSpan}>{t("formulaPrompt")}</span>
          <TemplateContentEditor content={content} onChange={setContent} t={t} />
        </div>

        <div>
          <span className={`text-sm ${labelSpan}`}>{t("formulaContext")}</span>
          <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("formulaContextHint")}</p>
          <div className="flex flex-wrap gap-x-4 gap-y-1.5">
            {CONTEXT_OPTIONS.map((opt) => (
              <label key={opt.bit} className="flex items-center gap-2 text-sm dark:text-gray-200">
                <input type="checkbox" checked={(context & opt.bit) !== 0} onChange={() => toggle(opt.bit)} />
                <span>{t(opt.key)}</span>
              </label>
            ))}
          </div>
        </div>

        {isPersonal && (
          <label className="flex items-start gap-2 text-sm dark:text-gray-200">
            <input type="checkbox" checked={shared} onChange={(e) => setShared(e.target.checked)} className="mt-0.5" />
            <span>
              <span className="font-medium">{t("formulaShared")}</span>
              <span className="block text-xs text-gray-400 dark:text-gray-500">{t("formulaSharedHint")}</span>
            </span>
          </label>
        )}

        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="flex justify-end gap-2 border-t pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="submit"
            disabled={busy || !canSave}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {t("common:save")}
          </button>
        </div>
      </form>
    </div>
  );
}
