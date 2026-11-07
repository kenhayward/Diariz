import { useState } from "react";

export type AiOperation = "summary" | "minutes" | "actions" | "tags" | "embeddings";
export type AiSelection = Record<AiOperation, boolean>;

const OPTIONS: { key: AiOperation; label: string; description: string }[] = [
  { key: "summary", label: "Riepilogo", description: "Rigenera il riepilogo della trascrizione" },
  { key: "minutes", label: "Verbale", description: "Rigenera il verbale della riunione" },
  { key: "actions", label: "Azioni", description: "Estrae nuovamente le attività dalla trascrizione" },
  { key: "tags", label: "Tag", description: "Aggiorna i tag suggeriti dall'AI" },
  { key: "embeddings", label: "Embeddings", description: "Ricrea gli indici semantici della trascrizione" },
];

export default function RegenerateAiModal({
  busy, summaryEdited, minutesEdited, onCancel, onConfirm,
}: {
  busy: boolean;
  summaryEdited: boolean;
  minutesEdited: boolean;
  onCancel: () => void;
  onConfirm: (selection: AiSelection, overwriteManualEdits: boolean) => Promise<void>;
}) {
  const [selection, setSelection] = useState<AiSelection>({
    summary: false, minutes: false, actions: false, tags: false, embeddings: false,
  });
  const [overwrite, setOverwrite] = useState(false);
  const anySelected = Object.values(selection).some(Boolean);
  const editedSelected = (selection.summary && summaryEdited) || (selection.minutes && minutesEdited);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" role="presentation" onMouseDown={(e) => { if (e.target === e.currentTarget && !busy) onCancel(); }}>
      <div role="dialog" aria-modal="true" aria-labelledby="regenerate-ai-title" className="w-full max-w-lg rounded-xl bg-white p-5 shadow-xl dark:bg-gray-800 dark:text-gray-100">
        <h2 id="regenerate-ai-title" className="text-lg font-semibold">Rigenera elaborazioni AI</h2>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">Seleziona le elaborazioni da eseguire sulla trascrizione esistente. L'audio non verrà trascritto nuovamente.</p>
        <div className="mt-4 space-y-2">
          {OPTIONS.map(({ key, label, description }) => (
            <label key={key} className="flex cursor-pointer items-start gap-3 rounded-lg border border-gray-200 p-3 dark:border-gray-600">
              <input type="checkbox" className="mt-1" checked={selection[key]} disabled={busy} onChange={(e) => setSelection((prev) => ({ ...prev, [key]: e.target.checked }))} />
              <span><span className="block text-sm font-medium">{label}</span><span className="block text-xs text-gray-500 dark:text-gray-400">{description}</span></span>
            </label>
          ))}
        </div>
        {editedSelected && (
          <label className="mt-4 flex items-start gap-2 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-700 dark:bg-amber-950/40 dark:text-amber-200">
            <input type="checkbox" className="mt-1" checked={overwrite} disabled={busy} onChange={(e) => setOverwrite(e.target.checked)} />
            <span>Il riepilogo o il verbale selezionato contiene modifiche manuali. Autorizzo la sostituzione di questi contenuti.</span>
          </label>
        )}
        <div className="mt-5 flex justify-end gap-2">
          <button type="button" disabled={busy} onClick={onCancel} className="rounded-lg border px-4 py-2 text-sm dark:border-gray-600">Annulla</button>
          <button type="button" disabled={busy || !anySelected || (editedSelected && !overwrite)} onClick={() => void onConfirm(selection, editedSelected && overwrite)} className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-40">{busy ? "Accodamento…" : "Avvia elaborazioni"}</button>
        </div>
      </div>
    </div>
  );
}
