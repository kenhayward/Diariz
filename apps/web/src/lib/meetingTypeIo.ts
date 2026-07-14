import type { TemplateContent } from "./types";

/// The portable subset of a meeting type (minutes template) - everything except instance/permission fields
/// (id, isPlatform, canEdit). This is what Export writes and Import reads.
export interface MeetingTypeExport {
  groupName: string;
  title: string;
  overview: string;
  icon: string;
  color: string;
  content: TemplateContent;
}

const MARKER = "diariz-meeting-type";
const DEFAULT_ICON = "document";
const DEFAULT_COLOR = "#5C6BC0";

/// Serialize a template to a pretty-printed JSON string, tagged with a format marker for validation on import.
export function serializeMeetingType(t: MeetingTypeExport): string {
  return JSON.stringify(
    { [MARKER]: 1, groupName: t.groupName, title: t.title, overview: t.overview, icon: t.icon, color: t.color, content: t.content },
    null,
    2,
  );
}

/// Parse an exported (or raw) meeting-type JSON string into the portable subset. Throws on malformed input:
/// bad JSON, a non-object, or a missing/invalid `content.sections` array. Optional presentation fields default;
/// unknown fields (id, isPlatform, ...) are ignored.
export function parseMeetingType(text: string): MeetingTypeExport {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    throw new Error("Not valid JSON.");
  }
  if (!obj || typeof obj !== "object") throw new Error("Not a meeting type.");
  const o = obj as Record<string, unknown>;

  const content = o.content;
  if (!content || typeof content !== "object" || !Array.isArray((content as { sections?: unknown }).sections))
    throw new Error("Not a meeting type (missing sections).");

  return {
    groupName: str(o.groupName),
    title: str(o.title),
    overview: str(o.overview),
    icon: str(o.icon) || DEFAULT_ICON,
    color: str(o.color) || DEFAULT_COLOR,
    content: content as TemplateContent,
  };
}

/// A filesystem-safe filename for an exported template.
export function exportFilename(title: string): string {
  const slug = title.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  return `${slug || "meeting-type"}.meeting-type.json`;
}

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}
