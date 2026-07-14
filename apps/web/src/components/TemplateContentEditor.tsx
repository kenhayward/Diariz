import { useEffect, useRef } from "react";
import type { TemplateContent, TemplateBlock, TemplateBlockKind } from "../lib/types";
import {
  FIELD_OPTIONS, addSection, removeSection, updateSection, moveSection,
  addBlock, removeBlock, updateBlock, moveBlock, moveBlockCrossSection,
} from "../lib/meetingTypeDraft";
import KebabMenu from "./KebabMenu";

/// The structured template editor: sections of typed blocks (heading / literal text / substituted recording
/// field / model prompt / horizontal rule), reorderable by drag handle or the per-item kebab.
///
/// It was built for meeting-minutes templates, and now serves formulas too - because a formula IS a template.
/// A meeting type no longer has a template of its own (it points at the formula that generates its minutes), so
/// this lives on its own rather than inside the meeting-type modal.

/// The sections + blocks editor. Reorder via a drag handle (HTML5 DnD) or the per-item kebab (move up/down).
export default function TemplateContentEditor({
  content,
  onChange,
  t,
}: {
  content: TemplateContent;
  onChange: (c: TemplateContent) => void;
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
                onChange={(e) => onChange(updateSection(content, si, { level: Number(e.target.value) }))}
                aria-label={t("workspace:mtHeadingLevel")}
                className="rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              >
                {/* Level 0 = no heading: the body alone. It is the shape a formula that is just a prompt takes,
                    so the editor has to be able to show and keep it. */}
                <option value={0}>{t("workspace:mtLevelNone")}</option>
                <option value={1}>H1</option>
                <option value={2}>H2</option>
                <option value={3}>H3</option>
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
                  { label: t("workspace:mtAddHr"), onClick: () => onChange(addBlock(content, si, "hr")) },
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
    : kind === "hr" ? t("workspace:mtKindHr")
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
        ) : kind === "hr" ? (
          // A horizontal rule has nothing to edit - show the line it renders as.
          <div className="my-2 border-t border-gray-300 dark:border-gray-600" aria-hidden="true" />
        ) : (
          <AutoGrowTextarea
            value={text}
            onChange={onText}
            placeholder={kind === "prompt" ? t("workspace:mtPromptPlaceholder") : t("workspace:mtBoilerplatePlaceholder")}
            ariaLabel={label}
          />
        )}
      </div>
      {/* A rule always sits on its own paragraph (the composer forces it), so it has no break-after choice. */}
      {kind !== "hr" && (
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
      )}
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
