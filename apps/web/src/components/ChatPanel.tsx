import { useEffect, useRef, useState } from "react";
import { useNavigate, useMatch } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { parseRecordingLink, recordingLinkPath } from "../lib/transcriptNav";
import { linkifyRecordings } from "../lib/linkify";
import { renderMarkdown } from "../lib/markdown";
import { useActiveRecordingId } from "../lib/useActiveRecordingId";
import { useSelection } from "../lib/selection";
import {
  inferCurrentContext, currentContextLabelKey, currentContextRequest, type CurrentContext,
} from "../lib/chatContext";
import {
  emptyToolCallLine,
  toolStarted,
  toolEnded,
  toolCallLineText,
  type ToolCallLineState,
} from "../lib/toolCallLine";
import {
  parseChatCommand,
  buildToolsOutput,
  buildHelpOutput,
  bulletList,
  matchCommands,
  type ChatCommand,
  type CommandInfo,
} from "../lib/chatCommands";
import type { AttachmentDraft, ChatConversationSummary, ChatTurn, ChatUsage } from "../lib/types";
import ContextDial from "./ContextDial";
import PickRecordingModal from "./PickRecordingModal";

// The explicit "selected" mode is gone - the "current" context is inferred (folder / single / multiple) and
// its label switches accordingly.
type ContextMode = "current" | "all" | "none";

/// Right-panel chat: ask questions over one or more transcripts, attach a PDF/text file as extra
/// context, watch the reply stream in, and save / reload / delete conversations.
export default function ChatPanel() {
  const { t } = useTranslation("chat");
  const navigate = useNavigate();
  const activeId = useActiveRecordingId();
  const activeSectionId = useMatch("/sections/:id")?.params.id ?? null;
  const selection = useSelection();
  const qc = useQueryClient();
  // The chat "add as attachment" tool: the model queues a note during the turn; we act once the reply lands —
  // one transcript in context → add it; several → let the user pick one.
  const pendingDraftRef = useRef<AttachmentDraft | null>(null);
  const [pickerDraft, setPickerDraft] = useState<AttachmentDraft | null>(null);
  const [attachNotice, setAttachNotice] = useState<string | null>(null);
  // Output of a client-side slash command (/tools, /help) — shown in the thread, never sent to the model.
  const [commandOutput, setCommandOutput] = useState<string | null>(null);

  /// Intercept clicks on the assistant's transcript deep-links so they open in the middle panel (and seek
  /// to the moment) via the SPA router, instead of triggering a full page reload.
  function onThreadClick(e: React.MouseEvent<HTMLDivElement>) {
    const anchor = (e.target as HTMLElement).closest("a");
    const href = anchor?.getAttribute("href");
    if (!href) return;
    const link = parseRecordingLink(href);
    if (!link) return; // external / non-transcript link — leave default behaviour
    e.preventDefault();
    // Gather every moment this answer cited for the same recording, so the transcript can offer prev/next.
    const container = anchor!.closest(".chat-md") ?? threadRef.current;
    const times = new Set<number>();
    container?.querySelectorAll("a").forEach((a) => {
      const l = parseRecordingLink(a.getAttribute("href") ?? "");
      if (l && l.id === link.id && l.t != null) times.add(l.t);
    });
    navigate(recordingLinkPath(link, [...times].sort((a, b) => a - b)));
  }
  // The model's context-window size for the dial (per-user override, else server default).
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  const [messages, setMessages] = useState<ChatTurn[]>([]);
  const [input, setInput] = useState("");
  const [streaming, setStreaming] = useState(false);
  const [usage, setUsage] = useState<ChatUsage | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Ephemeral "Tool call: …" indicator while built-in tools run (cleared when the assistant resumes text).
  const [toolLine, setToolLine] = useState<ToolCallLineState>(emptyToolCallLine);

  const [contextMode, setContextMode] = useState<ContextMode>("current");
  const [includeAttachments, setIncludeAttachments] = useState(false);
  const [pickerOpen, setPickerOpen] = useState(false);
  // The inferred "current" context, snapshotted when the input is focused - so the context-selector wording
  // only changes when the user's cursor enters the chat box (not live as they navigate/select). Seeded from
  // the mount-time inference so the initial pill/context is already correct.
  const [frozenCurrent, setFrozenCurrent] = useState<CurrentContext>(() =>
    inferCurrentContext({ sectionId: activeSectionId, recordingId: activeId, selectedIds: selection.selectedIds }));

  const [attachment, setAttachment] = useState<{ name: string; text: string; chars: number } | null>(null);
  const [uploading, setUploading] = useState(false);

  const [savedList, setSavedList] = useState<ChatConversationSummary[]>([]);
  const [listOpen, setListOpen] = useState(false);
  const [openedId, setOpenedId] = useState<string | null>(null);
  const [saveStatus, setSaveStatus] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const abortRef = useRef<AbortController | null>(null);
  // Recordings the tools referenced this turn (name → href), used to linkify plain mentions on completion.
  const refsRef = useRef<Map<string, string>>(new Map());
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

  // What "current" resolves to right now (open folder / open recording / 2+ ticked). Live; the displayed
  // wording uses `frozenCurrent`, which only updates on input focus.
  const inferredCurrent = inferCurrentContext({
    sectionId: activeSectionId,
    recordingId: activeId,
    selectedIds: selection.selectedIds,
  });

  // The context actually sent: a folder (section id) or a set of recording ids, from the chosen mode.
  const { recordingIds, sectionId } =
    contextMode === "current"
      ? currentContextRequest(frozenCurrent)
      : { recordingIds: [] as string[], sectionId: null };
  const hasContext = recordingIds.length > 0 || sectionId != null;

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

  /// After the turn completes, link any plain mention of a tool-referenced recording that the model didn't
  /// link itself, so the answer always has clickable transcript references.
  function linkifyLastAssistant() {
    const refs = Array.from(refsRef.current, ([name, href]) => ({ name, href }));
    if (refs.length === 0) return;
    setMessages((prev) => {
      const next = prev.slice();
      const last = next[next.length - 1];
      if (last?.role === "assistant" && last.content)
        next[next.length - 1] = { ...last, content: linkifyRecordings(last.content, refs) };
      return next;
    });
  }

  /// Save a chat-prepared note to a transcript as a Markdown attachment, refresh that recording's attachments,
  /// and show a short confirmation.
  async function createMarkdownAttachment(draft: AttachmentDraft, rec: { id: string; title: string }) {
    try {
      await api.addMarkdownAttachment(rec.id, draft.name, draft.content);
      qc.invalidateQueries({ queryKey: ["attachments", rec.id] });
      setAttachNotice(t("attachAdded", { name: draft.name, title: rec.title }));
    } catch {
      setAttachNotice(t("attachFailed"));
    }
  }

  /// Once the reply completes, act on any attachment the tool queued: one candidate → add it; several → ask.
  function resolvePendingDraft() {
    const draft = pendingDraftRef.current;
    pendingDraftRef.current = null;
    if (!draft || draft.recordings.length === 0) return;
    if (draft.recordings.length === 1) void createMarkdownAttachment(draft, draft.recordings[0]);
    else setPickerDraft(draft);
  }

  /// The slash commands, in the order shown by the autocomplete popup and /help (client-side only).
  const commandInfos: CommandInfo[] = [
    { cmd: "clear", command: "/clear", description: t("cmdHelpClear") },
    { cmd: "context", command: "/context", description: t("cmdHelpContext") },
    { cmd: "copy", command: "/copy", description: t("cmdHelpCopy") },
    { cmd: "help", command: "/help", description: t("cmdHelpHelp") },
    { cmd: "load", command: "/load", description: t("cmdHelpLoad") },
    { cmd: "retry", command: "/retry", description: t("cmdHelpRetry") },
    { cmd: "save", command: "/save", description: t("cmdHelpSave") },
    { cmd: "tools", command: "/tools", description: t("cmdHelpTools") },
  ];

  // Autocomplete: commands whose name starts with what's typed (only while the input is a "/…" and not busy).
  const commandMatches = streaming ? [] : matchCommands(input, commandInfos);

  /// Markdown for the output-only commands (/tools, /help, /context). Action commands are handled in runSlash.
  function commandText(cmd: ChatCommand): string {
    if (cmd === "tools")
      return buildToolsOutput(settings?.tools ?? [], settings?.toolsEnabled ?? false, {
        heading: t("cmdToolsHeading"),
        disabled: t("cmdToolsDisabled"),
        none: t("cmdToolsNone"),
        colName: t("cmdToolsColName"),
        colDescription: t("cmdToolsColDesc"),
      });
    if (cmd === "context")
      return bulletList(t("cmdContextHeading"), [
        t("cmdContextScope", { scope: contextLabel }),
        t("cmdContextCount", { count: recordingIds.length }),
        t("cmdContextModel", { model: dialModel || "—" }),
        t("cmdContextUsage", { used: dialUsed.toLocaleString(), total: dialTotal.toLocaleString() }),
      ]);
    return buildHelpOutput(commandInfos, t("cmdHelpHeading"));
  }

  /// Copy the assistant's most recent reply to the clipboard.
  function copyLastReply() {
    const last = [...messages].reverse().find((m) => m.role === "assistant" && m.content.trim());
    if (!last) {
      setCommandOutput(t("cmdCopyNone"));
      return;
    }
    navigator.clipboard?.writeText(last.content).then(
      () => setCommandOutput(t("cmdCopied")),
      () => setCommandOutput(t("cmdCopyFailed")),
    );
  }

  /// Re-ask the last question (drops the trailing assistant reply and streams a fresh one).
  function retry() {
    if (streaming) return;
    let history = messages.slice();
    if (history.length > 0 && history[history.length - 1].role === "assistant") history = history.slice(0, -1);
    if (history.length === 0 || history[history.length - 1].role !== "user") {
      setCommandOutput(t("cmdRetryNone"));
      return;
    }
    runTurn(history);
  }

  /// Run a client-side slash command. Never sent to the model.
  function runSlash(cmd: ChatCommand) {
    setInput("");
    switch (cmd) {
      case "tools":
      case "help":
      case "context":
        setCommandOutput(commandText(cmd));
        break;
      case "clear":
        clearThread();
        setCommandOutput(null);
        break;
      case "save":
        setCommandOutput(null);
        void saveConversation();
        break;
      case "load":
        setCommandOutput(null);
        void toggleSavedList();
        break;
      case "copy":
        copyLastReply();
        break;
      case "retry":
        setCommandOutput(null);
        retry();
        break;
    }
  }

  /// Stream one turn for the given history (which must end in a user turn). Shared by send() and retry().
  function runTurn(history: ChatTurn[]) {
    setError(null);
    setSaveStatus(null);
    setCommandOutput(null);
    setMessages([...history, { role: "assistant", content: "" }]);
    setStreaming(true);
    setPickerOpen(false);
    setAttachNotice(null);
    pendingDraftRef.current = null;

    const controller = new AbortController();
    abortRef.current = controller;
    refsRef.current = new Map();
    api
      .chatStream(
        {
          recordingIds,
          sectionId,
          attachmentName: attachment?.name ?? null,
          attachmentText: attachment?.text ?? null,
          messages: history,
          includeAttachments: includeAttachments && hasContext,
          searchAllMeetings: contextMode === "all",
        },
        {
          onToken: (tok) => {
            setToolLine(emptyToolCallLine); // assistant produced text → drop the tool-call line
            appendToken(tok);
          },
          onMeta: setUsage,
          onToolStart: (name) => setToolLine((s) => toolStarted(s, name)),
          onToolEnd: (name) => setToolLine((s) => toolEnded(s, name)),
          onRef: (name, href) => refsRef.current.set(name, href),
          onAttachmentDraft: (d) => (pendingDraftRef.current = d),
          signal: controller.signal,
        },
      )
      .then((u) => {
        if (controller.signal.aborted) return;
        setUsage(u);
        linkifyLastAssistant();
        resolvePendingDraft();
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setLastAssistant(`[${apiErrorMessage(e, t("chatFailed"))}]`);
      })
      .finally(() => {
        if (abortRef.current === controller) abortRef.current = null;
        setToolLine(emptyToolCallLine);
        setStreaming(false);
      });
  }

  function send() {
    const prompt = input.trim();
    if (!prompt || streaming) return;

    // Slash commands are handled entirely client-side — they never reach the model (so "/tools" always
    // just lists the tools and can't trigger a spurious tool call).
    const command = parseChatCommand(prompt);
    if (command) {
      runSlash(command);
      return;
    }

    setInput("");
    runTurn([...messages, { role: "user", content: prompt }]);
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
      .catch((err: unknown) => setError(apiErrorMessage(err, t("couldNotReadFile"))))
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
      // Restore the conversation's transcripts as the shared selection; "current" then infers single/multiple.
      const ids = c.context.recordingIds ?? [];
      if (c.context.searchAllMeetings) {
        setContextMode("all");
      } else if (ids.length > 0) {
        selection.set(ids);
        setContextMode("current");
        setFrozenCurrent(ids.length >= 2 ? { kind: "selected", recordingIds: ids } : { kind: "single", recordingId: ids[0] });
      } else {
        setContextMode("none");
      }
      setAttachment(
        c.context.attachmentName && c.context.attachmentText
          ? { name: c.context.attachmentName, text: c.context.attachmentText, chars: c.context.attachmentText.length }
          : null,
      );
      setIncludeAttachments(c.context.includeAttachments ?? false);
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
        includeAttachments,
        searchAllMeetings: contextMode === "all",
      },
    };
    try {
      const res = openedId
        ? await api.updateChatConversation(openedId, body)
        : await api.createChatConversation(body);
      setOpenedId(res.id);
      setSaveStatus(t("saved"));
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

  // The "current" wording (Current Transcript / Selected Transcripts / Current Folder) comes from the frozen
  // snapshot, so it only changes when the user focuses the input.
  const currentLabel = t(currentContextLabelKey(frozenCurrent));
  const currentHint =
    frozenCurrent.kind === "folder"
      ? t("pickFolderHint")
      : frozenCurrent.kind === "selected"
        ? t("pickSelectedHint", { n: frozenCurrent.recordingIds.length })
        : t("pickCurrentHint");
  const contextLabel =
    contextMode === "current" ? currentLabel : contextMode === "all" ? t("ctxAll") : t("ctxNone");

  // Localized "Tool call: …" indicator: translate the prefix and each tool's friendly name (falling back to
  // a humanized form of the snake_case id when a label isn't translated).
  const toolLineText = toolCallLineText(toolLine, {
    prefix: t("toolCallPrefix"),
    label: (n) => t(`toolLabels.${n}`, { defaultValue: n.replace(/_/g, " ") }),
  });

  return (
    <div className="flex h-full flex-col">
      {/* Toolbar */}
      <div className="flex h-9 shrink-0 items-center gap-1 border-b px-2 dark:border-gray-700">
        <div ref={savedRef} className="relative flex items-center">
          <IconButton label={t("savedConversations")} onClick={toggleSavedList} aria-expanded={listOpen}>
            <BookmarkIcon />
          </IconButton>
          {listOpen && (
            <div
              role="menu"
              className="absolute left-0 top-full z-30 mt-1 max-h-64 w-56 overflow-y-auto rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
            >
              {savedList.length === 0 ? (
                <div className="px-3 py-2 text-xs text-gray-400">{t("noSavedConversations")}</div>
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
        <IconButton label={t("clearConversation")} onClick={clearThread} disabled={!started}>
          <EraserIcon />
        </IconButton>
        <IconButton label={t("saveConversation")} onClick={saveConversation} disabled={!started || streaming || saving}>
          <SaveIcon />
        </IconButton>
        <IconButton label={t("deleteConversation")} onClick={deleteConversation} disabled={!openedId}>
          <TrashIcon />
        </IconButton>
        {saveStatus && <span className="ml-1 text-xs text-green-600 dark:text-green-400">{saveStatus}</span>}
        {/* flex so the inline-flex dial is a flex item (centred) rather than baseline-aligned in a line box. */}
        <div className="ml-auto flex items-center">
          {dialTotal > 0 && <ContextDial model={dialModel} used={dialUsed} total={dialTotal} />}
        </div>
      </div>

      {/* Thread */}
      <div ref={threadRef} onClick={onThreadClick} className="min-h-0 flex-1 space-y-3 overflow-y-auto p-3">
        {messages.length === 0 && !commandOutput ? (
          <p className="px-1 text-xs text-gray-400 dark:text-gray-500">{t("intro")}</p>
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
                      <span className="text-gray-400">{t("thinking")}</span>
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
        {commandOutput && (
          <div className="flex justify-start">
            <div className="max-w-[90%] rounded-lg border border-dashed bg-gray-50 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800/60 dark:text-gray-100">
              <div className="mb-1 flex items-center justify-between gap-2">
                <span className="text-[11px] uppercase tracking-wide text-gray-400">{t("cmdLabel")}</span>
                <button
                  type="button"
                  aria-label={t("cmdDismiss")}
                  onClick={() => setCommandOutput(null)}
                  className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
                >
                  ✕
                </button>
              </div>
              <div
                className="chat-md break-words
                  [&_table]:my-1 [&_table]:w-full [&_table]:border-collapse
                  [&_th]:border [&_th]:px-2 [&_th]:py-1 [&_th]:text-left [&_th]:font-semibold
                  [&_td]:border [&_td]:px-2 [&_td]:py-1 [&_td]:align-top
                  dark:[&_th]:border-gray-700 dark:[&_td]:border-gray-700
                  [&_ul]:list-disc [&_ul]:pl-5 [&_li]:my-0.5"
                dangerouslySetInnerHTML={{ __html: renderMarkdown(commandOutput) }}
              />
            </div>
          </div>
        )}
        {toolLineText && (
          <div className="flex justify-start">
            <span className="px-1 text-xs italic text-gray-400">{toolLineText}</span>
          </div>
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
              {t("contextPrefix")} {contextLabel}
              <span className="text-gray-400">{pickerOpen ? "▲" : "▼"}</span>
            </button>
            {pickerOpen && (
              <div
                role="menu"
                className="absolute bottom-full left-0 z-30 mb-1 w-60 overflow-hidden rounded-md border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-800"
              >
                {(
                  [
                    { mode: "current", label: currentLabel, hint: currentHint },
                    { mode: "all", label: t("ctxAll"), hint: t("pickAllHint") },
                    { mode: "none", label: t("ctxNone"), hint: t("pickNoneHint") },
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
                {/* Pull the selected transcripts' attachments (files + URLs) into the context too. */}
                <label className="mt-1 flex cursor-pointer items-start gap-1.5 border-t px-3 py-1.5 text-sm dark:border-gray-700 dark:text-gray-200">
                  <input
                    type="checkbox"
                    checked={includeAttachments}
                    disabled={contextMode === "none" || contextMode === "all"}
                    onChange={(e) => setIncludeAttachments(e.target.checked)}
                    className="mt-0.5"
                  />
                  <span>
                    {t("includeAttachments")}
                    <span className="block text-[11px] text-gray-400">{t("includeAttachmentsHint")}</span>
                  </span>
                </label>
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
              <button type="button" aria-label={t("removeAttachment")} onClick={() => setAttachment(null)} className="text-gray-400 hover:text-red-500">
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
              {uploading ? t("reading") : t("attachFile")}
            </button>
          )}
        </div>

        {attachNotice && (
          <div className="mt-2 flex items-center justify-between gap-2 rounded bg-green-50 px-2 py-1 text-xs text-green-700 dark:bg-green-900/30 dark:text-green-300">
            <span className="truncate">{attachNotice}</span>
            <button type="button" aria-label={t("common:dismiss", { defaultValue: "Dismiss" })} onClick={() => setAttachNotice(null)} className="shrink-0 text-green-600 hover:text-green-800 dark:text-green-400">
              ✕
            </button>
          </div>
        )}

        {/* Slash-command autocomplete (opens upward, above the input). */}
        {commandMatches.length > 0 && (
          <div className="mt-2 overflow-hidden rounded-lg border bg-white shadow dark:border-gray-700 dark:bg-gray-800">
            {commandMatches.map((c) => (
              <button
                key={c.command}
                type="button"
                // Keep focus in the textarea; mousedown fires before blur so the popup doesn't flicker.
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => runSlash(c.cmd)}
                className="flex w-full items-baseline gap-2 px-3 py-1.5 text-left hover:bg-gray-100 dark:hover:bg-gray-700"
              >
                <span className="font-mono text-sm text-blue-600 dark:text-blue-400">{c.command}</span>
                <span className="truncate text-xs text-gray-500 dark:text-gray-400">{c.description}</span>
              </button>
            ))}
          </div>
        )}

        {/* Input */}
        <div className="mt-2 flex items-end gap-2">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onFocus={() => setFrozenCurrent(inferredCurrent)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                // If a partial "/cmd" is typed, Enter runs the top match; a complete command falls through
                // to send() (which parses + runs it), and normal text is sent to the model.
                if (commandMatches.length > 0 && !parseChatCommand(input)) runSlash(commandMatches[0].cmd);
                else send();
              }
            }}
            rows={2}
            placeholder={t("askPlaceholder")}
            aria-label={t("messageAria")}
            className="min-h-0 flex-1 resize-none rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          {streaming ? (
            <button
              type="button"
              onClick={stop}
              className="rounded bg-gray-200 px-3 py-1.5 text-sm dark:bg-gray-700 dark:text-gray-100"
            >
              {t("stop")}
            </button>
          ) : (
            <button
              type="button"
              onClick={send}
              disabled={!input.trim()}
              className="rounded bg-blue-600 px-3 py-1.5 text-sm text-white disabled:opacity-50"
            >
              {t("send")}
            </button>
          )}
        </div>
      </div>

      {pickerDraft && (
        <PickRecordingModal
          draft={pickerDraft}
          onCancel={() => setPickerDraft(null)}
          onPick={async (recId) => {
            const rec = pickerDraft.recordings.find((r) => r.id === recId);
            setPickerDraft(null);
            if (rec) await createMarkdownAttachment(pickerDraft, rec);
          }}
        />
      )}
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
