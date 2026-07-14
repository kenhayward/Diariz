import { useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import type { MeetingType } from "../lib/types";
import { groupMeetingTypes } from "../lib/meetingTypes";
import { serializeMeetingType, parseMeetingType, exportFilename, resolveFormulaNames } from "../lib/meetingTypeIo";
import MeetingTypeIcon from "./MeetingTypeIcon";
import IconColorPicker from "./IconColorPicker";
import { PrimaryFormulaPicker, AdditionalFormulasPicker } from "./FormulaPicker";

interface Draft {
  id: string | null; // null = a new (unsaved) type
  groupName: string;
  title: string;
  overview: string;
  icon: string;
  color: string;
  primaryFormulaId: string | null;
  additionalFormulaIds: string[];
  isPlatform: boolean;
}

const DEFAULT_COLOR = "#5C6BC0";

function draftFrom(t: MeetingType): Draft {
  return {
    id: t.id, groupName: t.groupName, title: t.title, overview: t.overview,
    icon: t.icon || "document", color: t.color || DEFAULT_COLOR,
    primaryFormulaId: t.primaryFormulaId, additionalFormulaIds: t.additionalFormulaIds, isPlatform: t.isPlatform,
  };
}

function blankDraft(isPlatform: boolean): Draft {
  return {
    id: null, groupName: "", title: "", overview: "", icon: "document", color: DEFAULT_COLOR,
    primaryFormulaId: null, additionalFormulaIds: [], isPlatform,
  };
}

/// The "Manage Meeting Types" editor: a master-detail modal. Left = the templates the caller may see (a Platform
/// Administrator sees all Platform + own; a normal user sees only their own Personal types). Right = an editor for
/// the selected template (title, group, icon/colour, overview, and the H1/H2 sections of blocks). Saves atomically;
/// Cancel reverts. Does not close on an outside click (X or Escape only).
export default function ManageMeetingTypesModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(["workspace", "common"]);
  const { isPlatformAdmin } = useAuth();
  const qc = useQueryClient();
  const { data: types } = useQuery({ queryKey: ["meeting-types"], queryFn: api.listMeetingTypes });
  // The formulas a type may point at. Same list the Formulas picker uses (own Personal + enabled shared).
  const { data: formulas = [] } = useQuery({ queryKey: ["formulas"], queryFn: api.listFormulas });

  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  // The list to show: admins see everything the API returns (Platform + own); others see only their own Personal.
  const visible = useMemo(
    () => (types ?? []).filter((ty) => isPlatformAdmin || !ty.isPlatform),
    [types, isPlatformAdmin],
  );
  const groups = useMemo(() => groupMeetingTypes(visible), [visible]);

  const editable = draft !== null && (draft.id === null || (visible.find((v) => v.id === draft.id)?.canEdit ?? false));

  function select(ty: MeetingType) {
    setError(null);
    setDraft(draftFrom(ty));
  }
  function startNew() {
    setError(null);
    setDraft(blankDraft(false));
  }
  function cancel() {
    setError(null);
    // Edit → reload from the server copy; New → discard.
    const original = draft?.id ? visible.find((v) => v.id === draft.id) : undefined;
    setDraft(original ? draftFrom(original) : null);
  }

  async function save() {
    if (!draft) return;
    setError(null);
    if (!draft.title.trim()) return setError(t("workspace:mtTitleRequired"));
    if (!draft.groupName.trim()) return setError(t("workspace:mtGroupRequired"));
    setBusy(true);
    try {
      const input = {
        groupName: draft.groupName.trim(), title: draft.title.trim(), overview: draft.overview.trim(),
        icon: draft.icon, color: draft.color,
        primaryFormulaId: draft.primaryFormulaId, additionalFormulaIds: draft.additionalFormulaIds,
        isPlatform: draft.isPlatform,
      };
      const saved = draft.id ? await api.updateMeetingType(draft.id, input) : await api.createMeetingType(input);
      await qc.invalidateQueries({ queryKey: ["meeting-types"] });
      setDraft(draftFrom(saved));
    } catch (e) {
      setError(apiErrorMessage(e, t("workspace:mtSaveError")));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!draft?.id || !window.confirm(t("workspace:mtDeleteConfirm"))) return;
    setBusy(true);
    try {
      await api.deleteMeetingType(draft.id);
      await qc.invalidateQueries({ queryKey: ["meeting-types"] });
      setDraft(null);
    } catch (e) {
      setError(apiErrorMessage(e, t("workspace:mtDeleteError")));
    } finally {
      setBusy(false);
    }
  }

  // Export the selected template as a JSON file (portable subset - no id/permission fields).
  const importRef = useRef<HTMLInputElement>(null);
  function exportTemplate() {
    if (!draft) return;
    const json = serializeMeetingType({
      groupName: draft.groupName, title: draft.title, overview: draft.overview,
      icon: draft.icon, color: draft.color,
      // Formula IDs mean nothing on another instance, so the export carries names.
      primaryFormulaName: formulas.find((f) => f.id === draft.primaryFormulaId)?.name ?? null,
      additionalFormulaNames: draft.additionalFormulaIds
        .map((id) => formulas.find((f) => f.id === id)?.name)
        .filter((n): n is string => n != null),
    });
    const url = URL.createObjectURL(new Blob([json], { type: "application/json" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = exportFilename(draft.title);
    a.click();
    URL.revokeObjectURL(url);
  }

  // Import a template from a JSON file: parse it, ask for a name (it may duplicate an existing one), then create
  // it as a Personal type. A Platform Admin can flip it to Platform afterwards.
  async function onImportFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = ""; // allow re-picking the same file later
    if (!file) return;
    setError(null);
    let tpl;
    try {
      tpl = parseMeetingType(await file.text());
    } catch {
      return setError(t("workspace:mtImportError"));
    }
    const name = window.prompt(t("workspace:mtImportNamePrompt"), tpl.title);
    if (name === null) return; // cancelled
    setBusy(true);
    try {
      // Resolve the exported formula names against what THIS instance has; report any it doesn't.
      const primary = resolveFormulaNames(
        tpl.primaryFormulaName ? [tpl.primaryFormulaName] : [], formulas);
      const additional = resolveFormulaNames(tpl.additionalFormulaNames, formulas);
      const missing = [...primary.missing, ...additional.missing];

      const created = await api.createMeetingType({
        groupName: tpl.groupName || t("workspace:mtImportedGroup"),
        title: name.trim() || tpl.title || t("workspace:mtImportedGroup"),
        overview: tpl.overview, icon: tpl.icon, color: tpl.color,
        primaryFormulaId: primary.ids[0] ?? null,
        additionalFormulaIds: additional.ids,
        isPlatform: false,
      });
      await qc.invalidateQueries({ queryKey: ["meeting-types"] });
      setDraft(draftFrom(created));
      if (missing.length > 0) setError(t("workspace:mtImportMissingFormulas", { names: missing.join(", ") }));
    } catch (err) {
      setError(apiErrorMessage(err, t("workspace:mtSaveError")));
    } finally {
      setBusy(false);
    }
  }

  const patch = (p: Partial<Draft>) => setDraft((d) => (d ? { ...d, ...p } : d));

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-label={t("workspace:mtTitle")}
        className="flex h-[85vh] w-[80vw] max-w-6xl flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between border-b px-5 py-3 dark:border-gray-700">
          <h2 className="text-base font-semibold dark:text-gray-100">{t("workspace:mtTitle")}</h2>
          <button
            type="button"
            aria-label={t("common:close")}
            onClick={onClose}
            className="rounded p-1 text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
          >
            ✕
          </button>
        </div>

        <div className="flex min-h-0 flex-1">
          {/* Left: grouped template list + New. */}
          <div className="flex w-64 shrink-0 flex-col border-r dark:border-gray-700">
            <div className="min-h-0 flex-1 overflow-y-auto p-2">
              {groups.map(([groupName, list]) => (
                <div key={groupName} className="mb-2">
                  <div className="px-2 py-1 text-xs font-medium uppercase tracking-wide text-gray-400 dark:text-gray-500">
                    {groupName}
                  </div>
                  {list.map((ty) => (
                    <button
                      key={ty.id}
                      type="button"
                      onClick={() => select(ty)}
                      className={`flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
                        draft?.id === ty.id ? "bg-gray-100 dark:bg-gray-800" : ""
                      }`}
                    >
                      <MeetingTypeIcon icon={ty.icon} color={ty.color} size={18} />
                      <span className="min-w-0 flex-1 truncate text-gray-700 dark:text-gray-200">{ty.title}</span>
                      {ty.isPlatform && (
                        <span className="shrink-0 text-[10px] uppercase text-gray-400">{t("workspace:mtPlatform")}</span>
                      )}
                    </button>
                  ))}
                </div>
              ))}
              {visible.length === 0 && (
                <p className="px-2 py-4 text-xs text-gray-400 dark:text-gray-500">{t("workspace:mtNone")}</p>
              )}
            </div>
            <div className="space-y-1 border-t p-2 dark:border-gray-700">
              <button
                type="button"
                onClick={startNew}
                className="w-full rounded border px-2 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
              >
                + {t("workspace:mtNew")}
              </button>
              <div className="flex gap-1">
                <button
                  type="button"
                  onClick={() => importRef.current?.click()}
                  className="flex-1 rounded border px-2 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                >
                  {t("workspace:mtImport")}
                </button>
                <button
                  type="button"
                  onClick={exportTemplate}
                  disabled={!draft}
                  className="flex-1 rounded border px-2 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-40 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                >
                  {t("workspace:mtExport")}
                </button>
              </div>
              <input
                ref={importRef}
                type="file"
                accept="application/json,.json"
                onChange={onImportFile}
                className="hidden"
                data-testid="import-input"
              />
            </div>
          </div>

          {/* Right: editor. */}
          <div className="min-h-0 flex-1 overflow-y-auto p-5">
            {!draft ? (
              <p className="text-sm text-gray-500 dark:text-gray-400">{t("workspace:mtPickOne")}</p>
            ) : (
              <div className="space-y-4">
                <fieldset disabled={!editable || busy} className="space-y-4">
                  <label className="block text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("workspace:mtFieldTitle")}</span>
                    <input
                      value={draft.title}
                      onChange={(e) => patch({ title: e.target.value })}
                      className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    />
                  </label>
                  <label className="block text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("workspace:mtFieldGroup")}</span>
                    <input
                      value={draft.groupName}
                      onChange={(e) => patch({ groupName: e.target.value })}
                      className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    />
                  </label>

                  {/* Icon + background colour. */}
                  <div className="text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("workspace:mtFieldIcon")}</span>
                    <IconColorPicker
                      icon={draft.icon}
                      color={draft.color}
                      onChange={patch}
                      colorLabel={t("workspace:mtFieldColor")}
                    />
                  </div>

                  <label className="block text-sm">
                    <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("workspace:mtFieldOverview")}</span>
                    <textarea
                      value={draft.overview}
                      onChange={(e) => patch({ overview: e.target.value })}
                      rows={3}
                      className="w-full resize-y rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                    />
                    <span className="mt-0.5 block text-xs text-gray-400 dark:text-gray-500">{t("workspace:mtOverviewHint")}</span>
                  </label>

                  {isPlatformAdmin && (
                    <label className="flex items-center gap-2 text-sm">
                      <input
                        type="checkbox"
                        checked={draft.isPlatform}
                        onChange={(e) => patch({ isPlatform: e.target.checked })}
                      />
                      <span className="text-gray-700 dark:text-gray-200">{t("workspace:mtPlatformSwitch")}</span>
                    </label>
                  )}

                  {/* A meeting type carries no prompts: it names the formula that generates its minutes,
                      plus any run alongside it. Only formulas this type's scope may reference are offered. */}
                  <PrimaryFormulaPicker
                    formulas={formulas}
                    value={draft.primaryFormulaId}
                    isPlatform={draft.isPlatform}
                    disabled={!editable || busy}
                    onChange={(id) => patch({ primaryFormulaId: id })}
                  />
                  <AdditionalFormulasPicker
                    formulas={formulas}
                    value={draft.additionalFormulaIds}
                    isPlatform={draft.isPlatform}
                    disabled={!editable || busy}
                    onChange={(ids) => patch({ additionalFormulaIds: ids })}
                  />
                </fieldset>

                {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

                {editable ? (
                  <div className="flex items-center gap-2 border-t pt-3 dark:border-gray-700">
                    <button
                      type="button"
                      onClick={save}
                      disabled={busy}
                      className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
                    >
                      {busy ? t("common:saving") : t("common:save")}
                    </button>
                    <button
                      type="button"
                      onClick={cancel}
                      disabled={busy}
                      className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                    >
                      {t("common:cancel")}
                    </button>
                    {draft.id && (
                      <button
                        type="button"
                        onClick={remove}
                        disabled={busy}
                        className="ml-auto rounded border border-red-300 px-3 py-1.5 text-sm text-red-600 hover:bg-red-50 disabled:opacity-50 dark:border-red-800 dark:text-red-400 dark:hover:bg-red-950"
                      >
                        {t("common:delete")}
                      </button>
                    )}
                  </div>
                ) : (
                  <p className="border-t pt-3 text-xs text-gray-400 dark:border-gray-700 dark:text-gray-500">
                    {t("workspace:mtReadOnly")}
                  </p>
                )}
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
