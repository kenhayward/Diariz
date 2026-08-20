import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../../lib/api";
import type { LlmTestRecording, RecordingStatus, RecordingSummary } from "../../lib/types";

interface Props {
  value: LlmTestRecording;
  onChange: (recordingId: string, title: string) => void;
  disabled?: boolean;
}

/// Statuses whose latest transcription has segments. Anything earlier has no transcript to test against,
/// and offering it would only produce an error from the server (which validates this again - the filter is
/// for the administrator's benefit, not a security boundary).
const TESTABLE = new Set<RecordingStatus>(["Transcribed", "Summarized", "Summarizing", "Merging"]);

/// The recording a model test runs against.
///
/// Scoped to the administrator's OWN recordings, which is what `listRecordings` returns - their personal
/// room. That is a privacy boundary, not a convenience: the transcript is sent to whatever third-party
/// endpoint is being tested, and its reply comes back on screen.
export default function RecordingPicker({ value, onChange, disabled }: Props) {
  const { t } = useTranslation("account");
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [recordings, setRecordings] = useState<RecordingSummary[] | null>(null);
  const box = useRef<HTMLDivElement>(null);

  // Fetched on first open, not on mount: the four sample-transcript tabs never render this at all, and most
  // visits to the drawer never touch the picker.
  useEffect(() => {
    if (!open || recordings !== null) return;
    let cancelled = false;
    api
      .listRecordings()
      .then((rows) => {
        if (!cancelled) setRecordings(rows);
      })
      .catch(() => {
        // An empty list renders the "nothing to test against" line, which is the useful thing to say either
        // way. A failed fetch is not worth its own banner inside a dropdown.
        if (!cancelled) setRecordings([]);
      });
    return () => {
      cancelled = true;
    };
  }, [open, recordings]);

  useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (!box.current?.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  const label = (r: RecordingSummary) => r.name ?? r.title;

  const testable = useMemo(
    () => (recordings ?? []).filter((r) => TESTABLE.has(r.status)),
    [recordings],
  );
  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? testable.filter((r) => label(r).toLowerCase().includes(q)) : testable;
  }, [testable, query]);

  return (
    <div ref={box} className="relative min-w-0">
      <button
        type="button"
        disabled={disabled}
        onClick={() => setOpen((o) => !o)}
        className="flex w-full min-w-0 items-center justify-between gap-2 rounded-md border border-gray-300 px-2.5 py-1 text-left text-xs disabled:opacity-60 dark:border-gray-700"
      >
        <span
          className={`block min-w-0 flex-1 truncate ${
            value.title ? "" : "text-gray-400 dark:text-gray-500"
          }`}
        >
          {value.title ?? t("llmTestChooseRecording")}
        </span>
        <span aria-hidden className="shrink-0 text-[9px] text-gray-400 dark:text-gray-500">
          &#9660;
        </span>
      </button>

      {open && !disabled && (
        <div className="absolute z-20 mt-1 w-full overflow-hidden rounded-md border border-gray-200 bg-white shadow-lg dark:border-gray-800 dark:bg-gray-900">
          <input
            type="text"
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t("llmTestSearchRecordings")}
            className="w-full border-b border-gray-100 px-2.5 py-1.5 text-xs outline-none dark:border-gray-800 dark:bg-gray-900"
          />
          <ul className="max-h-[220px] overflow-auto">
            {matches.length === 0 && (
              <li className="px-2.5 py-2 text-[11px] text-gray-500 dark:text-gray-400">
                {t("llmTestNoRecordings")}
              </li>
            )}
            {matches.map((r) => (
              <li key={r.id}>
                <button
                  type="button"
                  onClick={() => {
                    onChange(r.id, label(r));
                    setOpen(false);
                    setQuery("");
                  }}
                  className="block w-full min-w-0 px-2.5 py-1.5 text-left text-xs hover:bg-slate-50 dark:hover:bg-gray-800"
                >
                  {/* Block, not inline: `truncate` on an inline element only sets white-space:nowrap, so the
                      text can neither wrap nor ellipsise and a long name overflows the drawer sideways. */}
                  <span className="block truncate">{label(r)}</span>
                  <span className="block truncate text-[10.5px] text-gray-400 dark:text-gray-500">
                    {new Date(r.createdAt).toLocaleDateString()}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
