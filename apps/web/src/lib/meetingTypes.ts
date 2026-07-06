import type { MeetingType } from "./types";

/// Group meeting types by their template group, preserving the server's ordering (types arrive ordered by
/// group then title). Returns [groupName, types][] so the picker can render group headers in order.
export function groupMeetingTypes(types: MeetingType[]): [string, MeetingType[]][] {
  const out: [string, MeetingType[]][] = [];
  for (const ty of types) {
    const last = out[out.length - 1];
    if (last && last[0] === ty.groupName) last[1].push(ty);
    else out.push([ty.groupName, [ty]]);
  }
  return out;
}

/// The type to show as currently selected: the explicitly-applied one, else the General default (a recording
/// with no explicit type uses it), else null when there are no types.
export function selectedMeetingType(
  types: MeetingType[],
  currentTypeId: string | null | undefined,
): MeetingType | null {
  return types.find((x) => x.id === currentTypeId) ?? types.find((x) => x.isDefault) ?? null;
}
