import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import { useActiveRecordingId } from "../lib/useActiveRecordingId";
import { useSelection } from "../lib/selection";
import type { ChatConversationSummary, ChatTurn, ChatUsage } from "../lib/types";
import ContextDial from "./ContextDial";

type ContextMode = "current" | "selected" | "none";

/// Right-panel chat: ask questions over one or more transcripts, attach a PDF/text file as extra
/// context, watch the reply stream in, and save / reload / delete conversations.
export default function ChatPanel() {
  const activeId = useActiveRecordingId();
  const selection = useSelection();
  // The model's context-window size for the dial (per-user override, else server default).
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  const [messages, setMessages] = useState<ChatTurn[]>([]);
  const [input, setInput] = useState("");
  const [streaming, setStreaming] = useState(false);
  const [usage, setUsage] = useState<ChatUsage | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [contextMode, setContextMode] = useState<ContextMode>("current");
  const [pickerOpen, setPickerOpen] = useState(false);

  const [attachment, setAttachment] = useState<{ name: string; text: string; chars: number } | null>(null);
  const [uploading, setUploading] = useState(false);

  const [savedList, setSavedList] = useState<ChatConversationSummary[]>([]);
  const [listOpen, setListOpen] = useState(false);
  const [openedId, setOpenedId] = useState<string | null>(null);
  const [saveStatus, setSaveStatus] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const abortRef = useRef<AbortController | null>(null);
  const threadRef = useRef<HTMLDivElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const savedRef = useRef<HTMLDivElement>(null);
  const pickerRef = useRef<HTMLDivElement>(null);

  // Close the saved-conversations / context dropdowns on an outside click or Escape.
  useEffect(() => {
    if (!listOpen && !pickerOpen) return;
    function onDown(e: MouseEvent) {
      if (listOpen && savedRef.current && !savedRef.current.contains(e.target as Node)) setListOpen(false);
      if (pickerOpen && pickerRef.current && !pickerRef.current.contains(e.target as Node)) setPickerOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") {
        setListOpen(false);
        setPickerOpen(false);
      }
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [listOpen, pickerOpen]);

  const started = messages.length > 0;

  // The transcripts used as context, from the chosen mode: the recording open in the middle panel,
  // the ones ticked via the list's Select mode, or none.
  const recordingIds =
    contextMode === "current"
      ? activeId
        ? [activeId]
        : []
      : contextMode === "selected"
        ? selection.selectedIds
        : [];

  // Dial: show the configured context window from the start (used 0), then the live figures the
  // server reports on each turn via the meta/done events.
  const totalContext = settings ? settings.contextWindow ?? settings.defaultContextWindow : 0;
  const dialTotal = usage?.contextTotal || totalContext;
  const dialUsed = usage?.contextUsed ?? 0;
  const dialModel = usage?.model || settings?.model || settings?.defaultModel || "";

  // Keep the thread scrolled to the newest message as tokens stream in.
  useEffect(() => {
    const el = threadRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages]);

  function appendToken(token: string) {
    setMessages((prev) => {
      if (prev.length === 0) return prev;
      const next = prev.slice();
      const last = next[next.length - 1];
      if (last.role === "assistant") next[next.length - 1] = { ...last, content: last.content + token };
      return next;
    });
  }

  function setLastAssistant(content: string) {
    setMessages((prev) => {
      const next = prev.slice();
      const last = next[next.length - 1];
      if (last?.role === "assistant") next[next.length - 1] = { ...last, content };
      return next;
    });
  }

  function send() {
    const prompt = input.trim();
    if (!prompt || streaming) return;
    setError(null);
    setSaveStatus(null);

    const history: ChatTurn[] = [...messages, { role: "user", content: prompt }];
    setMessages([...history, { role: "assistant", content: "" }]);
    setInput("");
    setStreaming(true);
    setPickerOpen(false);

    const controller = new AbortController();
    abortRef.current = controller;
    api
      .chatStream(
        {
          recordingIds,
          attachmentName: attachment?.name ?? null,
          attachmentText: attachment?.text ?? null,
          messages: history,
        },
        { onToken: appendToken, onMeta: setUsage, signal: controller.signal },
      )
      .then((u) => {
        if (!controller.signal.aborted) setUsage(u);
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setLastAssistant(`[${apiErrorMessage(e, "Chat failed.")}]`);
      })
      .finally(() => {
        if (abortRef.current === controller) abortRef.current = null;
        setStreaming(false);
      });
  }

  function stop() {
    abortRef.current?.abort();
  }

  /// Clear the current conversation thread (stops any stream and resets to a blank chat).
  function clearThread() {
    stop();
    setMessages([]);
    setUsage(null);
    setOpenedId(null);
    setSaveStatus(null);
    setError(null);
  }

  function onPickFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = ""; // allow re-picking the same file
    if (!file) return;
    setError(null);
    setUploading(true);
    api
      .uploadChatAttachment(file)
      .then((r) => setAttachment({ name: r.name, text: r.text, chars: r.chars }))
      .catch((err: unknown) => setError(apiErrorMessage(err, "Could not read the file.")))
      .finally(() => setUploading(false));
  }

  async function refreshSavedList() {
    try {
      setSavedList(await api.listChatConversations());
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function toggleSavedList() {
    const next = !listOpen;
    setListOpen(next);
    if (next) await refreshSavedList();
  }

  async function openConversation(id: string) {
    setListOpen(false);
    try {
      const c = await api.getChatConversation(id);
      setMessages(c.messages);
      // Restore the conversation's transcripts as the shared selection and switch to that mode.
      const ids = c.context.recordingIds ?? [];
      if (ids.length > 0) {
        selection.set(ids);
        setContextMode("selected");
      } else {
        setContextMode("none");
      }
      setAttachment(
        c.context.attachmentName && c.context.attachmentText
          ? { name: c.context.attachmentName, text: c.context.attachmentText, chars: c.context.attachmentText.length }
          : null,
      );
      setOpenedId(id);
      setUsage(null);
      setSaveStatus(null);
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function saveConversation() {
    if (!started || streaming || saving) return;
    setSaving(true);
    setError(null);
    const body = {
      messages,
      context: {
        recordingIds,
        attachmentName: attachment?.name ?? null,
        attachmentText: attachment?.text ?? null,
      },
    };
    try {
      const res = openedId
        ? await api.updateChatConversation(openedId, body)
        : await api.createChatConversation(body);
      setOpenedId(res.id);
      setSaveStatus("Saved");
      // Keep the saved-conversations dropdown current (it may be open, and isn't otherwise refreshed).
      await refreshSavedList();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setSaving(false);
    }
  }

  async function deleteConversation() {
    if (!openedId) return;
    try {
      await api.deleteChatConversation(openedId);
      clearThread();
      setSavedList((prev) => prev.filter((c) => c.id !== openedId));
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  const contextLabel =
    contextMode === "current"
      ? "Current transcript"
      : contextMode === "selected"
        ? `Selected (${selection.selectedIds.length})`
        : "None";

  return (
    <div className="flex h-full flex-col">
      {/* Toolbar */}
      <div className="flex h-9 shrink-0 items-center gap-1 border-b px-2 dark:border-gray-700">
        <div ref={savedRef} className="relative flex items-center">
          <IconButton label="Saved conversations" onClick={toggleSavedList} aria-expanded={listOpen}>
            <BookmarkIcon />
          </IconButton>
          {listOpen && (
            <div
              role="menu"
              className="absolute left-0 top-full z-30 mt-1 max-h-64 w-56 overflow-y-auto rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
            >
              {savedList.length === 0 ? (
                <div className="px-3 py-2 text-xs text-gray-400">No saved conversations</div>
              ) : (
                savedList.map((c) => (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => openConversation(c.id)}
                    className="block w-full truncate px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:text-gray-200 dark:hover:bg-gray-700"
                    title={new Date(c.updatedAt).toLocaleString()}
                  >
                    {c.title}
                  </button>
                ))
              )}
            </div>
          )}
        </div>
        <IconButton label="Clear conversation" onClick={clearThread} disabled={!started}>
          <EraserIcon />
        </IconButton>
        <IconButton label="Save conversation" onClick={saveConversation} disabled={!started || streaming || saving}>
          <SaveIcon />
        </IconButton>
        <IconButton label="Delete conversation" onClick={deleteConversation} disabled={!openedId}>
          <TrashIcon />
        </IconButton>
        {saveStatus && <span className="ml-1 text-xs text-green-600 dark:text-green-400">{saveStatus}</span>}
        {/* flex so the inline-flex dial is a flex item (centred) rather than baseline-aligned in a line box. */}
        <div className="ml-auto flex items-center">
          {dialTotal > 0 && <ContextDial model={dialModel} used={dialUsed} total={dialTotal} />}
        </div>
      </div>

      {/* Thread */}
      <div ref={threadRef} className="min-h-0 flex-1 space-y-3 overflow-y-auto p-3">
        {messages.length === 0 ? (
          <p className="px-1 text-xs text-gray-400 dark:text-gray-500">
            Ask a question about your recordings. Pick which transcripts the assistant can see below.
          </p>
        ) : (
          messages.map((m, i) => {
            const thinking = m.role === "assistant" && m.content === "" && streaming && i === messages.length - 1;
            return (
              <div key={i} className={m.role === "user" ? "flex justify-end" : "flex justify-start"}>
                <div
                  className={
                    m.role === "user"
                      ? "max-w-[85%] rounded-lg bg-blue-600 px-3 py-2 text-sm text-white"
                      : "max-w-[90%] rounded-lg border bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                  }
                >
                  {m.role === "assistant" ? (
                    thinking ? (
                      <span className="text-gray-400">Thinking…</span>
                    ) : (
                      // Assistant markdown is sanitized in renderMarkdown (DOMPurify) before injection.
                      <div
                        className="chat-md break-words [&_a]:underline [&_code]:rounded [&_code]:bg-black/10 [&_code]:px-1 [&_pre]:overflow-x-auto"
                        dangerouslySetInnerHTML={{ __html: renderMarkdown(m.content) }}
                      />
                    )
                  ) : (
                    <span className="whitespace-pre-wrap break-words">{m.content}</span>
                  )}
                </div>
              </div>
            );
          })
        )}
      </div>

      {error && <p className="px-3 pb-1 text-xs text-red-600 dark:text-red-400">{error}</p>}

      {/* Context + attachment */}
      <div className="shrink-0 border-t px-2 py-1.5 dark:border-gray-700">
        <div className="flex flex-wrap items-center gap-2">
          <div ref={pickerRef} className="relative flex items-center">
            <button
              type="button"
              onClick={() => setPickerOpen((v) => !v)}
              aria-expanded={pickerOpen}
              className="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-300 dark:hover:bg-gray-800"
            >
              <span className="h-1.5 w-1.5 rounded-full bg-blue-500" />
              Context: {contextLabel}
              <span className="text-gray-400">{pickerOpen ? "▲" : "▼"}</span>
            </button>
            {pickerOpen && (
              <div
                role="menu"
                className="absolute bottom-full left-0 z-30 mb-1 w-60 overflow-hidden rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
              >
                {(
                  [
                    { mode: "current", label: "Current transcript", hint: "The recording open in the middle panel" },
                    { mode: "selected", label: `Selected transcript${selection.selectedIds.length === 1 ? "" : "s"}`, hint: `Ticked via the list's Select button (${selection.selectedIds.length})` },
                    { mode: "none", label: "None", hint: "No transcript context" },
                  ] as { mode: ContextMode; label: string; hint: string }[]
                ).map((o) => (
                  <button
                    key={o.mode}
                    type="button"
                    role="menuitemradio"
                    aria-checked={contextMode === o.mode}
                    onClick={() => {
                      setContextMode(o.mode);
                      setPickerOpen(false);
                    }}
                    className={`block w-full px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-700 ${
                      contextMode === o.mode ? "font-medium text-blue-700 dark:text-blue-300" : "dark:text-gray-200"
                    }`}
                  >
                    <span className="flex items-center gap-1.5">
                      {contextMode === o.mode ? "●" : "○"} {o.label}
                    </span>
                    <span className="ml-4 block text-[11px] text-gray-400">{o.hint}</span>
                  </button>
                ))}
              </div>
            )}
          </div>

          <input
            ref={fileRef}
            type="file"
            accept=".pdf,.txt,.text,.md,.markdown,.csv,.tsv,.log,.json,.yaml,.yml,.rst,application/pdf,text/*"
            className="hidden"
            onChange={onPickFile}
          />
          {attachment ? (
            <span className="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs dark:border-gray-700 dark:text-gray-300">
              📎 <span className="max-w-[120px] truncate">{attachment.name}</span>
              <button type="button" aria-label="Remove attachment" onClick={() => setAttachment(null)} className="text-gray-400 hover:text-red-500">
                ✕
              </button>
            </span>
          ) : (
            <button
              type="button"
              onClick={() => fileRef.current?.click()}
              disabled={uploading}
              className="inline-flex items-center rounded-full border px-2 py-0.5 text-xs text-gray-600 hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-300 dark:hover:bg-gray-800"
            >
              {uploading ? "Reading…" : "+ Attach file"}
            </button>
          )}
        </div>

        {/* Input */}
        <div className="mt-2 flex items-end gap-2">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                send();
              }
            }}
            rows={2}
            placeholder="Ask about your transcripts…"
            aria-label="Chat message"
            className="min-h-0 flex-1 resize-none rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          {streaming ? (
            <button
              type="button"
              onClick={stop}
              className="rounded bg-gray-200 px-3 py-1.5 text-sm dark:bg-gray-700 dark:text-gray-100"
            >
              Stop
            </button>
          ) : (
            <button
              type="button"
              onClick={send}
              disabled={!input.trim()}
              className="rounded bg-blue-600 px-3 py-1.5 text-sm text-white disabled:opacity-50"
            >
              Send
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function IconButton({
  label,
  children,
  ...rest
}: { label: string; children: React.ReactNode } & React.ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      // inline-flex centres the SVG (avoids the inline baseline gap) so the icons line up vertically
      // with the context dial, which is itself an inline-flex.
      className="inline-flex items-center justify-center rounded p-1 text-gray-500 hover:bg-gray-100 disabled:opacity-40 dark:text-gray-400 dark:hover:bg-gray-700"
      {...rest}
    >
      {children}
    </button>
  );
}

const iconProps = {
  width: 16,
  height: 16,
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 2,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
};

const BookmarkIcon = () => (
  <svg {...iconProps}>
    <path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z" />
  </svg>
);
const EraserIcon = () => (
  <svg {...iconProps}>
    <path d="M7 21h10M4 13l6 6 9-9a2 2 0 0 0 0-3l-3-3a2 2 0 0 0-3 0L4 13z" />
  </svg>
);
const SaveIcon = () => (
  <svg {...iconProps}>
    <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z" />
    <path d="M17 21v-8H7v8M7 3v5h8" />
  </svg>
);
const TrashIcon = () => (
  <svg {...iconProps}>
    <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
  </svg>
);
