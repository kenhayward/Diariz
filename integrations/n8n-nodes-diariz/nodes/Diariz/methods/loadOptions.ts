import type { IDataObject, ILoadOptionsFunctions, INodePropertyOptions } from "n8n-workflow";
import { diarizApiRequest } from "../transport/request";

/// Populates a dropdown from a Diariz listing endpoint. A failure here would otherwise surface as an opaque
/// "could not load options" in the editor, so the message says which list could not be read.
async function options(
  ctx: ILoadOptionsFunctions,
  path: string,
  label: (row: IDataObject) => string,
): Promise<INodePropertyOptions[]> {
  const rows = (await diarizApiRequest.call(ctx, "GET", path)) as IDataObject[];
  if (!Array.isArray(rows)) return [];
  return rows
    .map((row) => ({ name: label(row) || "Untitled", value: String(row.id) }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

export async function getRecordings(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  return options(this, "/api/recordings", (r) => (r.name as string) || (r.title as string));
}

export async function getFolders(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  return options(this, "/api/sections", (r) => r.name as string);
}

export async function getRooms(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  return options(this, "/api/rooms", (r) => r.name as string);
}

export async function getFormulas(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  return options(this, "/api/formulas", (r) => r.name as string);
}

export async function getSpeakerProfiles(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  return options(this, "/api/speaker-profiles", (r) => (r.displayName as string) || (r.name as string));
}

export async function getMeetingTypes(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  return options(this, "/api/meeting-types", (r) => (r.title as string) || (r.name as string));
}

/// The active Workflow Signals, for a platform subscription's signal filter. Deliberately NOT built on
/// options() above: a subscription's SignalFilter stores the signal's KEY, not its id, and the publisher
/// compares keys - so an id here would produce a filter that matches nothing and a webhook that never fires.
/// Open to any signed-in user, so the picker works before the credential is known to be an administrator.
export async function getWorkflowSignals(this: ILoadOptionsFunctions): Promise<INodePropertyOptions[]> {
  const rows = (await diarizApiRequest.call(this, "GET", "/api/workflow-signals")) as IDataObject[];
  if (!Array.isArray(rows)) return [];
  return rows
    .map((r) => ({
      name: (r.label as string) || (r.key as string),
      value: String(r.key),
      description: (r.description as string) || undefined,
    }))
    .sort((a, b) => a.name.localeCompare(b.name));
}
