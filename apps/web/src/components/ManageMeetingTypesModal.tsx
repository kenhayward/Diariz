import { useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";
import type { MeetingType, MeetingTypeContent, TemplateBlock, TemplateBlockKind } from "../lib/types";
import { groupMeetingTypes } from "../lib/meetingTypes";
import { serializeMeetingType, parseMeetingType, exportFilename } from "../lib/meetingTypeIo";
import {
  FIELD_OPTIONS, addSection, removeSection, updateSection, moveSection,
  addBlock, removeBlock, updateBlock, moveBlock, moveBlockCrossSection, normalizeBreaks,
  contentError, emptyContent,
} from "../lib/meetingTypeDraft";
import { MEETING_TYPE_ICONS } from "./MeetingTypeIcon";
import MeetingTypeIcon from "./MeetingTypeIcon";
import KebabMenu from "./KebabMenu";

interface Draft {
  id: string | null; // null = a new (unsaved) type
  groupName: string;
  title: string;
  overview: string;
  icon: string;
  color: string;
  content: MeetingTypeContent;
  isPlatform: boolean;
}

const DEFAULT_COLOR = "#5C6BC0";

function draftFrom(t: MeetingType): Draft {
  return {
    id: t.id, groupName: t.groupName, title: t.title, overview: t.overview,
    icon: t.icon || "document", color: t.color || DEFAULT_COLOR, content: normalizeBreaks(t.content), isPlatform: t.isPlatform,
  };
}

function blankDraft(isPlatform: boolean): Draft {
  return {
    id: null, groupName: "", title: "", overview: "", icon: "document", color: DEFAULT_COLOR,
    content: emptyContent(), isPlatform,
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
    const ce = contentError(draft.content);
    if (ce) return setError(t(`workspace:${ce}`));

    setBusy(true);
    try {
      const input = {
        groupName: draft.groupName.trim(), title: draft.title.trim(), overview: draft.overview.trim(),
        icon: draft.icon, color: draft.color, content: draft.content, isPlatform: draft.isPlatform,
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
      icon: draft.icon, color: draft.color, content: draft.content,
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
      const created = await api.createMeetingType({
        groupName: tpl.groupName || t("workspace:mtImportedGroup"),
        title: name.trim() || tpl.title || t("workspace:mtImportedGroup"),
        overview: tpl.overview, icon: tpl.icon, color: tpl.color, content: tpl.content, isPlatform: false,
      });
      await qc.invalidateQueries({ queryKey: ["meeting-types"] });
      setDraft(draftFrom(created));
    } catch (err) {
      setError(apiErrorMessage(err, t("workspace:mtSaveError")));
    } finally {
      setBusy(false);
    }
  }

  const patch = (p: Partial<Draft>) => setDraft((d) => (d ? { ...d, ...p } : d));
  const setContent = (content: MeetingTypeContent) => patch({ content });

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
                    <div className="flex items-center gap-2">
                      <input
                        type="color"
                        value={draft.color}
                        onChange={(e) => patch({ color: e.target.value })}
                        aria-label={t("workspace:mtFieldColor")}
                        className="h-8 w-8 shrink-0 cursor-pointer rounded border p-0.5 dark:border-gray-700 dark:bg-gray-800"
                      />
                      <div className="flex flex-wrap gap-1">
                        {MEETING_TYPE_ICONS.map((icon) => (
                          <button
                            key={icon}
                            type="button"
                            aria-label={icon}
                            aria-pressed={draft.icon === icon}
                            onClick={() => patch({ icon })}
                            className={`rounded p-0.5 ${draft.icon === icon ? "ring-2 ring-indigo-500" : ""}`}
                          >
                            <MeetingTypeIcon icon={icon} color={draft.color} size={22} />
                          </button>
                        ))}
                      </div>
                    </div>
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

                  {/* Content: sections + blocks. */}
                  <ContentEditor content={draft.content} onChange={setContent} t={t} />
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

/// The sections + blocks editor. Reorder via a drag handle (HTML5 DnD) or the per-item kebab (move up/down).
function ContentEditor({
  content,
  onChange,
  t,
}: {
  content: MeetingTypeContent;
  onChange: (c: MeetingTypeContent) => void;
  t: (k: string) => string;
}) {
  const dragSection = useRef<number | null>(null);
  const dragBlock = useRef<{ section: number; index: number } | null>(null);

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <div className="mb-2 text-sm font-medium text-gray-700 dark:text-gray-200">{t("workspace:mtContent")}</div>
      <div className="space-y-3">
        {content.sections.map((section, si) => (
          <div
            key={si}
            className="rounded border dark:border-gray-700"
            onDragOver={(e) => e.preventDefault()}
            onDrop={() => {
              if (dragSection.current !== null && dragSection.current !== si) {
                onChange(moveSection(content, dragSection.current, si));
              }
              dragSection.current = null;
            }}
          >
            <div className="flex items-center gap-2 border-b bg-gray-50 px-2 py-1.5 dark:border-gray-700 dark:bg-gray-800">
              <span
                draggable
                onDragStart={() => (dragSection.current = si)}
                aria-label={t("workspace:mtDragSection")}
                className="cursor-grab select-none text-gray-400"
              >
                ⠿
              </span>
              <select
                value={section.level}
                onChange={(e) => onChange(updateSection(content, si, { level: Number(e.target.value) as 1 | 2 }))}
                aria-label={t("workspace:mtHeadingLevel")}
                className="rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              >
                <option value={1}>H1</option>
                <option value={2}>H2</option>
              </select>
              <input
                value={section.title}
                onChange={(e) => onChange(updateSection(content, si, { title: e.target.value }))}
                placeholder={t("workspace:mtSectionTitle")}
                aria-label={t("workspace:mtSectionTitle")}
                className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-900 dark:text-gray-100"
              />
              <KebabMenu
                label={t("workspace:mtSectionActions")}
                actions={[
                  { label: t("workspace:mtAddBoilerplate"), onClick: () => onChange(addBlock(content, si, "boilerplate")) },
                  { label: t("workspace:mtAddField"), onClick: () => onChange(addBlock(content, si, "field")) },
                  { label: t("workspace:mtAddPrompt"), onClick: () => onChange(addBlock(content, si, "prompt")) },
                  { label: t("workspace:mtMoveUp"), onClick: () => onChange(moveSection(content, si, si - 1)), disabled: si === 0 },
                  { label: t("workspace:mtMoveDown"), onClick: () => onChange(moveSection(content, si, si + 1)), disabled: si === content.sections.length - 1 },
                  { label: t("workspace:mtDeleteSection"), onClick: () => onChange(removeSection(content, si)), danger: true },
                ]}
              />
            </div>

            <div
              className="space-y-2 p-2"
              onDragOver={(e) => e.preventDefault()}
              onDrop={(e) => {
                if (dragBlock.current) {
                  e.stopPropagation();
                  onChange(moveBlockCrossSection(content, dragBlock.current, { section: si, index: section.blocks.length }));
                }
                dragBlock.current = null;
              }}
            >
              {section.blocks.length === 0 && (
                <p className="text-xs text-gray-400 dark:text-gray-500">{t("workspace:mtNoBlocks")}</p>
              )}
              {section.blocks.map((block, bi) => (
                <BlockRow
                  key={bi}
                  section={si}
                  index={bi}
                  kind={block.kind}
                  text={block.text ?? ""}
                  field={block.field ?? "date"}
                  breakAfter={block.breakAfter ?? "paragraph"}
                  count={section.blocks.length}
                  t={t}
                  onText={(text) => onChange(updateBlock(content, si, bi, { text }))}
                  onField={(field) => onChange(updateBlock(content, si, bi, { field }))}
                  onBreakAfter={(breakAfter) => onChange(updateBlock(content, si, bi, { breakAfter: breakAfter as TemplateBlock["breakAfter"] }))}
                  onDragStart={() => { dragBlock.current = { section: si, index: bi }; dragSection.current = null; }}
                  onBlockDrop={() => {
                    if (dragBlock.current) onChange(moveBlockCrossSection(content, dragBlock.current, { section: si, index: bi }));
                    dragBlock.current = null;
                  }}
                  onMove={(to) => onChange(moveBlock(content, si, bi, to))}
                  onRemove={() => onChange(removeBlock(content, si, bi))}
                />
              ))}
            </div>
          </div>
        ))}
      </div>
      <button
        type="button"
        onClick={() => onChange(addSection(content, 1))}
        className="mt-2 rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
      >
        + {t("workspace:mtAddSection")}
      </button>
    </div>
  );
}

/// A textarea that grows to fit its content (no inner scrollbar), for the raw-markdown block text.
function AutoGrowTextarea({
  value, onChange, placeholder, ariaLabel,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  ariaLabel: string;
}) {
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }, [value]);
  return (
    <textarea
      ref={ref}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      aria-label={ariaLabel}
      rows={1}
      className="w-full resize-none overflow-hidden rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
    />
  );
}

function BlockRow({
  kind, text, field, breakAfter, index, count, t, onText, onField, onBreakAfter, onDragStart, onBlockDrop, onMove, onRemove,
}: {
  section: number;
  index: number;
  count: number;
  kind: TemplateBlockKind;
  text: string;
  field: string;
  breakAfter: string;
  t: (k: string) => string;
  onText: (v: string) => void;
  onField: (v: string) => void;
  onBreakAfter: (v: string) => void;
  onDragStart: () => void;
  onBlockDrop: () => void;
  onMove: (to: number) => void;
  onRemove: () => void;
}) {
  const label =
    kind === "boilerplate" ? t("workspace:mtKindBoilerplate")
    : kind === "field" ? t("workspace:mtKindField")
    : t("workspace:mtKindPrompt");

  return (
    <div
      className="flex items-start gap-2 rounded border px-2 py-1.5 dark:border-gray-700"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => { e.preventDefault(); e.stopPropagation(); onBlockDrop(); }}
    >
      <span
        draggable
        onDragStart={onDragStart}
        aria-label={t("workspace:mtDragBlock")}
        title={t("workspace:mtDragBlock")}
        className="mt-1 cursor-grab select-none text-gray-400"
      >
        ⠿
      </span>
      <span className="mt-1 w-16 shrink-0 text-xs font-medium uppercase text-gray-400">{label}</span>
      <div className="min-w-0 flex-1">
        {kind === "field" ? (
          <select
            value={field}
            onChange={(e) => onField(e.target.value)}
            aria-label={t("workspace:mtKindField")}
            className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          >
            {FIELD_OPTIONS.map((f) => (
              <option key={f} value={f}>{t(`workspace:mtFieldOpt_${f}`)}</option>
            ))}
          </select>
        ) : (
          <AutoGrowTextarea
            value={text}
            onChange={onText}
            placeholder={kind === "prompt" ? t("workspace:mtPromptPlaceholder") : t("workspace:mtBoilerplatePlaceholder")}
            ariaLabel={label}
          />
        )}
      </div>
      <select
        value={breakAfter}
        onChange={(e) => onBreakAfter(e.target.value)}
        aria-label={t("workspace:mtBreakAfter")}
        title={t("workspace:mtBreakAfter")}
        className="mt-1 shrink-0 rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      >
        <option value="none">{t("workspace:mtBreakNone")}</option>
        <option value="line">{t("workspace:mtBreakLine")}</option>
        <option value="paragraph">{t("workspace:mtBreakParagraph")}</option>
      </select>
      <KebabMenu
        label={t("workspace:mtBlockActions")}
        actions={[
          { label: t("workspace:mtMoveUp"), onClick: () => onMove(index - 1), disabled: index === 0 },
          { label: t("workspace:mtMoveDown"), onClick: () => onMove(index + 1), disabled: index === count - 1 },
          { label: t("workspace:mtDeleteBlock"), onClick: onRemove, danger: true },
        ]}
      />
    </div>
  );
}
