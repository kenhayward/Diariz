import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { useStatus } from "../../lib/status";
import TagsPill from "./TagsPill";
import TagsPopover from "./TagsPopover";

/// The hero card's tagging control: the pill, its popover, and the server round-trip behind them. Owns the
/// mutations itself rather than taking callbacks down through HeroSummaryCard and RecordingDetail - nothing
/// outside this control needs to know about tagging, and both of those already carry long prop lists.
///
/// Every action persists on its own (no Save button): each fires its mutation, invalidates the queries that
/// feed the affected views, and the chips re-render from whatever the server then reports. There is
/// deliberately no local optimistic copy of the tag lists - `tags`/`suggested` are the only source of what
/// is shown, exactly as every other mutation in this app works (speaker renames, applying a meeting type,
/// completing an action item all simply fire, invalidate, and re-render from the server). An overlay of
/// locally-replayed edits cannot distinguish "the server confirmed my edit" from "the server happens to
/// agree for another reason", so it either strands a phantom chip or drops a real one; the round-trip is
/// short enough that showing the server's answer is the honest thing to do.
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

  function onEditError(e: unknown) {
    setError(apiErrorMessage(e, t("workspace:errSaveTag")));
  }

  const add = useMutation({
    mutationFn: (tag: string) => api.addRecordingTag(recordingId, tag),
    onSettled: refresh,
    onSuccess: onEditSuccess,
    onError: onEditError,
  });
  const remove = useMutation({
    mutationFn: (tag: string) => api.removeRecordingTag(recordingId, tag),
    onSettled: refresh,
    onSuccess: onEditSuccess,
    onError: onEditError,
  });
  const dismiss = useMutation({
    mutationFn: (tag: string) => api.dismissRecordingTag(recordingId, tag),
    onSettled: refresh,
    onSuccess: onEditSuccess,
    onError: onEditError,
  });

  return (
    <div className="relative">
      <TagsPill count={tags.length} tags={tags} open={open} onToggle={() => setOpen((o) => !o)} />
      <TagsPopover
        open={open}
        onClose={() => setOpen(false)}
        tags={tags}
        suggested={suggested}
        onAdd={(tag) => add.mutate(tag)}
        onRemove={(tag) => remove.mutate(tag)}
        onDismiss={(tag) => dismiss.mutate(tag)}
      />
    </div>
  );
}
