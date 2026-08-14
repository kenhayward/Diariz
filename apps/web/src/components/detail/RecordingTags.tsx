import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { useStatus } from "../../lib/status";
import TagsPill from "./TagsPill";
import TagsPopover from "./TagsPopover";

/// One in-flight edit, applied on top of the server's own `tags`/`suggested` to render the pill/popover
/// before the request settles. Tracked individually - rather than as one combined snapshot - because the
/// popover is built for rapid successive edits (space commits a word and keeps focus, so several can be in
/// flight at once): one edit failing must retract only itself, never a sibling that is still valid. `type` +
/// `tag` identifies an op uniquely: the popover never lets the same tag be in flight twice for the same
/// mutation type (a duplicate add/remove/dismiss of a tag already reflected in `shownTags`/`shownSuggested`
/// is rejected before it reaches here - see `lib/tagInput.ts`'s `addTag`).
interface PendingOp {
  type: "add" | "remove" | "dismiss";
  tag: string;
}

/// The hero card's tagging control: the pill, its popover, and the server round-trip behind them. Owns the
/// mutations itself rather than taking callbacks down through HeroSummaryCard and RecordingDetail - nothing
/// outside this control needs to know about tagging, and both of those already carry long prop lists.
///
/// Every action persists on its own (no Save button). Local `ops` state applies each change immediately so
/// the pill count and the chips react to a click without waiting for the refetch; the authoritative lists
/// arrive back as props when the recording detail query settles.
export default function RecordingTags({
  recordingId,
  tags,
  suggested,
}: {
  recordingId: string;
  tags: string[];
  suggested: string[];
}) {
  const { t } = useTranslation(["workspace"]);
  const qc = useQueryClient();
  const { setStatus } = useStatus();
  const [open, setOpen] = useState(false);

  /// One entry per edit still in flight (or not yet reflected by a refetch). Rendered by replaying them, in
  /// order, on top of the server's own lists - see `applyOps`.
  const [ops, setOps] = useState<PendingOp[]>([]);

  /// The overlay clears once the parent hands back genuinely new `tags`/`suggested` content (i.e. the
  /// detail query actually refetched) - not merely once a mutation's promise settles, and not merely on a
  /// new array reference. A mutation can settle (success) well before the invalidated query refetches, and
  /// clearing on settle alone would flash an added chip back out. Comparing by content rather than
  /// reference matters too: `HeroSummaryCard` passes `rec.tags ?? []`, and that `?? []` mints a fresh array
  /// on every one of its re-renders whenever a recording has no tags yet - a reference-based dependency
  /// would clear the overlay on any unrelated re-render.
  ///
  /// This only handles the success path. A rejected mutation never changes the server's lists (nothing was
  /// applied), so this effect alone would never fire for a failure - each op instead retracts itself
  /// directly on error, in the mutations' `onError` below.
  const tagsKey = tags.join("|");
  const suggestedKey = suggested.join("|");
  useEffect(() => {
    setOps([]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tagsKey, suggestedKey]);

  const lower = (s: string) => s.toLowerCase();

  /// Replays the in-flight ops, in the order they were made, on top of the server's own lists.
  function applyOps(base: { tags: string[]; suggested: string[] }): { tags: string[]; suggested: string[] } {
    let liveTags = base.tags;
    let liveSuggested = base.suggested;
    for (const op of ops) {
      if (op.type === "add") {
        if (!liveTags.some((x) => lower(x) === lower(op.tag))) liveTags = [...liveTags, op.tag];
        liveSuggested = liveSuggested.filter((x) => lower(x) !== lower(op.tag));
      } else if (op.type === "remove") {
        liveTags = liveTags.filter((x) => lower(x) !== lower(op.tag));
      } else {
        liveSuggested = liveSuggested.filter((x) => lower(x) !== lower(op.tag));
      }
    }
    return { tags: liveTags, suggested: liveSuggested };
  }
  const { tags: shownTags, suggested: shownSuggested } = applyOps({ tags, suggested });

  /// Both queries: the recording detail feeds this control's props, and the ["tags"] prefix covers every
  /// room variant of the tag cloud the Tags tab renders.
  async function refresh() {
    await Promise.all([
      qc.invalidateQueries({ queryKey: ["recording", recordingId] }),
      qc.invalidateQueries({ queryKey: ["tags"] }),
    ]);
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
  useEffect(() => () => {
    if (pushedErrorRef.current) setStatus(null);
  }, [setStatus]);

  /// A later edit going through means whatever the last failure was about is over - the message stops
  /// applying, so retract it (subject to the "only what we pushed" guard in the effect above).
  function onOpSuccess() {
    setError(null);
  }

  /// Retracts exactly the op that failed, not the whole overlay - a sibling op still in flight or already
  /// applied must stay visible - and reports the failure.
  ///
  /// Deliberately wired as the *hook-level* `onError`/`onSuccess` (`useMutation({ onError, onSuccess })`),
  /// not as per-call options passed to `.mutate(tag, { onError })`. `MutationObserver.mutate()` stores
  /// per-call options on the shared observer and detaches it from whatever mutation was previously in
  /// flight, so an earlier overlapping call's per-call `onError` would silently never fire once a later
  /// `.mutate()` on the same hook superseded it. Hook-level options are captured per-`Mutation` instance at
  /// build time and always run from inside that mutation's own `execute()`, independent of the observer -
  /// exactly what "several edits genuinely in flight at once" needs.
  function onOpError(type: PendingOp["type"], tag: string, e: unknown) {
    setOps((cur) => cur.filter((o) => !(o.type === type && lower(o.tag) === lower(tag))));
    setError(apiErrorMessage(e, t("workspace:errSaveTag")));
  }

  const add = useMutation({
    mutationFn: (tag: string) => api.addRecordingTag(recordingId, tag),
    onSettled: refresh,
    onSuccess: onOpSuccess,
    onError: (e, tag) => onOpError("add", tag, e),
  });
  const remove = useMutation({
    mutationFn: (tag: string) => api.removeRecordingTag(recordingId, tag),
    onSettled: refresh,
    onSuccess: onOpSuccess,
    onError: (e, tag) => onOpError("remove", tag, e),
  });
  const dismiss = useMutation({
    mutationFn: (tag: string) => api.dismissRecordingTag(recordingId, tag),
    onSettled: refresh,
    onSuccess: onOpSuccess,
    onError: (e, tag) => onOpError("dismiss", tag, e),
  });

  function onAdd(tag: string) {
    // A promoted suggestion leaves the hint list in the same beat it becomes a chip - see `applyOps`.
    setOps((cur) => [...cur, { type: "add", tag }]);
    add.mutate(tag);
  }

  function onRemove(tag: string) {
    setOps((cur) => [...cur, { type: "remove", tag }]);
    remove.mutate(tag);
  }

  function onDismiss(tag: string) {
    setOps((cur) => [...cur, { type: "dismiss", tag }]);
    dismiss.mutate(tag);
  }

  return (
    <div className="relative">
      <TagsPill
        count={shownTags.length}
        tags={shownTags}
        open={open}
        onToggle={() => setOpen((o) => !o)}
      />
      <TagsPopover
        open={open}
        onClose={() => setOpen(false)}
        tags={shownTags}
        suggested={shownSuggested}
        onAdd={onAdd}
        onRemove={onRemove}
        onDismiss={onDismiss}
      />
    </div>
  );
}
