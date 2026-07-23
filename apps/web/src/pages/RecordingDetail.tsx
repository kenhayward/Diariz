import { useEffect, useMemo, useRef, useState } from "react";
import { useParams, useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import { isProcessing } from "../lib/recordingStatus";
import { useStatus } from "../lib/status";
import ActionsTable from "../components/ActionsTable";
import DetailSections, { type DetailSection } from "../components/detail/DetailSections";
import DetailHeader from "../components/detail/DetailHeader";
import RecordingHub from "../components/detail/RecordingHub";
import MeetingCard from "../components/detail/MeetingCard";
import ConversationFlowPlayer from "../components/detail/ConversationFlowPlayer";
import {
  ActionsGlyph,
  FilesGlyph,
  FormulasGlyph,
  MinutesGlyph,
  NotesGlyph,
  SpeakersGlyph,
  TranscriptGlyph,
} from "../components/detail/SectionIcons";
import { DETAIL_SECTION_KEY, initialSection, type SectionKey } from "../lib/detailSection";
import MoveToSectionModal from "../components/MoveToSectionModal";
import ShareToRoomModal from "../components/ShareToRoomModal";
import { useRoom } from "../lib/rooms";
import DownloadTranscriptModal from "../components/DownloadTranscriptModal";
import SummaryEditModal from "../components/SummaryEditModal";
import MeetingMinutesEditModal from "../components/MeetingMinutesEditModal";
import EmailMinutesModal from "../components/EmailMinutesModal";
import MeetingTypeMenu from "../components/MeetingTypeMenu";
import NotesSection from "../components/NotesSection";
import ScreenshotModal from "../components/ScreenshotModal";
import ScreenshotsSection from "../components/ScreenshotsSection";
import ManageMeetingTypesModal from "../components/ManageMeetingTypesModal";
import { renderMarkdown } from "../lib/markdown";
import { weaveTranscript } from "../lib/transcriptNotes";
import { useAuth } from "../auth";
import AttachmentsManager from "../components/AttachmentsManager";
import CalendarLinkModal from "../components/CalendarLinkModal";
import PreferencesModal from "../components/PreferencesModal";
import SpeakerAssign from "../components/SpeakerAssign";
import ToolbarButton, { iconProps } from "../components/ToolbarButton";
import FormulasToolbar from "../components/FormulasToolbar";
import FormulasPanel from "../components/FormulasPanel";
import FormulaRunModal from "../components/FormulaRunModal";
import SharedFormulasBrowser from "../components/SharedFormulasBrowser";
import FormulaResultEditModal from "../components/FormulaResultEditModal";
import { recordingMenu } from "../components/recordingMenu";
import { copyRichLink, transcriptUrl } from "../lib/clipboard";
import { segmentIndexAtMs, parseMatchTimes } from "../lib/transcriptNav";
import { speakerRanges, selectedRanges, rangeAt, nextRangeStart, type PlayRange } from "../lib/segmentPlayback";
import { formatBytes, formatDate, formatDuration, formatDurationApprox } from "../lib/format";
import { hasRevisions, segmentText } from "../lib/transcriptView";
import { fetchLanguages } from "../lib/languages";
import { selectedMeetingType } from "../lib/meetingTypes";
import type { MeetingNote, SegmentDto, SpeakerInfo, SpeakerProfile, FormulaResult } from "../lib/types";

// Feather-style icons for the panel toolbars and the per-speaker play control.
const RefreshIcon = (
  <svg {...iconProps}><polyline points="23 4 23 10 17 10" /><polyline points="1 20 1 14 7 14" /><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" /></svg>
);
const PencilIcon = (
  <svg {...iconProps}><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></svg>
);
const MailIcon = (
  <svg {...iconProps}><path d="M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z" /><polyline points="22,6 12,13 2,6" /></svg>
);
const UsersIcon = (
  <svg {...iconProps}><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M23 21v-2a4 4 0 0 0-3-3.87" /><path d="M16 3.13a4 4 0 0 1 0 7.75" /></svg>
);
const SlidersIcon = <svg {...iconProps}><line x1="4" y1="21" x2="4" y2="14" /><line x1="4" y1="10" x2="4" y2="3" /><line x1="12" y1="21" x2="12" y2="12" /><line x1="12" y1="8" x2="12" y2="3" /><line x1="20" y1="21" x2="20" y2="16" /><line x1="20" y1="12" x2="20" y2="3" /><line x1="1" y1="14" x2="7" y2="14" /><line x1="9" y1="8" x2="15" y2="8" /><line x1="17" y1="16" x2="23" y2="16" /></svg>;
const PlayIcon = <svg {...iconProps}><polygon points="5 3 19 12 5 21 5 3" /></svg>;
const PauseIcon = <svg {...iconProps}><rect x="6" y="4" width="4" height="16" /><rect x="14" y="4" width="4" height="16" /></svg>;
// Play-all = a "from the start" glyph (skip-to-start bar + triangle).
const SelectIcon = <svg {...iconProps}><path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" /></svg>;
const MergeIcon = <svg {...iconProps}><path d="M6 3v6a6 6 0 0 0 6 6 6 6 0 0 0 6-6V3" /><line x1="12" y1="15" x2="12" y2="21" /></svg>;
const TrashIcon = <svg {...iconProps}><polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></svg>;
const GlobeIcon = <svg {...iconProps}><circle cx="12" cy="12" r="10" /><line x1="2" y1="12" x2="22" y2="12" /><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" /></svg>;
const EyeIcon = <svg {...iconProps}><path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z" /><circle cx="12" cy="12" r="3" /></svg>;

function fmt(ms: number): string {
  const s = Math.floor(ms / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
}

export default function RecordingDetail() {
  const { t, i18n } = useTranslation();
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  // While a minutes run (recreate or a template apply) is in flight the picker is disabled and the status bar
  // shows progress; both clear when fresh minutes arrive. Declared before the query so the query can poll while
  // a run is in flight (below).
  const [minutesRunning, setMinutesRunning] = useState(false);
  const minutesBaselineRef = useRef<string | null>(null);
  const { data: rec, error: recError } = useQuery({
    queryKey: ["recording", id],
    queryFn: () => api.getRecording(id),
    enabled: Boolean(id),
    // A missing recording won't come back by retrying, and we want the redirect (below) to fire promptly.
    retry: (_count, e) => (e as { response?: { status?: number } })?.response?.status !== 404,
    // Minutes generation is async with no status change, and its only "done" signal is a single SignalR push.
    // If that event is missed (slow LLM + a proxy that idle-drops the socket), the picker would spin forever.
    // Poll while a run is in flight so the fresh minutes are picked up regardless; the effect below stops it.
    refetchInterval: minutesRunning ? 2500 : false,
  });

  // If the recording no longer exists - deleted from here, from the list, on another device, or reached via
  // a stale link - send the user home instead of leaving them on a "Loading..." / stale transcript page.
  useEffect(() => {
    if ((recError as { response?: { status?: number } })?.response?.status === 404) {
      navigate("/", { replace: true });
    }
  }, [recError, navigate]);
  const { data: profiles = [] } = useQuery({
    queryKey: ["speaker-profiles"],
    queryFn: api.listSpeakerProfiles,
  });
  const { data: attachments = [] } = useQuery({
    queryKey: ["attachments", id],
    queryFn: () => api.listAttachments(id),
    enabled: Boolean(id),
  });
  // Generated formula results (the Formulas tab). Formula runs are async, so poll while any result is still
  // generating (the run adds a Generating row immediately; the poll fills in the Ready/Failed outcome).
  const { data: formulaResults = [] } = useQuery({
    queryKey: ["formula-results", id],
    queryFn: () => api.listFormulaResults(id),
    enabled: Boolean(id),
    refetchInterval: (q) => {
      const d = q.state.data as FormulaResult[] | undefined;
      return d?.some((r) => r.status === "Generating") ? 2500 : false;
    },
  });
  // The user's own note lines (the Notes tab). Sparse trigger phrases that will steer the minutes (PR 3).
  const { data: notes = [] } = useQuery({
    queryKey: ["notes", id],
    queryFn: () => api.listNotes(id),
    enabled: Boolean(id),
  });
  // Captures taken during the recording; woven into the transcript and listed in the Notes tab.
  const { data: shots = [], refetch: refetchShots } = useQuery({
    queryKey: ["screenshots", id],
    queryFn: () => api.listScreenshots(id),
    enabled: Boolean(id),
  });
  const [openShot, setOpenShot] = useState<number | null>(null);

  async function removeShot(shotId: string) {
    await api.deleteScreenshot(id, shotId);
    // Close rather than re-clamp: the modal's own index (post-delete) would otherwise dangle past the
    // end of the array for the instant between the delete and the refetch resolving.
    setOpenShot(null);
    await refetchShots();
  }
  // The user's native language drives the "Translate to …" action; resolve its display name.
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const { data: languages = [] } = useQuery({ queryKey: ["languages"], queryFn: fetchLanguages });
  const nativeLang = languages.find((l) => l.code === profile?.nativeLanguage) ?? null;

  // The room being viewed. Calendar is a personal-only concept: in a shared room we hide any linked event and
  // never link (auto or manual), and skip the calendar fetches entirely. (Above the early return below so it
  // stays a top-level hook.)
  const { currentRoom } = useRoom();
  const inSharedRoom = Boolean(currentRoom && !currentRoom.isPersonal);

  // If the user has connected Google Calendar, find the meeting this recording overlaps (the suggestion
  // that seeds the auto-saved link and the "Suggested meeting" prompt). Personal room only.
  const { data: calendarMatch } = useQuery({
    queryKey: ["calendar-match", id],
    queryFn: () => api.getCalendarMatch(id),
    enabled: Boolean(id) && !inSharedRoom && profile?.googleCalendar === true,
    retry: false,
  });
  // The full, live details of the linked event (attendees/description/location) for the Overview. Falls
  // back to the stored snapshot if Google is unreachable / the event was deleted.
  const linkedEventId = rec?.calendarLink?.eventId ?? null;
  const { data: linkedEvent } = useQuery({
    queryKey: ["calendar-event", linkedEventId],
    queryFn: () => api.getCalendarEvent(linkedEventId!),
    enabled: Boolean(linkedEventId) && !inSharedRoom,
    retry: false,
  });
  const [linkModalOpen, setLinkModalOpen] = useState(false);

  // Auto-save the best time-overlap match the first time an unlinked recording is opened, so the calendar
  // icon + Overview details appear with no clicks. Manual links and existing links are never touched, and it
  // never fires while viewing a shared room (calendar is personal-only).
  const autoLinkedRef = useRef(false);
  useEffect(() => {
    if (!id || !rec || inSharedRoom || profile?.googleCalendar !== true) return;
    if (rec.calendarLink || !calendarMatch || autoLinkedRef.current) return;
    autoLinkedRef.current = true;
    api
      .putCalendarLink(id, calendarMatch.id, false, calendarMatch.calendarId)
      .then(() => qc.invalidateQueries({ queryKey: ["recording", id] }))
      .then(() => qc.invalidateQueries({ queryKey: ["recordings"] }))
      .catch(() => {
        autoLinkedRef.current = false; // let a later render retry (e.g. the event became reachable)
      });
  }, [id, rec, inSharedRoom, profile, calendarMatch, qc]);

  async function unlinkMeeting() {
    if (!id) return;
    try {
      await api.deleteCalendarLink(id);
      autoLinkedRef.current = true; // don't immediately re-auto-link what the user just removed
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      await qc.invalidateQueries({ queryKey: ["recordings"] });
    } catch (e) {
      setActionError(apiErrorMessage(e));
    }
  }

  async function acceptSuggestion() {
    if (!id || !calendarMatch) return;
    try {
      await api.putCalendarLink(id, calendarMatch.id, false, calendarMatch.calendarId);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      await qc.invalidateQueries({ queryKey: ["recordings"] });
    } catch (e) {
      setActionError(apiErrorMessage(e));
    }
  }

  // The current user's name, shown as the "speaker" on their notes woven into the transcript. `id` (the
  // caller's user id) tells the recording's owner apart from a room co-viewer, who can read its notes and
  // screenshots but never add/edit/delete them (the API 404s those routes for anyone but the owner).
  const { id: myId, fullName, email } = useAuth();
  const audioRef = useRef<HTMLAudioElement>(null);
  const [activeIdx, setActiveIdx] = useState<number | null>(null);
  // Per-speaker audition: the label currently playing and that speaker's (merged) play ranges.
  const [playingSpeaker, setPlayingSpeaker] = useState<string | null>(null);
  // Speakers tab: the label whose segment table is expanded below the list (click a row to toggle).
  const [selectedSpeaker, setSelectedSpeaker] = useState<string | null>(null);
  // The active gapless play ranges ([] = continuous). Drives per-speaker AND "play selected"; onTimeUpdate
  // skips the gaps between ranges.
  const speakerRangesRef = useRef<PlayRange[]>([]);
  const [peopleOpen, setPeopleOpen] = useState(false);
  // Segment Select mode (local to this recording — distinct from the recordings/actions shared selection).
  const [selectMode, setSelectMode] = useState(false);
  const [selectedSegIds, setSelectedSegIds] = useState<Set<string>>(new Set());
  // Formulas tab: single-selected result id, shared between FormulasManager (the tab content) and
  // FormulasToolbar (a sibling passed to the tab's `toolbar` slot) - lifted here since neither is a
  // descendant of the other. See the transcript segment select-mode above for the same pattern.
  const [selectedFormulaResultId, setSelectedFormulaResultId] = useState<string | null>(null);
  const [formulaRunOpen, setFormulaRunOpen] = useState(false);
  const [editingFormulaResult, setEditingFormulaResult] = useState<FormulaResult | null>(null);
  // "Manage formulas" (in FormulaRunModal's footer) opens Preferences on the Formulas tab.
  const [managingFormulas, setManagingFormulas] = useState(false);
  // "Find shared formulas" opens the global discovery browser (no recording context).
  const [sharedBrowserOpen, setSharedBrowserOpen] = useState(false);
  // Mini player (the small header progress bar): current time + play/pause state of the shared <audio>.
  const [audioCur, setAudioCur] = useState(0);
  const [audioPaused, setAudioPaused] = useState(true);
  // True while "Play selected" owns the audio, so its toolbar button can offer Pause. Cleared by anything
  // that takes the audio away from the selection (the flow player, a seek, the end of the last range).
  const [selectionPlaying, setSelectionPlaying] = useState(false);
  // Reset segment selection when navigating to a different recording.
  useEffect(() => {
    setSelectMode(false);
    setSelectedSegIds(new Set());
    setSelectedFormulaResultId(null);
  }, [id]);

  // Active detail section, persisted globally (like the left "Meetings" panel) so it survives reloads and
  // navigating between recordings. Defaults to the hub — but a chat transcript deep-link (?t=…) targets a
  // segment in the Transcript, so open there when the URL carries one. `initialSection` also migrates the
  // keys the old tab strip persisted ("overview" → hub, "attachments" → files).
  const [tab, setTab] = useState<SectionKey>(() =>
    initialSection(localStorage.getItem(DETAIL_SECTION_KEY), searchParams.get("t") != null),
  );
  const selectTab = (key: SectionKey) => {
    setTab(key);
    localStorage.setItem(DETAIL_SECTION_KEY, key);
    // Entering a section starts from Play: the selection button shouldn't come back reading Pause for audio
    // the user started in a previous visit.
    setSelectionPlaying(false);
  };

  // When opened from a chat transcript link (/recordings/:id?t=ms), switch to the Transcript tab (so the
  // segment is rendered), then highlight and scroll to it. The nested rAF waits one extra frame so the tab's
  // content has committed even when we had to switch tabs first.
  const tParam = searchParams.get("t");
  useEffect(() => {
    if (tParam == null) return;
    const ms = Number(tParam);
    const segs = rec?.current?.segments;
    if (!Number.isFinite(ms) || !segs || segs.length === 0) return;
    const idx = segmentIndexAtMs(segs, ms);
    if (idx < 0) return;
    setTab("transcript");
    setActiveIdx(idx);
    const segId = segs[idx].id;
    // Defer until the (possibly just-switched) transcript rows have rendered.
    requestAnimationFrame(() =>
      requestAnimationFrame(() =>
        document.getElementById(`seg-${segId}`)?.scrollIntoView({ block: "center", behavior: "smooth" }),
      ),
    );
  }, [tParam, rec]);

  // Prev/next across the moments a chat answer cited for this recording (?ts=…).
  const matchTimes = parseMatchTimes(searchParams.get("ts"));
  const activeMatch = tParam != null ? Number(tParam) : null;
  const matchIdx = activeMatch != null ? matchTimes.indexOf(activeMatch) : -1;
  function goToMatch(idx: number) {
    const ms = matchTimes[idx];
    if (ms == null) return;
    setSearchParams(
      (prev) => {
        prev.set("t", String(ms));
        return prev;
      },
      { replace: true },
    );
  }
  const [requeuing, setRequeuing] = useState(false);
  const [summarizing, setSummarizing] = useState(false);
  const [extracting, setExtracting] = useState(false);
  const [reidentifying, setReidentifying] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [sharing, setSharing] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [editingSummary, setEditingSummary] = useState(false);
  const [editingMinutes, setEditingMinutes] = useState(false);
  const [emailMinutesOpen, setEmailMinutesOpen] = useState(false);
  const [managingTypes, setManagingTypes] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [editingSeg, setEditingSeg] = useState<SegmentDto | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionInfo, setActionInfo] = useState<string | null>(null);
  const [retranscribeOpen, setRetranscribeOpen] = useState(false);
  // When the transcript has edited/translated segments, the user can flip the whole list back to the
  // model's original words.
  const [showOriginal, setShowOriginal] = useState(false);
  const [translating, setTranslating] = useState(false);

  // Mirror this page's transient state into the app-wide bottom status bar (the in-page banners stay too).
  // Status-based pipeline progress (transcribing/summarising/merging/queuing) is derived by the bar from the
  // recordings list; here we surface the client-only actions that aren't recording statuses.
  const { setStatus } = useStatus();
  useEffect(() => { if (extracting) setStatus(t("workspace:extractingActions"), "progress"); }, [extracting]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (translating) setStatus(t("workspace:translating"), "progress"); }, [translating]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (reidentifying) setStatus(t("workspace:reidentifying"), "progress"); }, [reidentifying]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (requeuing) setStatus(t("workspace:retranscribing"), "progress"); }, [requeuing]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (minutesRunning) setStatus(t("workspace:generatingMinutes"), "progress"); }, [minutesRunning]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (actionInfo) setStatus(actionInfo, "success"); }, [actionInfo]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (actionError) setStatus(actionError, "error"); }, [actionError]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    const hub = createHub((e) => {
      if (e.recordingId === id) qc.invalidateQueries({ queryKey: ["recording", id] });
    });
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [id, qc]);

  // Transient action banners belong to the recording that produced them — clear them when the route
  // switches to a different recording so they don't bleed onto an unrelated transcript.
  useEffect(() => {
    setActionInfo(null);
    setActionError(null);
  }, [id]);

  const labels = useMemo(() => {
    const set = new Set<string>();
    rec?.current?.segments.forEach((s) => set.add(s.speaker));
    return [...set];
  }, [rec]);

  // Segment count per speaker (shown in each speaker's row).
  const speakerCounts = useMemo(() => {
    const m = new Map<string, number>();
    rec?.current?.segments.forEach((s) => m.set(s.speaker, (m.get(s.speaker) ?? 0) + 1));
    return m;
  }, [rec]);

  // Total spoken time (ms) per speaker, summed from their segments — shown next to the segment count.
  const speakerDurations = useMemo(() => {
    const m = new Map<string, number>();
    rec?.current?.segments.forEach((s) =>
      m.set(s.speaker, (m.get(s.speaker) ?? 0) + Math.max(0, s.endMs - s.startMs)));
    return m;
  }, [rec]);

  // Labels flagged "Multiple Speakers" — their segment/speaker display is localised in-app.
  const multiSpeakerLabels = useMemo(
    () => new Set((rec?.speakers ?? []).filter((s) => s.isMultiSpeaker).map((s) => s.label)),
    [rec],
  );

  // A diarization label's shown name, applying the same "Multiple Speakers" localisation the transcript
  // and speaker rows use. The hub's avatars and the flow track's legend both name speakers, so they share it.
  const speakerNameOf = (label: string) =>
    multiSpeakerLabels.has(label)
      ? t("workspace:multipleSpeakers")
      : rec?.speakers.find((s) => s.label === label)?.displayName ?? label;

  // The applied minutes template, for the Formulas tile's "From <template>" line. `MeetingTypeMenu` fetches
  // the same list off the same query key, so this is served from cache rather than a second round trip.
  const { data: meetingTypes = [] } = useQuery({ queryKey: ["meeting-types"], queryFn: api.listMeetingTypes });
  const appliedMeetingType = selectedMeetingType(meetingTypes, rec?.meetingTypeId);

  async function assignSpeaker(label: string, profileId: string | null) {
    setActionError(null);
    try {
      await api.assignSpeaker(id, label, profileId);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errAssignSpeaker")));
    }
  }

  // Enrol a new person from the typeahead's "Create" row, using the text the user typed.
  async function newPerson(label: string, typedName: string) {
    const name = typedName.trim();
    if (!name) return;
    setActionError(null);
    try {
      await api.createSpeakerProfile(name, id, label);
      // Awaited so the typeahead's spinner covers the refetch too - the new person isn't really "there"
      // until the reloaded lists show them.
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["recording", id] }),
        qc.invalidateQueries({ queryKey: ["speaker-profiles"] }),
      ]);
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errCreatePerson")));
    }
  }

  // Mark a speaker as "Multiple Speakers" (overlapping speech) — detaches it from any voiceprint.
  async function markMulti(label: string) {
    setActionError(null);
    try {
      await api.markMultiSpeaker(id, label);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errAssignSpeaker")));
    }
  }


  // The transcript rows carry the same assignment typeahead as the Speakers tab, driving the same handlers.
  const segmentAssign: SegmentAssign = {
    profiles,
    infoOf: (label) => rec?.speakers.find((s) => s.label === label),
    onAssign: assignSpeaker,
    onCreate: newPerson,
    onMulti: markMulti,
  };

  async function saveRecordingName(name: string) {
    await api.renameRecording(id, name.trim() || null);
    setRenaming(false);
    qc.invalidateQueries({ queryKey: ["recording", id] });
    // Also refresh the left list so its row label updates immediately (not only after a manual refresh).
    qc.invalidateQueries({ queryKey: ["recordings"] });
  }

  // Re-transcribe with the (optional) speaker-count hints chosen in the modal.
  async function retranscribe(min: number | null, max: number | null) {
    setActionError(null);
    setActionInfo(null);
    setRequeuing(true);
    try {
      await api.retranscribe(id, { speakers: { min, max } });
      setRetranscribeOpen(false);
      // Progress shows in the status bar only (not a banner). The transient "retranscribing" push (the requeuing
      // effect) hands off to the recordings-list pipeline (Queued -> Transcribing) once the requeue is accepted.
      await Promise.all([
        qc.invalidateQueries({ queryKey: ["recording", id] }),
        qc.invalidateQueries({ queryKey: ["recordings"] }),
      ]);
      setStatus(null);
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errRetranscribe")));
    } finally {
      setRequeuing(false);
    }
  }

  async function summarize() {
    // Re-summarising replaces the summary — confirm first when the user has hand-edited it.
    if (rec?.summary?.isUserEdited && !window.confirm(t("workspace:confirmResummarise"))) return;
    setActionError(null);
    setSummarizing(true);
    try {
      await api.summarize(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errSummarise")));
    } finally {
      setSummarizing(false);
    }
  }

  // Copy a persistent rich-text link to this transcript (the name shows as the link text).
  async function copyLink() {
    setActionError(null);
    setActionInfo(null);
    const ok = await copyRichLink(transcriptUrl(id), rec?.name ?? rec?.title ?? "");
    if (ok) setActionInfo(t("workspace:linkCopied"));
    else setActionError(t("workspace:errCopyLink"));
  }

  // Save a manually-written/edited summary (flagged user-edited so auto-summary won't clobber it).
  async function saveSummary(text: string) {
    setActionError(null);
    try {
      await api.updateSummary(id, text);
      setEditingSummary(false);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errEditSummary")));
    }
  }

  // Re-create the meeting minutes via the LLM — confirm first when the user has hand-edited them.
  async function recreateMinutes() {
    if (rec?.meetingMinutes?.isUserEdited && !window.confirm(t("workspace:confirmRecreateMinutes"))) return;
    setActionError(null);
    setActionInfo(null);
    // Progress shows in the status bar only (not a banner); cleared when the fresh minutes arrive.
    minutesBaselineRef.current = rec?.meetingMinutes?.createdAt ?? null;
    setMinutesRunning(true);
    try {
      await api.generateMeetingMinutes(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setMinutesRunning(false);
      setActionError(apiErrorMessage(e, t("workspace:errMinutes")));
    }
  }

  // Apply a meeting type and re-run the minutes. Disable the picker until the new minutes arrive.
  async function applyMeetingType(typeId: string) {
    if (rec?.meetingMinutes?.isUserEdited && !window.confirm(t("workspace:confirmRecreateMinutes"))) return;
    setActionError(null);
    setActionInfo(null);
    minutesBaselineRef.current = rec?.meetingMinutes?.createdAt ?? null;
    setMinutesRunning(true);
    try {
      await api.applyMeetingType(id, typeId);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setMinutesRunning(false);
      setActionError(apiErrorMessage(e, t("workspace:errMinutes")));
    }
  }

  // Clear the picker's busy state + the status-bar progress once fresh minutes have arrived (timestamp changed).
  useEffect(() => {
    if (minutesRunning && rec?.meetingMinutes && rec.meetingMinutes.createdAt !== minutesBaselineRef.current) {
      setMinutesRunning(false);
      setStatus(null);
    }
  }, [minutesRunning, rec?.meetingMinutes?.createdAt]); // eslint-disable-line react-hooks/exhaustive-deps

  // Safety net: if a run never produces fresh minutes (the generation failed server-side, so no new
  // timestamp ever arrives), stop the busy state + polling after a generous cap rather than spinning forever.
  useEffect(() => {
    if (!minutesRunning) return;
    const timer = setTimeout(() => {
      setMinutesRunning(false);
      setStatus(null);
      setActionInfo(t("workspace:minutesSlow"));
    }, 5 * 60 * 1000);
    return () => clearTimeout(timer);
  }, [minutesRunning]); // eslint-disable-line react-hooks/exhaustive-deps

  // Save hand-edited meeting minutes (Markdown; flagged user-edited so the auto-generator won't clobber them).
  async function saveMinutes(markdown: string) {
    setActionError(null);
    try {
      await api.updateMeetingMinutes(id, markdown);
      setEditingMinutes(false);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errMinutes")));
    }
  }

  // Email the minutes to me. With attachments, ask whether to include them first; otherwise send directly.
  function emailMinutes() {
    if (attachments.length > 0) {
      setEmailMinutesOpen(true);
      return;
    }
    void sendMinutesEmail(false);
  }

  async function sendMinutesEmail(includeAttachments: boolean) {
    setActionError(null);
    setActionInfo(null);
    try {
      await api.emailMeetingMinutes(id, includeAttachments);
      setEmailMinutesOpen(false);
      setActionInfo(t("workspace:emailedMinutes"));
    } catch (e) {
      setEmailMinutesOpen(false);
      setActionError(apiErrorMessage(e, t("workspace:errEmail")));
    }
  }

  const refreshAttachments = () => {
    qc.invalidateQueries({ queryKey: ["attachments", id] });
    qc.invalidateQueries({ queryKey: ["user-storage"] }); // attachment bytes count toward quota
  };

  const refreshFormulas = () => {
    void qc.invalidateQueries({ queryKey: ["formula-results", id] });
    // Also refresh the two-panel preview so an edited result's rendered body isn't stale.
    void qc.invalidateQueries({ queryKey: ["formula-result-text", id] });
  };

  async function downloadFormulaResult() {
    if (!selectedFormulaResultId) return;
    setActionError(null);
    try {
      await api.downloadFormulaResult(id, selectedFormulaResultId);
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errDownloadFormulaResult")));
    }
  }

  async function emailFormulaResult() {
    if (!selectedFormulaResultId) return;
    setActionError(null);
    setActionInfo(null);
    try {
      await api.emailFormulaResult(id, selectedFormulaResultId);
      setActionInfo(t("workspace:emailedFormulaResult"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errEmailFormulaResult")));
    }
  }

  async function deleteFormulaResult() {
    const result = formulaResults.find((r) => r.id === selectedFormulaResultId);
    if (!result) return;
    if (!window.confirm(t("workspace:confirmDeleteFormulaResult", { name: result.name }))) return;
    setActionError(null);
    try {
      await api.deleteFormulaResult(id, result.id);
      setSelectedFormulaResultId(null);
      refreshFormulas();
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errDeleteFormulaResult")));
    }
  }

  function openFormulaResult() {
    const result = formulaResults.find((r) => r.id === selectedFormulaResultId);
    if (result) setEditingFormulaResult(result);
  }

  // Files dropped anywhere on the detail page are attached to this recording.
  async function onDropFiles(e: React.DragEvent) {
    e.preventDefault();
    setDragging(false);
    const files = e.dataTransfer.files;
    if (!files || files.length === 0) return;
    setActionError(null);
    try {
      for (const f of Array.from(files)) await api.addFileAttachment(id, f);
      refreshAttachments();
    } catch (err) {
      setActionError(apiErrorMessage(err, t("workspace:errAddAttachment")));
    }
  }

  // Extract action items from the current transcript via the LLM, then show the Actions panel.
  // Re-extracting replaces the existing list — confirm first if there's anything to lose.
  async function extractActions() {
    if (rec?.actionsExtracted && rec.actions.length > 0 &&
        !window.confirm(t("workspace:confirmReextract"))) return;
    setActionError(null);
    setActionInfo(null);
    setExtracting(true);
    try {
      const actions = await api.extractActions(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo(actions.length ? t("workspace:extractedActions", { count: actions.length })
                                   : t("workspace:noActionsFound"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errExtract")));
    } finally {
      setExtracting(false);
    }
  }

  // Translate the whole transcript (+ summary + actions) into the user's native language. Overwrites any
  // existing revision/translation, so confirm first when there's edited text to lose.
  async function translateRecording() {
    if (!nativeLang) return;
    if (rec?.current && hasRevisions(rec.current.segments) &&
        !window.confirm(t("workspace:confirmTranslate", { language: nativeLang.englishName })))
      return;
    setActionError(null);
    setActionInfo(null);
    setTranslating(true);
    try {
      await api.translateRecording(id, nativeLang.code);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo(t("workspace:translatedTo", { language: nativeLang.englishName }));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errTranslateTranscript")));
    } finally {
      setTranslating(false);
    }
  }


  async function addAction() {
    setActionError(null);
    try {
      await api.createAction(id, { text: "", actor: "", deadline: "" });
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errAddAction")));
    }
  }

  async function updateAction(actionId: string, patch: { text?: string; actor?: string; deadline?: string }) {
    setActionError(null);
    try {
      await api.updateAction(id, actionId, patch);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errUpdateAction")));
    }
  }

  async function removeAction(actionId: string) {
    setActionError(null);
    try {
      await api.deleteAction(id, actionId);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errRemoveAction")));
    }
  }

  async function toggleActionComplete(actionId: string, completed: boolean) {
    setActionError(null);
    try {
      await api.completeActions([actionId], completed);
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["actions", "all"] }); // keep the Actions tab in sync
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errUpdateAction")));
    }
  }

  // ---- Notes tab handlers (the user's own note lines) ----
  async function addNote(text: string) {
    setActionError(null);
    try {
      await api.createNotes(id, [{ text }]);
      qc.invalidateQueries({ queryKey: ["notes", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e));
    }
  }

  async function editNote(noteId: string, text: string) {
    setActionError(null);
    try {
      await api.updateNote(id, noteId, text);
      qc.invalidateQueries({ queryKey: ["notes", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e));
    }
  }

  async function removeNote(noteId: string) {
    setActionError(null);
    try {
      await api.deleteNote(id, noteId);
      qc.invalidateQueries({ queryKey: ["notes", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e));
    }
  }

  // Jump from a stamped note line to that moment in the transcript (the ?t= deep-link behaviour).
  function jumpToMs(ms: number) {
    setSearchParams(
      (prev) => {
        prev.set("t", String(ms));
        return prev;
      },
      { replace: true },
    );
    selectTab("transcript");
  }

  async function mergeSegments() {
    if (!window.confirm(t("workspace:confirmMerge"))) return;
    setActionError(null);
    setActionInfo(null);
    try {
      await api.mergeSegments(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errMerge")));
    }
  }

  async function emailTranscript() {
    setActionError(null);
    setActionInfo(null);
    try {
      await api.emailTranscript(id);
      setActionInfo(t("workspace:emailedTranscript"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errEmail")));
    }
  }

  async function reidentify() {
    setActionError(null);
    setActionInfo(null);
    setReidentifying(true);
    try {
      await api.reidentify(id);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
      setActionInfo(t("workspace:reidentified"));
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errReidentify")));
    } finally {
      setReidentifying(false);
    }
  }

  // Lazily resolve the presigned URL and seek the shared <audio> element.
  async function playFrom(startMs: number) {
    const el = audioRef.current;
    if (!el) return;
    setActionError(null);
    exitSpeakerMode(); // a normal play/seek leaves single-speaker audition
    try {
      if (!el.src) el.src = await api.audioUrl(id);
      el.currentTime = startMs / 1000;
      await el.play();
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errPlayAudio")));
    }
  }

  function exitSpeakerMode() {
    speakerRangesRef.current = [];
    setPlayingSpeaker(null);
    setSelectionPlaying(false);
  }

  // Audition a single speaker: play only their (merged) segments, skipping everyone else's audio.
  async function playSpeaker(label: string) {
    const el = audioRef.current;
    if (!el) return;
    const ranges = speakerRanges(rec?.current?.segments ?? [], label);
    if (ranges.length === 0) return;
    setActionError(null);
    try {
      if (!el.src) el.src = await api.audioUrl(id);
      speakerRangesRef.current = ranges;
      setPlayingSpeaker(label);
      el.currentTime = ranges[0].start / 1000;
      await el.play();
    } catch (e) {
      exitSpeakerMode();
      setActionError(apiErrorMessage(e, t("workspace:errPlayAudio")));
    }
  }

  function toggleSpeaker(label: string) {
    if (playingSpeaker === label) {
      audioRef.current?.pause();
      exitSpeakerMode();
    } else {
      void playSpeaker(label);
    }
  }

  // Delete every segment attributed to one speaker (the backend then prunes the now-empty speaker row,
  // so it drops out of the list on refetch).
  async function deleteSpeaker(label: string, name: string) {
    const ids = (rec?.current?.segments ?? []).filter((s) => s.speaker === label).map((s) => s.id);
    if (ids.length === 0) return;
    if (!window.confirm(t("workspace:confirmDeleteSpeaker", { name, count: ids.length }))) return;
    setActionError(null);
    try {
      if (playingSpeaker === label) {
        audioRef.current?.pause();
        exitSpeakerMode();
      }
      await api.deleteSegments(id, ids);
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errDeleteSegment")));
    }
  }

  // Play only the selected segments, gaplessly (skipping the gaps between non-adjacent picks).
  async function playSelected() {
    const el = audioRef.current;
    if (!el) return;
    const ranges = selectedRanges(rec?.current?.segments ?? [], selectedSegIds);
    if (ranges.length === 0) return;
    setActionError(null);
    try {
      if (!el.src) el.src = await api.audioUrl(id);
      setPlayingSpeaker(null);
      speakerRangesRef.current = ranges;
      el.currentTime = ranges[0].start / 1000;
      await el.play();
      setSelectionPlaying(true);
    } catch (e) {
      exitSpeakerMode();
      setActionError(apiErrorMessage(e, t("workspace:errPlayAudio")));
    }
  }

  /// Stop a selection that is playing (the toolbar's Play selected doubles as Pause while it runs).
  function pauseSelected() {
    audioRef.current?.pause();
    exitSpeakerMode();
  }

  // Mini-player play/pause toggle (loads the audio lazily on first use). It takes the audio over from a
  // playing selection, so the toolbar's Pause reverts to Play.
  async function togglePlayPause() {
    const el = audioRef.current;
    if (!el) return;
    setSelectionPlaying(false);
    try {
      if (!el.src) el.src = await api.audioUrl(id);
      if (el.paused) await el.play();
      else el.pause();
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errPlayAudio")));
    }
  }

  function onTimeUpdate() {
    const el = audioRef.current;
    const segs = rec?.current?.segments;
    if (!el) return;
    setAudioCur(el.currentTime);
    if (!segs) return;
    const ms = el.currentTime * 1000;
    // Range playback (per-speaker audition OR "play selected"): when playback leaves the current range, jump
    // to the next range (skipping the gap) or stop at the end.
    if (speakerRangesRef.current.length && !rangeAt(speakerRangesRef.current, ms)) {
      const next = nextRangeStart(speakerRangesRef.current, ms);
      if (next == null) {
        el.pause();
        exitSpeakerMode();
      } else {
        el.currentTime = next / 1000;
      }
    }
    const idx = segs.findIndex((s) => ms >= s.startMs && ms < s.endMs);
    setActiveIdx(idx >= 0 ? idx : null);
  }

  // ---- Segment selection + bulk actions (the transcript Select-mode toolbar) ----
  /// Click a segment row: in Select mode, toggle it; otherwise pick just this one (replacing the selection).
  function clickSegment(segId: string) {
    setSelectedSegIds((prev) => {
      if (!selectMode) return new Set([segId]);
      const next = new Set(prev);
      if (next.has(segId)) next.delete(segId);
      else next.add(segId);
      return next;
    });
  }

  function editSelected() {
    if (selectedSegIds.size !== 1) return;
    const segId = [...selectedSegIds][0];
    const seg = rec?.current?.segments.find((s) => s.id === segId);
    if (seg) setEditingSeg(seg);
  }

  async function deleteSelected() {
    const ids = [...selectedSegIds];
    if (ids.length === 0) return;
    if (!window.confirm(t("workspace:confirmDeleteSelected", { count: ids.length }))) return;
    setActionError(null);
    try {
      await api.deleteSegments(id, ids);
      setSelectedSegIds(new Set());
      qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errDeleteSegment")));
    }
  }

  async function translateSelected() {
    const ids = [...selectedSegIds];
    if (ids.length === 0 || !nativeLang) return;
    setActionError(null);
    setActionInfo(null);
    setTranslating(true);
    try {
      await api.translateSegments(id, ids, nativeLang.code);
      await qc.invalidateQueries({ queryKey: ["recording", id] });
    } catch (e) {
      setActionError(apiErrorMessage(e, t("workspace:errTranslateSegment")));
    } finally {
      setTranslating(false);
    }
  }

  if (!rec) return <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;

  // Only the recording's owner may add/edit/delete its notes and screenshots - a room co-viewer reads the
  // same woven-in transcript but the mutating routes are owner-only (404 for anyone else).
  const isOwner = myId != null && rec.recordedByUserId === myId;

  // Formula-result mutation gate: mirrors FormulaResultsController.CanEdit - the result's creator OR the
  // recording's owner. Unlike the section (folder) side, there is no room ManageContents check here at all;
  // gate against this side's own rule rather than reusing the folder's. Open/Download/Email are reads (the
  // server only gates List/Get/Download/Email on being able to view the recording at all, not per-result), so
  // only Delete (and the "Open" modal's Save, via `editable` below) needs this.
  const selectedFormulaResult = formulaResults.find((r) => r.id === selectedFormulaResultId) ?? null;
  const canManageFormulaResult = (r: { createdByUserId: string | null }) => isOwner || (myId != null && r.createdByUserId === myId);
  const canManageSelectedFormulaResult = !!selectedFormulaResult && canManageFormulaResult(selectedFormulaResult);

  const hasTranscript = (rec.current?.segments.length ?? 0) > 0;
  const isSummarizing = rec.status === "Summarizing" || summarizing;

  // Room context for sharing: the recording's home (main) room, the rooms it is in, and whether the room
  // currently being viewed is a shared placement (which enables Remove-from-room and hides Delete).
  const homeRoom = rec.rooms?.find((r) => r.isMain);
  const inRoomIds = (rec.rooms ?? []).map((r) => r.id);
  const viewingSharedPlacement = Boolean(
    currentRoom && !currentRoom.isPersonal && inRoomIds.includes(currentRoom.id),
  );
  const sharedRoomNames = (rec.rooms ?? []).filter((r) => !r.isMain).map((r) => r.name);

  const menuActions = recordingMenu({
    onRename: () => setRenaming(true),
    onCopyLink: copyLink,
    onRetranscribe: () => setRetranscribeOpen(true),
    onSummarise: summarize,
    onEditSummary: () => setEditingSummary(true),
    onGenerateMinutes: recreateMinutes,
    onExtractActions: extractActions,
    onReidentify: reidentify,
    onTranslate: nativeLang ? translateRecording : undefined,
    translateLabel: nativeLang?.englishName,
    onMove: () => setMoving(true),
    onShare: homeRoom ? () => setSharing(true) : undefined,
    onRemoveFromRoom: viewingSharedPlacement
      ? async () => {
          if (!window.confirm(t("workspace:confirmRemoveFromRoom", { room: currentRoom!.name }))) return;
          await api.removeRecordingFromRoom(currentRoom!.id, id);
          navigate("/");
          qc.invalidateQueries({ queryKey: ["recordings"] });
        }
      : undefined,
    onPlay: () => void playFrom(0),
    onDownloadTranscript: () => setDownloading(true),
    onEmailTranscript: emailTranscript,
    onDownloadAudio: () => void api.downloadAudio(id),
    onDeleteAudio: async () => {
      if (!window.confirm(t("workspace:confirmDeleteAudio", { name: rec.name ?? rec.title }))) return;
      await api.deleteAudio(id);
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    },
    onSetAudioProtection: async () => {
      await api.setAudioProtection(id, !rec.audioProtectedAt);
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
    },
    isAudioProtected: Boolean(rec.audioProtectedAt),
    onDelete: async () => {
      // Destroying a recording removes it from every shared room too - name them so the recorder knows.
      const message = sharedRoomNames.length
        ? t("workspace:confirmDeleteShared", { name: rec.name ?? rec.title, rooms: sharedRoomNames.join(", ") })
        : t("workspace:confirmDelete", { name: rec.name ?? rec.title });
      if (!window.confirm(message)) return;
      await api.deleteRecording(id);
      // Leave the (now-deleted) transcript so no further action targets a missing recording, and refresh the list.
      navigate("/");
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    },
    // A recording can only be destroyed from its home room; viewing it in a shared room hides Delete.
    canDelete: !viewingSharedPlacement,
    hasTranscript,
    hasAudio: rec.hasAudio,
    isSummarizing,
    isProcessing: isProcessing(rec.status),
  }, t);

  // The linked-meeting block. It lived on the old Overview tab; the hero card has no slot for a whole
  // invite, so it keeps its own card directly beneath the hub. Calendar is personal-only, so none of this
  // shows while viewing the recording in a shared room.
  // The meeting this recording came from. Calendar is personal-only, so it is hidden entirely while viewing
  // the recording in a shared room.
  const meetingCard = !inSharedRoom ? (
    <MeetingCard
      calendarLink={rec.calendarLink}
      linkedEvent={linkedEvent}
      suggestion={calendarMatch}
      calendarConnected={profile?.googleCalendar === true}
      onLink={() => setLinkModalOpen(true)}
      onAcceptSuggestion={acceptSuggestion}
      onUnlink={unlinkMeeting}
    />
  ) : null;

  // The hub: the detail page's landing view. Everything the old Overview tab carried is here — its facts
  // as the hero's chip row, its summary inline in the hero, its calendar block beneath — plus a tile per
  // section, each showing that section's real count and a preview of what is inside it.
  const hubView = (
    <div className="flex flex-col gap-3.5">
      <RecordingHub
        rec={rec}
        notes={notes}
        attachments={attachments}
        formulaResults={formulaResults}
        meetingTypeTitle={appliedMeetingType?.title ?? null}
        speakerNameOf={speakerNameOf}
        minutesRunning={minutesRunning}
        hasTranscript={hasTranscript}
        isSummarizing={isSummarizing}
        showRooms={!inSharedRoom}
        onOpenSection={selectTab}
        onApplyMeetingType={applyMeetingType}
        onEditSummary={() => setEditingSummary(true)}
        onResummarise={summarize}
        onNewNote={() => selectTab("notes")}
        onAddFile={() => selectTab("files")}
        onRunFormula={() => setFormulaRunOpen(true)}
      />
      {meetingCard}
    </div>
  );

  // The sections you drill into from the hub. Same shape the tab strip used, so the bodies carry over
  // unchanged; only the chrome around them (a breadcrumb instead of a tab strip) is different.
  const detailTabs: DetailSection[] = [
    {
      key: "minutes",
      label: t("workspace:detailTabMinutes"),
      icon: <MinutesGlyph size={15} />,
      toolbar: (
        <>
          <MeetingTypeMenu
            currentTypeId={rec.meetingTypeId ?? null}
            busy={minutesRunning || isSummarizing}
            onApply={applyMeetingType}
          />
          <ToolbarButton
            label={t("workspace:mtManage")}
            icon={SlidersIcon}
            onClick={() => setManagingTypes(true)}
          />
          <ToolbarButton
            label={t("workspace:editMeetingMinutes")}
            icon={PencilIcon}
            disabled={!rec.meetingMinutes}
            onClick={() => setEditingMinutes(true)}
          />
          <ToolbarButton
            label={t("workspace:emailMinutes")}
            icon={MailIcon}
            disabled={!rec.meetingMinutes}
            onClick={emailMinutes}
          />
          <ToolbarButton
            label={t("workspace:recreateMeetingMinutes")}
            icon={RefreshIcon}
            disabled={!hasTranscript || isSummarizing}
            onClick={recreateMinutes}
          />
        </>
      ),
      content: rec.meetingMinutes ? (
        <div className="px-4 pb-4">
          {rec.meetingMinutes.isUserEdited && (
            <p className="mb-1 text-xs italic text-gray-400 dark:text-gray-500">{t("workspace:minutesEditedHint")}</p>
          )}
          <div
            className="break-words text-sm text-gray-800 dark:text-gray-200
              [&_h1]:mb-2 [&_h1]:mt-1 [&_h1]:text-lg [&_h1]:font-bold
              [&_h2]:mb-1 [&_h2]:mt-3 [&_h2]:text-base [&_h2]:font-semibold
              [&_h3]:mb-1 [&_h3]:mt-2 [&_h3]:font-semibold
              [&_ul]:my-1 [&_ul]:list-disc [&_ul]:pl-6 [&_ol]:my-1 [&_ol]:list-decimal [&_ol]:pl-6 [&_p]:my-1
              [&_table]:my-2 [&_table]:border-collapse [&_th]:border [&_th]:px-2 [&_th]:py-1 [&_th]:font-semibold
              [&_td]:border [&_td]:px-2 [&_td]:py-1 dark:[&_th]:border-gray-700 dark:[&_td]:border-gray-700"
            dangerouslySetInnerHTML={{ __html: renderMarkdown(rec.meetingMinutes.text) }}
          />
        </div>
      ) : (
        <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("workspace:minutesEmpty")}</p>
      ),
    },
    {
      key: "actions",
      label: t("workspace:detailTabActions"),
      icon: <ActionsGlyph size={15} />,
      toolbar: (
        <ToolbarButton
          label={t("workspace:extractActionsAction")}
          icon={RefreshIcon}
          onClick={extractActions}
          disabled={!hasTranscript}
        />
      ),
      content: (
        <ActionsTable
          actions={rec.actions}
          onAdd={addAction}
          onUpdate={updateAction}
          onToggleComplete={toggleActionComplete}
          onDelete={removeAction}
        />
      ),
    },
    {
      key: "notes",
      label: t("workspace:detailTabNotes"),
      icon: <NotesGlyph size={15} />,
      // Notes steer the minutes (and fill a template's Enhanced-notes section), so offer a re-run here.
      toolbar: (
        <ToolbarButton
          label={t("workspace:recreateMeetingMinutes")}
          icon={RefreshIcon}
          disabled={!hasTranscript || isSummarizing}
          onClick={recreateMinutes}
        />
      ),
      content: (
        <div className="px-4 pb-4 space-y-3">
          <NotesSection
            notes={notes}
            onAdd={isOwner ? addNote : undefined}
            onEdit={isOwner ? editNote : undefined}
            onDelete={isOwner ? removeNote : undefined}
            onJump={jumpToMs}
          />
          <ScreenshotsSection recordingId={id} shots={shots} onOpen={setOpenShot} />
        </div>
      ),
    },
    {
      key: "speakers",
      label: t("workspace:detailTabSpeakers"),
      icon: <SpeakersGlyph size={15} />,
      toolbar: (
        <>
          <ToolbarButton label={t("workspace:managePeople")} icon={UsersIcon} onClick={() => setPeopleOpen(true)} />
          <ToolbarButton
            label={t("workspace:reidentifyAction")}
            icon={RefreshIcon}
            disabled={!rec.hasAudio || !hasTranscript || reidentifying}
            onClick={reidentify}
          />
        </>
      ),
      content:
        labels.length > 0 ? (
          <>
            <div className="flex flex-col gap-2 px-4 pb-4">
              {labels.map((label) => {
                const info = rec.speakers.find((s) => s.label === label);
                return (
                  <SpeakerRow
                    key={label}
                    label={label}
                    info={info}
                    initial={
                      info?.isMultiSpeaker
                        ? t("workspace:multipleSpeakers")
                        : rec.speakerNames[label] ?? info?.displayName ?? label
                    }
                    count={speakerCounts.get(label) ?? 0}
                    durationMs={speakerDurations.get(label) ?? 0}
                    profiles={profiles}
                    canPlay={rec.hasAudio}
                    playing={playingSpeaker === label}
                    selected={selectedSpeaker === label}
                    onSelect={() => setSelectedSpeaker((cur) => (cur === label ? null : label))}
                    onTogglePlay={() => toggleSpeaker(label)}
                    onDelete={(name) => deleteSpeaker(label, name)}
                    onAssign={(profileId) => assignSpeaker(label, profileId)}
                    onCreate={(name) => newPerson(label, name)}
                    onMulti={() => markMulti(label)}
                  />
                );
              })}
            </div>
            {/* Selected speaker: their segments, in the same format as the Transcript tab. Click a row to play
                from there. */}
            {selectedSpeaker && rec.current && (
              <div className="px-4 pb-4">
                <h4 className="mb-2 text-xs font-medium text-gray-500 dark:text-gray-400">
                  {t("workspace:speakerSegmentsHeading", {
                    name: multiSpeakerLabels.has(selectedSpeaker)
                      ? t("workspace:multipleSpeakers")
                      : rec.speakerNames[selectedSpeaker] ??
                        rec.speakers.find((s) => s.label === selectedSpeaker)?.displayName ??
                        selectedSpeaker,
                  })}
                </h4>
                <ul className="space-y-2">
                  {rec.current.segments
                    .filter((s) => s.speaker === selectedSpeaker)
                    .map((s) => (
                      <SegmentRow
                        key={s.id}
                        seg={s}
                        speakerName={
                          multiSpeakerLabels.has(s.speaker) ? t("workspace:multipleSpeakers") : s.speakerDisplay
                        }
                        active={activeIdx != null && rec.current!.segments[activeIdx]?.id === s.id}
                        selected={false}
                        selectMode={false}
                        showOriginal={showOriginal}
                        onClick={() => playFrom(s.startMs)}
                      />
                    ))}
                </ul>
              </div>
            )}
          </>
        ) : (
          <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("workspace:speakersEmpty")}</p>
        ),
    },
    {
      key: "transcript",
      label: t("workspace:detailTabTranscript"),
      icon: <TranscriptGlyph size={15} />,
      meta: rec.current
        ? t("workspace:hubTranscriptSubtitle", {
            segments: rec.current.segments.length,
            duration: formatDurationApprox(rec.durationMs),
          })
        : undefined,
      // The old range-input mini-player that sat here has been replaced by the conversation-flow player in
      // the section body below - it seeks the same audio element, but shows who is speaking while it does.
      toolbar: rec.current ? (
        <>
          <ToolbarButton
            label={selectionPlaying ? t("workspace:pauseSelected") : t("workspace:playSelected")}
            icon={selectionPlaying ? PauseIcon : PlayIcon}
            active={selectionPlaying}
            onClick={selectionPlaying ? pauseSelected : playSelected}
            disabled={!rec.hasAudio || (!selectionPlaying && selectedSegIds.size === 0)}
          />
          <ToolbarButton label={t("workspace:mergeRows")} icon={MergeIcon} onClick={mergeSegments} />
          <ToolbarButton
            label={selectMode ? t("workspace:doneSelecting") : t("workspace:selectSegments")}
            icon={SelectIcon}
            active={selectMode}
            onClick={() => setSelectMode((v) => !v)}
          />
          <ToolbarButton label={t("workspace:editSegment")} icon={PencilIcon} onClick={editSelected} disabled={selectedSegIds.size !== 1} />
          {nativeLang && (
            <ToolbarButton
              label={t("recordings:translateTo", { language: nativeLang.englishName })}
              icon={GlobeIcon}
              onClick={translateSelected}
              disabled={selectedSegIds.size === 0 || translating}
            />
          )}
          <ToolbarButton label={t("workspace:deleteSelected")} icon={TrashIcon} onClick={deleteSelected} disabled={selectedSegIds.size === 0} />
          {/* The flow player carries the original/revised toggle, but it only renders when there is audio to
              play. Once the audio has been deleted the transcript remains, so keep the toggle here for that
              case rather than losing it - and don't show two of them when the player is present. */}
          {hasRevisions(rec.current.segments) && !rec.hasAudio && (
            <ToolbarButton label={t("workspace:toggleViewTitle")} icon={EyeIcon} active={showOriginal} onClick={() => setShowOriginal((v) => !v)} />
          )}
          {selectedSegIds.size > 0 && (
            <span className="ml-0.5 text-xs text-blue-700 dark:text-blue-300">{selectedSegIds.size}</span>
          )}
        </>
      ) : undefined,
      content: rec.current ? (
        <div className="space-y-3 pb-2">
          {/* Audio embedded in the flow: the conversation-flow track shows who spoke when and doubles as
              the scrubber, so the transcript is read and scrubbed in one place. */}
          {rec.hasAudio && (
            <ConversationFlowPlayer
              segments={rec.current.segments}
              durationMs={rec.durationMs}
              currentMs={audioCur * 1000}
              playing={!audioPaused}
              speakerNameOf={speakerNameOf}
              showOriginal={showOriginal}
              canToggleOriginal={hasRevisions(rec.current.segments)}
              onToggle={togglePlayPause}
              onSeek={(ms) => playFrom(ms)}
              onToggleOriginal={() => setShowOriginal((v) => !v)}
            />
          )}
          {matchTimes.length > 1 && (
            <div className="sticky top-0 z-10 mb-2 flex items-center gap-2 rounded-md border bg-blue-50 px-3 py-1.5 text-xs text-blue-900 dark:border-blue-800 dark:bg-blue-950/40 dark:text-blue-200">
              <span className="font-medium">
                {t("workspace:matchNav", { k: Math.max(1, matchIdx + 1), n: matchTimes.length })}
              </span>
              <div className="ml-auto flex gap-1">
                <button
                  type="button"
                  onClick={() => goToMatch(matchIdx <= 0 ? matchTimes.length - 1 : matchIdx - 1)}
                  aria-label={t("workspace:prevMatch")}
                  className="rounded border px-2 py-0.5 hover:bg-blue-100 dark:border-blue-800 dark:hover:bg-blue-900"
                >
                  ◀
                </button>
                <button
                  type="button"
                  onClick={() => goToMatch(matchIdx < 0 || matchIdx >= matchTimes.length - 1 ? 0 : matchIdx + 1)}
                  aria-label={t("workspace:nextMatch")}
                  className="rounded border px-2 py-0.5 hover:bg-blue-100 dark:border-blue-800 dark:hover:bg-blue-900"
                >
                  ▶
                </button>
              </div>
            </div>
          )}
          <ul className="space-y-2">
            {weaveTranscript(rec.current.segments, notes, shots).map((row) =>
              row.kind === "note" ? (
                <NoteRow key={`note-${row.note.id}`} note={row.note} speaker={fullName ?? email ?? t("workspace:noteSpeakerYou")} />
              ) : row.kind === "screenshot" ? (
                <li key={`shot-${row.shot.id}`} className="flex items-start gap-2">
                  {/* Same leading-timestamp treatment as NoteRow's - a plain (non-interactive) stamp, since
                      NoteRow's own timestamp isn't a click-to-jump control either. Uses the same time format
                      as the Notes-tab strip and the modal (formatDuration), not this page's own `fmt`, so the
                      same capture reads identically everywhere it appears. */}
                  <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">
                    {formatDuration(row.shot.capturedAtMs)}
                  </span>
                  <button
                    type="button"
                    onClick={() => setOpenShot(shots.findIndex((s) => s.id === row.shot.id))}
                    className="overflow-hidden rounded border hover:ring-2 hover:ring-blue-400 dark:border-gray-700"
                    aria-label={t("workspace:screenshotAlt", { time: formatDuration(row.shot.capturedAtMs) })}
                  >
                    <img
                      src={api.screenshotThumbUrl(id, row.shot.id)}
                      alt={t("workspace:screenshotAlt", { time: formatDuration(row.shot.capturedAtMs) })}
                      loading="lazy"
                      className="h-24 w-auto"
                    />
                  </button>
                </li>
              ) : (
                <SegmentRow
                  key={row.seg.id}
                  seg={row.seg}
                  speakerName={multiSpeakerLabels.has(row.seg.speaker) ? t("workspace:multipleSpeakers") : row.seg.speakerDisplay}
                  assign={segmentAssign}
                  active={row.index === activeIdx}
                  selected={selectedSegIds.has(row.seg.id)}
                  selectMode={selectMode}
                  showOriginal={showOriginal}
                  onClick={() => clickSegment(row.seg.id)}
                />
              ),
            )}
          </ul>
        </div>
      ) : (
        <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("workspace:noTranscriptYet")}</p>
      ),
    },
    {
      key: "files",
      label: t("workspace:detailTabFiles"),
      icon: <FilesGlyph size={15} />,
      content: <AttachmentsManager recordingId={id} attachments={attachments} onChange={refreshAttachments} />,
    },
    {
      key: "formulas",
      label: t("workspace:detailTabFormulas"),
      icon: <FormulasGlyph />,
      toolbar: (
        <FormulasToolbar
          selectedId={selectedFormulaResultId}
          canManageSelected={canManageSelectedFormulaResult}
          onRun={() => setFormulaRunOpen(true)}
          onOpen={openFormulaResult}
          onDownload={downloadFormulaResult}
          onEmail={emailFormulaResult}
          onDelete={deleteFormulaResult}
        />
      ),
      content: (
        <FormulasPanel
          loadText={(resultId) => api.getFormulaResultText(id, resultId)}
          sourceKey={["formula-result-text", id]}
          results={formulaResults}
          selectedId={selectedFormulaResultId}
          onSelect={setSelectedFormulaResultId}
        />
      ),
    },
  ];

  return (
    <div
      className="relative space-y-2.5"
      onDragOver={(e) => {
        // Only react to file drags (ignore in-app text/section drags).
        if (Array.from(e.dataTransfer.types).includes("Files")) {
          e.preventDefault();
          setDragging(true);
        }
      }}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target) setDragging(false);
      }}
      onDrop={onDropFiles}
    >
      {dragging && (
        <div className="pointer-events-none absolute inset-0 z-40 flex items-center justify-center rounded-lg border-2 border-dashed border-blue-400 bg-blue-50/80 text-sm font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-200">
          {t("workspace:dropToAttach")}
        </div>
      )}
      {/* Pipeline progress (summarising / extracting / translating / re-identifying / merging / etc.) is shown
          only in the global status bar - see the useStatus() effects above - so it isn't duplicated here. */}
      {renaming ? (
        <RecordingNameForm initial={rec.name ?? ""} onSave={saveRecordingName} onCancel={() => setRenaming(false)} />
      ) : (
        <DetailHeader
          title={rec.name ?? rec.title}
          menu={menuActions}
          hasAudio={rec.hasAudio}
          hasTranscript={hasTranscript}
          onPlay={() => playFrom(0)}
          onCopyLink={copyLink}
          onDownload={() => setDownloading(true)}
        />
      )}
      <p className="-mt-1 text-xs text-gray-500 dark:text-gray-400">
        {rec.source === "System" ? t("workspace:sourceSystem") : rec.source === "Upload" ? t("workspace:sourceUpload") : t("workspace:sourceMicrophone")} ·{" "}
        {formatDate(rec.createdAt, i18n.language)}
        {rec.durationMs > 0 ? ` · ${formatDuration(rec.durationMs)}` : ""} · {rec.status}
        {rec.sizeBytes > 0 ? ` · ${formatBytes(rec.sizeBytes)}` : ""}
        {rec.current?.processingMs ? ` · ${t("workspace:processedIn", { time: formatDuration(rec.current.processingMs) })}` : ""}
      </p>

      {actionError && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{actionError}</p>
      )}

      {actionInfo && (
        <p className="rounded bg-green-50 p-3 text-sm text-green-700 dark:bg-green-900/30 dark:text-green-300">{actionInfo}</p>
      )}

      {rec.status === "Failed" && rec.error && (
        <p className="rounded bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{rec.error}</p>
      )}

      {/* Hidden audio element — the header's Play button, the transcript's flow player, and the
          per-speaker/segment Play buttons all drive it (no native controls). It lives here, outside
          <DetailSections>, so it stays mounted whichever section is showing: the router renders only the
          active section, so keeping it inside the Transcript meant audioRef was null on the Speakers
          section and Play silently no-op'd. */}
      {rec.hasAudio && (
        <audio
          ref={audioRef}
          onTimeUpdate={onTimeUpdate}
          onPlay={() => setAudioPaused(false)}
          onPause={() => {
            setAudioPaused(true);
            setSelectionPlaying(false); // whatever paused it, the toolbar's Pause has nothing left to stop
          }}
          className="hidden"
        />
      )}

      <DetailSections sections={detailTabs} active={tab} onSelect={selectTab} hub={hubView} />

      {editingSeg && (
        <SegmentEditModal
          seg={editingSeg}
          onClose={() => setEditingSeg(null)}
          onSave={async (text) => {
            await api.updateSegment(id, editingSeg.id, text);
            setEditingSeg(null);
            qc.invalidateQueries({ queryKey: ["recording", id] });
          }}
        />
      )}

      {editingSummary && (
        <SummaryEditModal
          initial={rec.summary?.text ?? ""}
          onClose={() => setEditingSummary(false)}
          onSave={saveSummary}
        />
      )}

      {managingTypes && <ManageMeetingTypesModal onClose={() => setManagingTypes(false)} />}

      {editingMinutes && (
        <MeetingMinutesEditModal
          initial={rec.meetingMinutes?.text ?? ""}
          onClose={() => setEditingMinutes(false)}
          onSave={saveMinutes}
        />
      )}

      {emailMinutesOpen && (
        <EmailMinutesModal
          count={attachments.length}
          onCancel={() => setEmailMinutesOpen(false)}
          onChoose={sendMinutesEmail}
        />
      )}

      {moving && (
        <MoveToSectionModal
          recordingId={id}
          roomId={currentRoom && !currentRoom.isPersonal ? currentRoom.id : undefined}
          onClose={() => setMoving(false)}
        />
      )}
      {sharing && homeRoom && (
        <ShareToRoomModal
          recordingId={id}
          recordingName={rec.name ?? rec.title}
          fromRoomId={homeRoom.id}
          alreadyInRoomIds={inRoomIds}
          onClose={() => setSharing(false)}
        />
      )}
      {downloading && <DownloadTranscriptModal recordingId={id} onClose={() => setDownloading(false)} />}
      {peopleOpen && <PreferencesModal initialTab="voiceprints" onClose={() => setPeopleOpen(false)} />}
      {linkModalOpen && (
        <CalendarLinkModal recordingId={id} aroundDate={rec.createdAt} onClose={() => setLinkModalOpen(false)} />
      )}

      {openShot !== null && (
        <ScreenshotModal
          recordingId={id}
          shots={shots}
          index={openShot}
          onIndexChange={setOpenShot}
          onClose={() => setOpenShot(null)}
          onJump={jumpToMs}
          onDelete={isOwner ? removeShot : undefined}
        />
      )}

      {formulaRunOpen && (
        <FormulaRunModal
          target={{ kind: "recording", recordingId: id }}
          onClose={() => setFormulaRunOpen(false)}
          onRun={(result) => {
            refreshFormulas();
            setSelectedFormulaResultId(result.id);
          }}
          onError={(msg) => setActionError(msg)}
          onManageFormulas={() => {
            setFormulaRunOpen(false);
            setManagingFormulas(true);
          }}
          onFindShared={() => {
            setFormulaRunOpen(false);
            setSharedBrowserOpen(true);
          }}
        />
      )}
      {sharedBrowserOpen && <SharedFormulasBrowser onClose={() => setSharedBrowserOpen(false)} />}
      {editingFormulaResult && (
        <FormulaResultEditModal
          name={editingFormulaResult.name}
          load={() => api.getFormulaResultText(id, editingFormulaResult.id)}
          save={(md) => api.updateFormulaResult(id, editingFormulaResult.id, md).then(() => undefined)}
          onSaved={refreshFormulas}
          onClose={() => setEditingFormulaResult(null)}
          editable={canManageFormulaResult(editingFormulaResult)}
        />
      )}
      {managingFormulas && (
        <PreferencesModal initialTab="formulas" onClose={() => setManagingFormulas(false)} />
      )}

      {retranscribeOpen && (
        <RetranscribeModal
          initialMin={rec.minSpeakers}
          initialMax={rec.maxSpeakers}
          hasRevisions={!!rec.current && hasRevisions(rec.current.segments)}
          busy={requeuing}
          onCancel={() => setRetranscribeOpen(false)}
          onConfirm={retranscribe}
        />
      )}
    </div>
  );
}

/// Asked for when the user re-transcribes: optional diarization speaker-count hints (the exception, not
/// the norm — used mainly to split two people the diarizer merged into one).
function RetranscribeModal({
  initialMin,
  initialMax,
  hasRevisions,
  busy,
  onCancel,
  onConfirm,
}: {
  initialMin: number | null;
  initialMax: number | null;
  hasRevisions: boolean;
  busy: boolean;
  onCancel: () => void;
  onConfirm: (min: number | null, max: number | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const [min, setMin] = useState(initialMin != null ? String(initialMin) : "");
  const [max, setMax] = useState(initialMax != null ? String(initialMax) : "");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onCancel();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel]);

  function confirm() {
    onConfirm(min.trim() ? Number(min) : null, max.trim() ? Number(max) : null);
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div
        role="dialog"
        aria-label={t("retranscribeTitle")}
        className="w-full max-w-md rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">{t("retranscribeTitle")}</h2>
        <p className="mb-4 text-xs text-gray-500 dark:text-gray-400">{t("retranscribeHelp")}</p>
        {hasRevisions && (
          <p className="mb-4 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-700/60 dark:bg-amber-900/30 dark:text-amber-300">
            {t("retranscribeRevisionsWarning")}
          </p>
        )}
        <div className="flex items-center gap-4 text-sm">
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            {t("minSpeakers")}
            <input
              type="number"
              min={1}
              value={min}
              onChange={(e) => setMin(e.target.value)}
              aria-label={t("minSpeakersAria")}
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            {t("maxSpeakers")}
            <input
              type="number"
              min={1}
              value={max}
              onChange={(e) => setMax(e.target.value)}
              aria-label={t("maxSpeakersAria")}
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
        </div>
        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={confirm}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("starting") : t("retranscribeTitle")}
          </button>
        </div>
      </div>
    </div>
  );
}

function SegmentEditModal({
  seg,
  onClose,
  onSave,
}: {
  seg: SegmentDto;
  onClose: () => void;
  onSave: (text: string | null) => Promise<void>;
}) {
  const { t } = useTranslation("workspace");
  const [text, setText] = useState(seg.text);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const taRef = useRef<HTMLTextAreaElement>(null);
  const revised = seg.revised != null;

  // Grow the textarea to fit its content; CSS max-height keeps the modal on-screen (it scrolls past that).
  function autosize() {
    const el = taRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }
  // Size to the initial text once mounted.
  useEffect(() => autosize(), []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function save(value: string | null) {
    setBusy(true);
    setError(null);
    try {
      await onSave(value);
    } catch (e) {
      setError(apiErrorMessage(e, t("errSaveSegment")));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("editSegment")}
        className="w-full max-w-3xl rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("editSegment")}</h2>
        <textarea
          ref={taRef}
          autoFocus
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            autosize();
          }}
          aria-label={t("segmentTextAria")}
          className="block max-h-[60vh] min-h-[8rem] w-full resize-none overflow-y-auto rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        {revised && (
          <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">
            <span className="font-medium">{t("originalLabel")}</span> {seg.original}
          </p>
        )}
        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
        <div className="mt-3 flex items-center justify-end gap-2">
          {/* Clearing a revision restores the model's original words (sends null to the server). */}
          {revised && (
            <button
              type="button"
              onClick={() => save(null)}
              disabled={busy}
              className="mr-auto rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("resetToOriginal")}
            </button>
          )}
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={() => save(text)}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("common:saving") : t("common:save")}
          </button>
        </div>
      </div>
    </div>
  );
}

function RecordingNameForm({
  initial,
  onSave,
  onCancel,
}: {
  initial: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState(initial);
  return (
    <form
      className="flex items-center gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        onSave(value);
      }}
    >
      <input
        autoFocus
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onCancel()}
        placeholder={t("recordingNamePlaceholder")}
        aria-label={t("recordingNamePlaceholder")}
        className="w-64 rounded border px-2 py-1 text-base dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800">
        {t("common:save")}
      </button>
    </form>
  );
}

export function SpeakerRow({
  label,
  info,
  initial,
  count,
  durationMs,
  profiles,
  canPlay,
  playing,
  selected,
  onSelect,
  onTogglePlay,
  onDelete,
  onAssign,
  onCreate,
  onMulti,
}: {
  label: string;
  info: SpeakerInfo | undefined;
  initial: string;
  count: number;
  durationMs: number;
  profiles: SpeakerProfile[];
  canPlay: boolean;
  playing: boolean;
  selected: boolean;
  onSelect: () => void;
  onTogglePlay: () => void;
  onDelete: (name: string) => void;
  onAssign: (profileId: string | null) => void;
  onCreate: (name: string) => void;
  onMulti: () => void;
}) {
  const { t } = useTranslation("workspace");
  // The display name for the per-speaker action labels (the assignment typeahead owns the editing UI).
  const name = initial;
  // The interactive controls (assign box, play, delete) sit inside the clickable row, so they stop event
  // propagation to avoid toggling the row's selection when used.
  const stop = (e: { stopPropagation: () => void }) => e.stopPropagation();

  // One line per speaker: [label · auto] · assign typeahead · segment count · toolbar (play/delete). The whole
  // row is a button that toggles the speaker's segment table below the list; the leading label column is
  // fixed-width so the assign box and the items after it line up across rows.
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      aria-label={t("selectSpeakerAria", { name })}
      onClick={onSelect}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          onSelect();
        }
      }}
      className={`flex cursor-pointer flex-wrap items-center gap-2 rounded-lg border px-2 py-1 hover:bg-gray-50 dark:hover:bg-gray-800 ${
        selected
          ? "border-blue-400 bg-blue-50 ring-1 ring-blue-300 dark:border-blue-600 dark:bg-blue-900/30 dark:ring-blue-700"
          : "border-transparent"
      }`}
    >
      <div className="flex w-32 shrink-0 items-center gap-1">
        <span className="text-xs text-gray-400 dark:text-gray-500">{label}</span>
        {info?.identifiedAuto && (
          <span
            title={t("autoNameTitle")}
            className="rounded bg-blue-100 px-1 text-[10px] font-medium text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
          >
            {t("autoBadge")}
          </span>
        )}
      </div>
      <div onClick={stop} onKeyDown={stop}>
        <SpeakerAssign
          label={label}
          profiles={profiles}
          profileId={info?.profileId ?? null}
          isMulti={info?.isMultiSpeaker ?? false}
          onAssign={onAssign}
          onCreate={onCreate}
          onMulti={onMulti}
        />
      </div>
      <span className="w-40 shrink-0 whitespace-nowrap text-xs text-gray-500 dark:text-gray-400">
        {t("speakerSegmentCount", { count })} · {formatDuration(durationMs)}
      </span>
      <div className="flex items-center gap-0.5" onClick={stop} onKeyDown={stop}>
        {canPlay && (
          <ToolbarButton
            label={playing ? t("pauseSpeaker") : t("playSpeaker", { label: name })}
            icon={playing ? PauseIcon : PlayIcon}
            active={playing}
            onClick={onTogglePlay}
          />
        )}
        <ToolbarButton label={t("deleteSpeaker", { label: name })} icon={TrashIcon} onClick={() => onDelete(name)} />
      </div>
    </div>
  );
}

/// A note-taker's note woven into the transcript: same row layout as a segment (timestamp · speaker · text)
/// but the current user is the "speaker" and the text is green, to distinguish it from transcribed speech.
function NoteRow({ note, speaker }: { note: MeetingNote; speaker: string }) {
  return (
    <li className="flex items-start gap-3 rounded-lg border border-green-200 bg-green-50/40 px-4 py-2 dark:border-green-900 dark:bg-green-950/20">
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">
        {note.capturedAtMs != null ? fmt(note.capturedAtMs) : ""}
      </span>
      <span className="w-28 shrink-0 text-sm font-medium text-green-700 dark:text-green-400">{speaker}</span>
      <span className="flex-1 whitespace-pre-wrap break-words text-sm text-green-700 dark:text-green-400">{note.text}</span>
    </li>
  );
}

/// What a transcript row needs to offer the Speakers-tab assignment typeahead in its speaker column, so a
/// speaker can be named while reading/playing the transcript. Omitted (Speakers tab, where the speaker row
/// above already carries the typeahead) → the row shows a plain label as before.
export type SegmentAssign = {
  profiles: SpeakerProfile[];
  infoOf: (label: string) => SpeakerInfo | undefined;
  onAssign: (label: string, profileId: string | null) => void | Promise<void>;
  onCreate: (label: string, name: string) => void | Promise<void>;
  onMulti: (label: string) => void | Promise<void>;
};

function SegmentRow({
  seg,
  speakerName,
  assign,
  active,
  selected,
  selectMode,
  showOriginal,
  onClick,
}: {
  seg: SegmentDto;
  /// The speaker name to show (localised "Multiple Speakers" overrides the server display).
  speakerName: string;
  assign?: SegmentAssign;
  /// Currently playing (highlighted by the audio position).
  active: boolean;
  /// Picked in the transcript selection (drives the toolbar's bulk actions).
  selected: boolean;
  selectMode: boolean;
  showOriginal: boolean;
  /// Click anywhere on the row: select it (single, or toggle in Select mode). No longer auto-plays.
  onClick: () => void;
}) {
  const { t } = useTranslation("workspace");
  const revised = seg.revised != null;
  return (
    <li
      id={`seg-${seg.id}`}
      onClick={onClick}
      className={`flex cursor-pointer items-start gap-3 rounded-lg border px-4 py-2 hover:bg-gray-50 dark:hover:bg-gray-800 ${
        selected
          ? "border-blue-400 bg-blue-50 ring-1 ring-blue-300 dark:border-blue-600 dark:bg-blue-900/30 dark:ring-blue-700"
          : active
            ? "border-blue-300 bg-blue-50 dark:border-blue-700 dark:bg-blue-900/30"
            : "border-gray-200 bg-white dark:border-gray-700 dark:bg-gray-900"
      }`}
    >
      {selectMode && (
        // Visual only — the whole row is the click target, which toggles selection.
        <input type="checkbox" checked={selected} readOnly tabIndex={-1} aria-hidden className="mt-1 shrink-0 pointer-events-none" />
      )}
      <span className="w-12 shrink-0 font-mono text-xs text-gray-400 dark:text-gray-500">{fmt(seg.startMs)}</span>
      {assign ? (
        // The typeahead is a click target inside a clickable row, so it stops propagation (which would
        // otherwise select the segment). Closed, it renders as one button - cheap enough per row.
        <div className="shrink-0" onClick={(e) => e.stopPropagation()}>
          <SpeakerAssign
            label={seg.speaker}
            width="w-40"
            subtle
            profiles={assign.profiles}
            displayName={speakerName}
            profileId={assign.infoOf(seg.speaker)?.profileId ?? null}
            isMulti={assign.infoOf(seg.speaker)?.isMultiSpeaker ?? false}
            onAssign={(profileId) => assign.onAssign(seg.speaker, profileId)}
            onCreate={(name) => assign.onCreate(seg.speaker, name)}
            onMulti={() => assign.onMulti(seg.speaker)}
          />
        </div>
      ) : (
        <span className="w-28 shrink-0 text-sm font-medium text-gray-700 dark:text-gray-200">{speakerName}</span>
      )}
      {/* Auto-expands vertically to show the full (possibly merged) block of text. */}
      <span className="flex-1 whitespace-pre-wrap break-words text-sm dark:text-gray-200">
        {segmentText(seg, showOriginal)}
      </span>
      {/* The currently-playing row gets a small ▶ marker (distinct from the selection highlight). */}
      {active && <span aria-hidden className="mt-0.5 shrink-0 text-blue-500 dark:text-blue-400">▶</span>}
      {/* Marks a segment whose text has been edited or translated (a revision exists). */}
      {revised && (
        <span
          title={t("revisedTitle")}
          aria-label={t("editedAria")}
          className="mt-0.5 shrink-0 text-teal-500 dark:text-teal-400"
        >
          ✎
        </span>
      )}
    </li>
  );
}
