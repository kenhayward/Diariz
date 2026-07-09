/// Inferring the chat's "current" context from what's open in the middle panel + the sidebar multi-select.
/// The explicit "Selected transcripts" option was removed; instead the single "current" context is inferred
/// and its label switches between Current Transcript / Selected Transcripts / Current Folder. Pure so the
/// inference + label mapping can be unit-tested.

export type CurrentContext =
  | { kind: "folder"; sectionId: string }
  | { kind: "single"; recordingId: string }
  | { kind: "selected"; recordingIds: string[] }
  | { kind: "empty" };

/// Precedence: 2+ ticked recordings (a deliberate multi-selection) win; else the open folder; else the open
/// recording; else nothing. (A folder page can only appear in the folder view; the recording view always
/// shows a single transcript.)
export function inferCurrentContext(args: {
  sectionId: string | null;
  recordingId: string | null;
  selectedIds: string[];
}): CurrentContext {
  if (args.selectedIds.length >= 2) return { kind: "selected", recordingIds: args.selectedIds };
  if (args.sectionId) return { kind: "folder", sectionId: args.sectionId };
  if (args.recordingId) return { kind: "single", recordingId: args.recordingId };
  return { kind: "empty" };
}

/// The i18n key for the current-context pill/menu label.
export function currentContextLabelKey(ctx: CurrentContext): "ctxFolder" | "ctxSelected" | "ctxCurrent" {
  if (ctx.kind === "folder") return "ctxFolder";
  if (ctx.kind === "selected") return "ctxSelected";
  return "ctxCurrent"; // single or empty
}

/// The chat-stream request pieces a current-context resolves to (recording ids and/or a folder section id).
export function currentContextRequest(ctx: CurrentContext): { recordingIds: string[]; sectionId: string | null } {
  switch (ctx.kind) {
    case "folder":
      return { recordingIds: [], sectionId: ctx.sectionId };
    case "single":
      return { recordingIds: [ctx.recordingId], sectionId: null };
    case "selected":
      return { recordingIds: ctx.recordingIds, sectionId: null };
    default:
      return { recordingIds: [], sectionId: null };
  }
}
