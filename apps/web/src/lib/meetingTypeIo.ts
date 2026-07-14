/// The portable subset of a meeting type (minutes template) - everything except instance/permission fields
/// (id, isPlatform, canEdit). This is what Export writes and Import reads.
///
/// A meeting type no longer carries a template: it points at the FORMULA that generates its minutes. Formula ids
/// are meaningless on another instance, so the export carries formula **names** and the import resolves them
/// against whatever that instance has. A name it can't resolve is reported, not silently dropped.
export interface MeetingTypeExport {
  groupName: string;
  title: string;
  overview: string;
  icon: string;
  color: string;
  primaryFormulaName: string | null;
  additionalFormulaNames: string[];
}

const MARKER = "diariz-meeting-type";
const DEFAULT_ICON = "document";
const DEFAULT_COLOR = "#5C6BC0";

/// Serialize a template to a pretty-printed JSON string, tagged with a format marker for validation on import.
export function serializeMeetingType(t: MeetingTypeExport): string {
  return JSON.stringify(
    {
      [MARKER]: 1,
      groupName: t.groupName,
      title: t.title,
      overview: t.overview,
      icon: t.icon,
      color: t.color,
      primaryFormulaName: t.primaryFormulaName,
      additionalFormulaNames: t.additionalFormulaNames,
    },
    null,
    2,
  );
}

/// Parse an exported (or raw) meeting-type JSON string into the portable subset. Throws on malformed input: bad
/// JSON, a non-object, or something that carries the marker of neither a meeting type nor a title. Optional
/// presentation fields default; unknown fields (id, isPlatform, ...) are ignored.
export function parseMeetingType(text: string): MeetingTypeExport {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    throw new Error("Not valid JSON.");
  }
  if (!obj || typeof obj !== "object") throw new Error("Not a meeting type.");
  const o = obj as Record<string, unknown>;
  if (!(MARKER in o) && !str(o.title)) throw new Error("Not a meeting type.");

  return {
    groupName: str(o.groupName),
    title: str(o.title),
    overview: str(o.overview),
    icon: str(o.icon) || DEFAULT_ICON,
    color: str(o.color) || DEFAULT_COLOR,
    primaryFormulaName: str(o.primaryFormulaName) || null,
    additionalFormulaNames: Array.isArray(o.additionalFormulaNames)
      ? o.additionalFormulaNames.filter((n): n is string => typeof n === "string")
      : [],
  };
}

/// Resolve exported formula NAMES against the formulas this instance actually has (case-insensitive). Returns the
/// ids it found plus the names it could not - so an import can report what is missing rather than quietly
/// producing a template that generates nothing.
export function resolveFormulaNames(
  names: readonly string[],
  formulas: readonly { id: string; name: string }[],
): { ids: string[]; missing: string[] } {
  const byName = new Map(formulas.map((f) => [f.name.toLowerCase(), f.id]));
  const ids: string[] = [];
  const missing: string[] = [];

  for (const name of names) {
    const id = byName.get(name.toLowerCase());
    if (id) ids.push(id);
    else missing.push(name);
  }
  return { ids, missing };
}

/// A filesystem-safe filename for an exported template.
export function exportFilename(title: string): string {
  const slug = title.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  return `${slug || "meeting-type"}.meeting-type.json`;
}

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}
