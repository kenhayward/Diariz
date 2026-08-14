import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { useStatus } from "../../lib/status";
import { normalizeTag } from "../../lib/tagInput";
import type { RecordingDetail } from "../../lib/types";
import TagsPill from "./TagsPill";
import TagsPopover from "./TagsPopover";

/// One tag edit, as the cache patch and its undo need to see it. `tag` is the normalised text the server
/// will store (`normalizeTag` mirrors `TagText.Normalize`, so the optimistic chip is spelled the way the
/// refetch will spell it); `raw` is what the control handed us. The two differ when a suggestion carries
/// whitespace - the chip must read "Q3-planning" while the hint to strike off the list is still
/// "Q3 planning".
interface TagEdit {
  type: "add" | "remove" | "dismiss";
  tag: string;
  raw: string;
}

/// Everything the rollback needs, handed from `onMutate` to `onError` as the mutation's context. `snapshot`
/// is undefined when nothing was cached, in which case there was no patch to undo either.
interface EditContext {
  edit: TagEdit;
  snapshot: RecordingDetail | undefined;
}

/// Tags are compared case-insensitively everywhere (see `lib/tagInput.ts`).
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/// The edit, applied to the cached recording. Adopting a tag also strikes it off the hint list, so promoting
/// a suggestion moves it in one beat rather than showing it twice.
function applyEdit(detail: RecordingDetail, edit: TagEdit): RecordingDetail {
  const tags = detail.tags ?? [];
  const suggested = detail.suggestedTags ?? [];
  const isHint = (s: string) => same(s, edit.tag) || same(s, edit.raw);

  if (edit.type === "add")
    return {
      ...detail,
      tags: tags.some((x) => same(x, edit.tag)) ? tags : [...tags, edit.tag],
      suggestedTags: suggested.filter((s) => !isHint(s)),
    };
  if (edit.type === "remove") return { ...detail, tags: tags.filter((x) => !same(x, edit.tag)) };
  return { ...detail, suggestedTags: suggested.filter((s) => !isHint(s)) };
}

/// Puts an entry the edit struck out back where the snapshot had it - but only if the snapshot had it (else
/// the edit never removed it) and the list has since lost it (else something newer owns it now).
function restored(current: string[], snapshot: string[], matches: (s: string) => boolean): string[] {
  const at = snapshot.findIndex(matches);
  if (at < 0 || current.some(matches)) return current;
  const next = [...current];
  next.splice(Math.min(at, next.length), 0, snapshot[at]);
  return next;
}

/// Takes an entry the edit put in back out - unless the snapshot already had it, in which case the edit
/// added nothing and there is nothing of ours to take away.
function unadded(current: string[], snapshot: string[], matches: (s: string) => boolean): string[] {
  if (snapshot.some(matches)) return current;
  return current.filter((x) => !matches(x));
}

/// Undoes one edit's patch on top of whatever the cache holds now, using the snapshot only to recover what
/// the patch itself destroyed (whether the tag was there, and where the hint sat). This is the deliberate
/// deviation from React Query's literal `setQueryData(key, context.snapshot)` rollback: alpha's snapshot
/// predates beta's patch, so restoring it wholesale would take beta's perfectly good chip down with alpha's
/// - and would also throw away any server data that landed in between. Undoing only this edit leaves both
/// alone. It never asks "did my edit land", which is the question no amount of server content can answer.
function undoEdit(current: RecordingDetail, snapshot: RecordingDetail, edit: TagEdit): RecordingDetail {
  const cTags = current.tags ?? [];
  const sTags = snapshot.tags ?? [];
  const cHints = current.suggestedTags ?? [];
  const sHints = snapshot.suggestedTags ?? [];
  const isTag = (s: string) => same(s, edit.tag);
  const isHint = (s: string) => same(s, edit.tag) || same(s, edit.raw);

  if (edit.type === "add")
    return {
      ...current,
      tags: unadded(cTags, sTags, isTag),
      suggestedTags: restored(cHints, sHints, isHint),
    };
  if (edit.type === "remove") return { ...current, tags: restored(cTags, sTags, isTag) };
  return { ...current, suggestedTags: restored(cHints, sHints, isHint) };
}

/// The hero card's tagging control: the pill, its popover, and the server round-trip behind them. Owns the
/// mutations itself rather than taking callbacks down through HeroSummaryCard and RecordingDetail - nothing
/// outside this control needs to know about tagging, and both of those already carry long prop lists.
///
/// Every action persists on its own (no Save button), and each one is optimistic in React Query's own
/// documented sense: `onMutate` cancels any refetch that could land mid-edit, snapshots the cached
/// recording, and patches it; `onError` undoes that patch from the snapshot; `onSettled` invalidates. The
/// chips render from `tags`/`suggested`, which are the very cache entry being patched (the detail query
/// feeds them down through HeroSummaryCard), so the patch is what puts the chip on screen. That matters
/// because the save takes ~11 ms while the detail refetch is orders of magnitude slower on a long recording
/// (it carries every segment) - the user should not wait for it to see their own typing. Nothing here ever
/// asks the server's content whether an edit landed; state only ever comes back from the snapshot the edit
/// itself took.
export default function RecordingTags({
  recordingId,
  tags,
  suggested,
  canEdit,
}: {
  recordingId: string;
  tags: string[];
  suggested: string[];
  /// Whether the caller may edit this recording's tags - passed straight through to `TagsPopover`. The pill
  /// itself is unaffected either way (it keeps its count and hover text): only the popover's content changes.
  canEdit: boolean;
}) {
  const { t } = useTranslation(["workspace"]);
  const qc = useQueryClient();
  const { setStatus } = useStatus();
  const [open, setOpen] = useState(false);

  const detailKey = ["recording", recordingId];

  /// Both queries: the recording detail feeds this control's props, and the ["tags"] prefix covers every
  /// room variant of the tag cloud the Tags tab renders. The detail refetch is slow, but it now runs behind
  /// an already-correct screen, so its cost is nobody's wait.
  async function refresh() {
    await Promise.all([
      qc.invalidateQueries({ queryKey: detailKey }),
      qc.invalidateQueries({ queryKey: ["tags"] }),
    ]);
  }

  /// The optimistic half: cancel, snapshot, patch, and hand the snapshot on as the mutation's context.
  async function beginEdit(type: TagEdit["type"], raw: string): Promise<EditContext> {
    await qc.cancelQueries({ queryKey: detailKey });
    const edit: TagEdit = { type, tag: normalizeTag(raw) ?? raw, raw };
    const snapshot = qc.getQueryData<RecordingDetail>(detailKey);
    qc.setQueryData<RecordingDetail>(detailKey, (cur) => (cur ? applyEdit(cur, edit) : cur));
    return { edit, snapshot };
  }

  /// Error reporting: the shared status bar, the same way `Recorder.tsx` and `lib/calendarSync.ts` report a
  /// mutation failure that has no dedicated error UI of its own. `error` tone is sticky (`lib/status.tsx`),
  /// so it never auto-clears - this component must retract its own message once it stops applying, exactly
  /// as `Recorder.tsx` does: "Only clear what we pushed, so we never wipe another component's message."
  const [error, setError] = useState<string | null>(null);
  const pushedErrorRef = useRef(false);
  useEffect(() => {
    if (error) {
      setStatus(error, "error");
      pushedErrorRef.current = true;
    } else if (pushedErrorRef.current) {
      setStatus(null);
      pushedErrorRef.current = false;
    }
  }, [error, setStatus]);
  // Clear a lingering failure when this control unmounts (e.g. navigating off the recording).
  useEffect(
    () => () => {
      if (pushedErrorRef.current) setStatus(null);
    },
    [setStatus],
  );

  /// A later edit going through means whatever the last failure was about is over - the message stops
  /// applying, so retract it (subject to the "only what we pushed" guard in the effect above).
  function onEditSuccess() {
    setError(null);
  }

  function onEditError(e: unknown, ctx: EditContext | undefined) {
    const snapshot = ctx?.snapshot;
    if (snapshot)
      qc.setQueryData<RecordingDetail>(detailKey, (cur) =>
        cur ? undoEdit(cur, snapshot, ctx.edit) : cur,
      );
    setError(apiErrorMessage(e, t("workspace:errSaveTag")));
  }

  const add = useMutation({
    mutationFn: (tag: string) => api.addRecordingTag(recordingId, tag),
    onMutate: (tag: string) => beginEdit("add", tag),
    onSettled: refresh,
    onSuccess: onEditSuccess,
    onError: (e, _tag, ctx) => onEditError(e, ctx),
  });
  const remove = useMutation({
    mutationFn: (tag: string) => api.removeRecordingTag(recordingId, tag),
    onMutate: (tag: string) => beginEdit("remove", tag),
    onSettled: refresh,
    onSuccess: onEditSuccess,
    onError: (e, _tag, ctx) => onEditError(e, ctx),
  });
  const dismiss = useMutation({
    mutationFn: (tag: string) => api.dismissRecordingTag(recordingId, tag),
    onMutate: (tag: string) => beginEdit("dismiss", tag),
    onSettled: refresh,
    onSuccess: onEditSuccess,
    onError: (e, _tag, ctx) => onEditError(e, ctx),
  });

  return (
    <div className="relative">
      <TagsPill count={tags.length} tags={tags} open={open} onToggle={() => setOpen((o) => !o)} />
      <TagsPopover
        open={open}
        onClose={() => setOpen(false)}
        tags={tags}
        suggested={suggested}
        canEdit={canEdit}
        onAdd={(tag) => add.mutate(tag)}
        onRemove={(tag) => remove.mutate(tag)}
        onDismiss={(tag) => dismiss.mutate(tag)}
      />
    </div>
  );
}
