import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { useStatus } from "../../lib/status";
import TagsPill from "./TagsPill";
import TagsPopover from "./TagsPopover";

/// The hero card's tagging control: the pill, its popover, and the server round-trip behind them. Owns the
/// mutations itself rather than taking callbacks down through HeroSummaryCard and RecordingDetail - nothing
/// outside this control needs to know about tagging, and both of those already carry long prop lists.
///
/// Every action persists on its own (no Save button). Local `pending` state applies the change immediately
/// so the pill count and the chips react to a click without waiting for the refetch; the authoritative
/// lists arrive back as props when the recording detail query settles.
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
  /// Optimistic overlay, cleared whenever fresh props arrive.
  const [pending, setPending] = useState<{ tags: string[]; suggested: string[] } | null>(null);

  /// The overlay clears once the parent hands back genuinely new `tags`/`suggested` content (i.e. the
  /// detail query actually refetched) - not merely once a mutation's promise settles, and not merely on a
  /// new array reference. A mutation can settle (success) well before the invalidated query refetches, and
  /// clearing on settle alone would flash an added chip back out. Comparing by content rather than
  /// reference matters too: `HeroSummaryCard` passes `rec.tags ?? []`, and that `?? []` mints a fresh array
  /// on every one of its re-renders whenever a recording has no tags yet - a reference-based dependency
  /// would clear the overlay on any unrelated re-render.
  ///
  /// This only handles the success path. A rejected mutation never changes the server's lists (nothing was
  /// applied), so this effect alone would never fire for a failure - see `onFail` below for that half.
  const tagsKey = tags.join("|");
  const suggestedKey = suggested.join("|");
  useEffect(() => {
    setPending(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tagsKey, suggestedKey]);

  const shownTags = pending?.tags ?? tags;
  const shownSuggested = pending?.suggested ?? suggested;

  /// Both queries: the recording detail feeds this control's props, and the ["tags"] prefix covers every
  /// room variant of the tag cloud the Tags tab renders.
  async function refresh() {
    await Promise.all([
      qc.invalidateQueries({ queryKey: ["recording", recordingId] }),
      qc.invalidateQueries({ queryKey: ["tags"] }),
    ]);
  }

  /// The failure half of clearing the overlay. A rejected request leaves the server's `tags`/`suggested`
  /// exactly as they were, so the content-keyed effect above never sees a change and would otherwise leave
  /// the optimistic chip stuck for the rest of the session. Clear it directly here, and surface the failure
  /// the same way other components with no dedicated error UI of their own report a failed mutation
  /// (`Recorder.tsx`, `lib/calendarSync.ts`): push it to the shared status bar via `apiErrorMessage`.
  function onFail(e: unknown, fallback: string) {
    setPending(null);
    setStatus(apiErrorMessage(e, fallback), "error");
  }

  const add = useMutation({
    mutationFn: (tag: string) => api.addRecordingTag(recordingId, tag),
    onSettled: refresh,
    onError: (e) => onFail(e, t("workspace:errSaveTag")),
  });
  const remove = useMutation({
    mutationFn: (tag: string) => api.removeRecordingTag(recordingId, tag),
    onSettled: refresh,
    onError: (e) => onFail(e, t("workspace:errSaveTag")),
  });
  const dismiss = useMutation({
    mutationFn: (tag: string) => api.dismissRecordingTag(recordingId, tag),
    onSettled: refresh,
    onError: (e) => onFail(e, t("workspace:errSaveTag")),
  });

  const lower = (t: string) => t.toLowerCase();

  function onAdd(tag: string) {
    // A promoted suggestion leaves the hint list in the same beat it becomes a chip.
    setPending({
      tags: [...shownTags, tag],
      suggested: shownSuggested.filter((s) => lower(s) !== lower(tag)),
    });
    add.mutate(tag);
  }

  function onRemove(tag: string) {
    setPending({
      tags: shownTags.filter((t) => lower(t) !== lower(tag)),
      suggested: shownSuggested,
    });
    remove.mutate(tag);
  }

  function onDismiss(tag: string) {
    setPending({
      tags: shownTags,
      suggested: shownSuggested.filter((s) => lower(s) !== lower(tag)),
    });
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
