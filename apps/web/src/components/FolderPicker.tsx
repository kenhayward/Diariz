import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { orderedSections } from "../lib/sectionTree";
import { breadcrumbOf } from "../lib/drillView";
import type { SectionDto } from "../lib/types";
import { ArrowLeftIcon, ChevronRightIcon, FolderIcon } from "./icons";
import FolderPath from "./nav/FolderPath";

/// Direct children of a drill position, sorted the same way `orderedSections` walks the tree (manual
/// position, then name) so the drilled level and the flattened whole-tree filter agree on order.
function directChildren(sections: SectionDto[], parentId: string | null): SectionDto[] {
  return sections
    .filter((s) => (s.parentId ?? null) === parentId)
    .sort((a, b) => (a.position ?? 0) - (b.position ?? 0) || a.name.localeCompare(b.name));
}

/// A folder chooser for trees that can nest up to 8 levels deep: type to filter across the *whole* tree,
/// or - with an empty filter - drill one level at a time, mirroring the left nav's own drill-in list
/// (`RecordingsPanel` + `SectionRow` + `DrillBreadcrumb`). Both existing flat pickers (`MoveToSectionModal`'s
/// button list, `RecordingsSection`'s native `<select>`) render this instead, so every folder stays reachable
/// without an 8-deep `Parent > Child > ...` string dominating the row.
///
/// **Choosing vs entering** follows the nav's own split: `SectionRow`'s row body drills deeper rather than
/// opening the folder, and a separate control (the breadcrumb menu's "Open section page") is the one that
/// actually navigates to it. This component keeps that same rule - a folder row's body drills, and picking
/// it is a distinct, explicit control - so drilling all the way down to inspect a folder can never be
/// misread as choosing it. The root and every filtered match have nothing to drill into (root's children
/// *are* the top level already shown; a filtered match is a destination you searched for, not a place to
/// browse from), so for those two cases the row itself doubles as the choose control - there is no second
/// target to be inconsistent with, since there is no drill target at all.
///
/// Filter and drill state are intentionally independent `useState`s: clearing the filter reveals the drill
/// position underneath it, unchanged - filtering never resets, and is never remembered by, where you were
/// browsing. The drill position lives in this component's state, not the URL, unlike the nav's `?in=` -
/// this is a transient picker, not a place to deep-link into.
///
/// **The persisted selection always stays visible**, even when its own row is not part of the current view
/// (a filtered-out match, or a folder several drills below where the picker mounts) - the same guarantee
/// the `<select>` gave for free by always displaying its value. Rather than seeding the drill position from
/// `selectedId`'s parent (`breadcrumbOf`), which would couple this component's browsing state to a second
/// prop, a plain "Selected: {{path}}" line is shown above the list whenever the selection is not otherwise
/// visible - lower risk for a comfort feature, at the cost of an extra line rather than an extra state
/// dependency.
///
/// **Keyboard cost, accepted deliberately.** A `<select>` is one tab stop with arrow-key navigation; this
/// picker is `1 + 1 + 2N` stops (filter box, root row, then a row body and a select button per visible
/// folder) - with 20 top-level folders that is dozens of Tab presses to reach Save. That trade was made on
/// purpose, not overlooked: the filter box is the intended keyboard path for a long list (type instead of
/// tabbing past every row), and roving `tabindex` + arrow-key handling was considered and deliberately not
/// built - more machinery than a comfort feature justifies. Escape while the filter box is focused and
/// non-empty clears it, the one piece of that ergonomics cheap enough to be worth adding.
export default function FolderPicker({
  sections,
  selectedId,
  onSelect,
}: {
  sections: SectionDto[];
  /// `null` selects the root; both consumers show that choice as "Ungrouped".
  selectedId: string | null;
  onSelect: (id: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const [filter, setFilter] = useState("");
  const [drillId, setDrillId] = useState<string | null>(null);

  const query = filter.trim().toLowerCase();
  const filtering = query.length > 0;

  // The whole tree, flattened with each folder's full-path label - shared by the filter (which searches
  // it) and the "persisted selection" notice below (which needs the selected folder's path even when it
  // is nowhere in the current view).
  const allOrdered = useMemo(() => orderedSections(sections), [sections]);

  // Searches the whole tree, never just the current drill level - that is the entire point of typing
  // instead of drilling.
  const matches = useMemo(() => {
    if (!filtering) return [];
    return allOrdered.filter(({ section }) => section.name.toLowerCase().includes(query));
  }, [allOrdered, filtering, query]);

  const children = filtering ? [] : directChildren(sections, drillId);
  // Empty while filtering (drilling is not shown in that mode) or for a `drillId` no longer in `sections`
  // (deleted, or dropped by a refetch, while we were inside it) - `chain.length` alone can't distinguish
  // that from "at the root", which is why the Back button below gates on `drillId`, not on this.
  const chain = filtering || drillId === null ? [] : breadcrumbOf(sections, drillId);

  const rootLabel = t("ungrouped");

  // The root ("Ungrouped") row is always rendered and correctly marked regardless of drill position, so it
  // never needs this notice. Anything else only needs it when its own row isn't part of what's on screen
  // right now - a filtered-out match, or a folder that isn't a direct child of the current drill position.
  const selectedEntry = selectedId !== null ? allOrdered.find((o) => o.section.id === selectedId) : undefined;
  const selectedVisible = filtering
    ? matches.some((m) => m.section.id === selectedId)
    : children.some((c) => c.id === selectedId);
  const showSelectedNotice = selectedEntry !== undefined && !selectedVisible;

  function onFilterKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    // Only when there is something to clear, so an Escape on an already-empty filter box still bubbles to
    // close an enclosing modal instead of being silently swallowed here.
    if (e.key !== "Escape" || !filter) return;
    e.stopPropagation();
    setFilter("");
  }

  return (
    <div>
      {/* `aria-label` alone names this field - it wins over any associated <label> in the accessible-name
          computation, so a wrapping sr-only label here would be dead weight, not a second line of
          defence. */}
      <input
        type="text"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        onKeyDown={onFilterKeyDown}
        placeholder={t("folderPickerFilterPlaceholder")}
        aria-label={t("folderPickerFilterPlaceholder")}
        className="mb-2 w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />

      {/* Gated on `drillId`, not `chain.length` - a `drillId` whose folder is no longer in `sections`
          (deleted, or dropped by a refetch, while we were inside it) yields an empty `chain`, but Back must
          still be reachable or typing in the filter becomes the only way out. */}
      {!filtering && drillId !== null && (
        <div className="mb-1 flex items-center gap-1.5 border-b pb-1.5 dark:border-gray-800">
          <button
            type="button"
            aria-label={t("drillBack")}
            onClick={() => setDrillId(chain.length > 1 ? chain[chain.length - 2].id : null)}
            className="shrink-0 rounded border p-1 text-gray-500 hover:bg-gray-100 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
          >
            <ArrowLeftIcon size={12} />
          </button>
          {/* A crumb click also drills (same rule as the row body below) - the header offers no second,
              differently-behaved way to move around. Renders nothing itself when `chain` is empty (the
              ghost-parent case above) - the Back button alone still gets you out.

              `label`/`menuLabel` overrides are required here, not optional polish: this picker can be open
              over a page whose own nav already renders a "Folder path" landmark (DrillBreadcrumb), and
              FolderPath's own doc comment exists specifically to stop two identically-named landmarks from
              coexisting. */}
          <FolderPath
            crumbs={chain.map((s) => ({ id: s.id, name: s.name }))}
            maxVisible={2}
            onSelect={(id) => setDrillId(id)}
            label={t("folderPickerPathLabel")}
            menuLabel={t("folderPickerPathMenu")}
          />
        </div>
      )}

      {showSelectedNotice && selectedEntry && (
        <p className="mb-1 truncate px-1 text-xs text-gray-500 dark:text-gray-400">
          {t("folderPickerCurrentSelection", { path: selectedEntry.label })}
        </p>
      )}

      <ul role="list" aria-label={t("folderPickerListLabel")} className="max-h-64 overflow-y-auto">
        {filtering ? (
          matches.length === 0 ? (
            <li className="px-2 py-3 text-sm text-gray-500 dark:text-gray-400">{t("folderPickerNoMatches")}</li>
          ) : (
            matches.map(({ section, label }) => (
              <li key={section.id}>
                <ChooseRow label={label} selected={selectedId === section.id} onChoose={() => onSelect(section.id)} />
              </li>
            ))
          )
        ) : (
          <>
            <li>
              <ChooseRow label={rootLabel} selected={selectedId === null} onChoose={() => onSelect(null)} />
            </li>
            {children.length === 0 ? (
              <li className="px-2 py-3 text-sm text-gray-500 dark:text-gray-400">{t("drillEmpty")}</li>
            ) : (
              children.map((s) => (
                <li key={s.id} className="flex items-center gap-1">
                  <button
                    type="button"
                    onClick={() => setDrillId(s.id)}
                    aria-label={t("drillOpenFolder", { name: s.name })}
                    className="flex min-w-0 flex-1 items-center gap-1.5 rounded px-2 py-1.5 text-left text-sm hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
                  >
                    <span className="shrink-0 text-gray-400 dark:text-gray-500">
                      <FolderIcon size={14} />
                    </span>
                    <span className="min-w-0 flex-1 truncate">{s.name}</span>
                    <span className="shrink-0 text-gray-400 dark:text-gray-600">
                      <ChevronRightIcon size={12} />
                    </span>
                  </button>
                  <SelectButton name={s.name} selected={selectedId === s.id} onChoose={() => onSelect(s.id)} />
                </li>
              ))
            )}
          </>
        )}
      </ul>
    </div>
  );
}

/// The row IS the choose control (used for the root and every filtered match, neither of which has
/// anything to drill into) - so its accessible name replaces the visible "{{label}} (✓)" with an explicit
/// "Select {{label}}", the same way `selectRecordingAria` names an implicitly-actioned row elsewhere in
/// this codebase. `aria-current` (not colour alone) conveys the selected state to assistive tech.
function ChooseRow({ label, selected, onChoose }: { label: string; selected: boolean; onChoose: () => void }) {
  const { t } = useTranslation("workspace");
  return (
    <button
      type="button"
      onClick={onChoose}
      aria-current={selected ? "true" : undefined}
      aria-label={t("folderPickerSelectAria", { name: label })}
      className={`block w-full rounded px-2 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800 ${
        selected ? "font-medium text-blue-700 dark:text-blue-400" : "dark:text-gray-200"
      }`}
    >
      {label} {selected && <span aria-hidden>✓</span>}
    </button>
  );
}

/// The second, explicit target on a drill-level row - deliberately separate from the row body above, which
/// drills. Icon-only by design (the row body already shows the name), so its accessible name carries the
/// folder's name on its own; `aria-current` conveys the selected state to assistive tech, not colour alone.
///
/// This is the row's *primary* action (choosing a folder is the point of the picker), and the gesture that
/// used to live on the row body before drilling took it over - so its resting state carries a real border
/// and hover background rather than reading as a faint decorative tick. The resting text colour
/// (`text-gray-500` / `dark:text-gray-400`) clears WCAG 2.1 SC 1.4.11's 3:1 minimum for a graphical control
/// with room to spare (~4.8:1 on white, ~7:1 on `dark:bg-gray-900`) - the previous `text-gray-300` /
/// `dark:text-gray-600` sat at roughly 1.5:1 / 2.4:1.
function SelectButton({ name, selected, onChoose }: { name: string; selected: boolean; onChoose: () => void }) {
  const { t } = useTranslation("workspace");
  return (
    <button
      type="button"
      onClick={onChoose}
      aria-current={selected ? "true" : undefined}
      aria-label={t("folderPickerSelectAria", { name })}
      className={`shrink-0 rounded border px-2 py-1.5 text-sm ${
        selected
          ? "border-blue-200 text-blue-700 dark:border-blue-900 dark:text-blue-400"
          : "border-gray-200 text-gray-500 hover:bg-gray-100 hover:text-gray-700 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800 dark:hover:text-gray-200"
      }`}
    >
      <span aria-hidden>✓</span>
    </button>
  );
}
