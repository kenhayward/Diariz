import { useEffect, useState } from "react";
import CollapsibleSection from "./CollapsibleSection";
import type { RecordingAction } from "../lib/types";

type Patch = { text?: string; actor?: string; deadline?: string };

/// The "by exception" panel below the Summary listing extracted action items as an inline-editable
/// table (Action / Actor / Deadline — all free text). The parent owns persistence: each edit commits
/// on blur via `onUpdate`, and add/remove go through `onAdd`/`onDelete`.
export default function ActionsPanel({
  actions,
  onAdd,
  onUpdate,
  onDelete,
}: {
  actions: RecordingAction[];
  onAdd: () => void;
  onUpdate: (id: string, patch: Patch) => void;
  onDelete: (id: string) => void;
}) {
  return (
    <CollapsibleSection title="Actions">
      {actions.length === 0 ? (
        <p className="mb-3 text-sm text-gray-500 dark:text-gray-400">
          No actions identified — add one below.
        </p>
      ) : (
        <table className="mb-3 w-full table-fixed text-sm">
          <thead>
            <tr className="text-left text-xs font-medium text-gray-400 dark:text-gray-500">
              <th className="w-[55%] pb-1 pr-2 font-medium">Action</th>
              <th className="w-[18%] pb-1 pr-2 font-medium">Actor</th>
              <th className="w-[22%] pb-1 pr-2 font-medium">Deadline</th>
              <th className="w-[5%] pb-1" aria-hidden />
            </tr>
          </thead>
          <tbody>
            {actions.map((a, i) => (
              <ActionRow key={a.id} action={a} row={i + 1} onUpdate={onUpdate} onDelete={onDelete} />
            ))}
          </tbody>
        </table>
      )}

      <button
        type="button"
        onClick={onAdd}
        className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
      >
        + Add action
      </button>
    </CollapsibleSection>
  );
}

function ActionRow({
  action,
  row,
  onUpdate,
  onDelete,
}: {
  action: RecordingAction;
  row: number;
  onUpdate: (id: string, patch: Patch) => void;
  onDelete: (id: string) => void;
}) {
  return (
    <tr className="align-top">
      <td className="py-1 pr-2">
        <Cell label={`Action ${row}`} value={action.text} commit={(v) => onUpdate(action.id, { text: v })} />
      </td>
      <td className="py-1 pr-2">
        <Cell label={`Actor ${row}`} value={action.actor} commit={(v) => onUpdate(action.id, { actor: v })} />
      </td>
      <td className="py-1 pr-2">
        <Cell label={`Deadline ${row}`} value={action.deadline} commit={(v) => onUpdate(action.id, { deadline: v })} />
      </td>
      <td className="py-1 text-right">
        <button
          type="button"
          aria-label={`Remove action ${row}`}
          title="Remove"
          onClick={() => onDelete(action.id)}
          className="rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
        >
          ✕
        </button>
      </td>
    </tr>
  );
}

/// A single editable cell: an input seeded from props that commits on blur only when changed.
function Cell({ label, value, commit }: { label: string; value: string; commit: (v: string) => void }) {
  const [draft, setDraft] = useState(value);
  // Keep the field in sync if the value changes underneath us (e.g. a re-extract).
  useEffect(() => setDraft(value), [value]);

  return (
    <input
      value={draft}
      aria-label={label}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => {
        if (draft !== value) commit(draft.trim());
      }}
      className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
    />
  );
}
