/// Shaping search hits for the "everywhere" results view: grouping them by folder, working out which filter
/// chips to offer, and applying those chips.
///
/// **Facets are derived from the hits, not fetched.** The folder chip has to span every room the caller can
/// see, and `GET /api/sections?roomId=` is per-room - so building the options from the results keeps this
/// client-side and, better, never offers a filter that would match nothing. The honest limit: facets only
/// describe the hits that came back (`limit` <= 50), so a filter cannot surface a folder that didn't make the
/// cut. At that ceiling it doesn't bite; if it ever does, the fix is server-side facet counts, not a reshuffle
/// here.

import type { RecordingSearchHit } from "./types";

/// Sentinel id for the group holding hits that live in no folder.
export const UNGROUPED_GROUP = "__ungrouped__";

export interface SearchGroup {
  /// The folder's id, or `UNGROUPED_GROUP`.
  id: string;
  /// The folder's name; null for the ungrouped group (the caller supplies its label).
  name: string | null;
  hits: RecordingSearchHit[];
}

export interface SearchFacets {
  sections: { id: string; name: string; count: number }[];
  speakers: string[];
}

export interface SearchFilterState {
  sectionId?: string | null;
  speaker?: string | null;
  /// ISO date (inclusive lower bound) applied to the recording's date.
  from?: string | null;
  to?: string | null;
}

/// Hits grouped under their folder, ordered so the most relevant group comes first and the best hit stays at
/// the top of the list overall - grouping must not bury the answer the ranking just found. Ungrouped hits
/// always come last, however strong: "in no folder" is a footnote, not a heading.
export function groupBySection(hits: RecordingSearchHit[]): SearchGroup[] {
  const groups = new Map<string, SearchGroup>();
  for (const hit of hits) {
    const id = hit.sectionId ?? UNGROUPED_GROUP;
    const group = groups.get(id) ?? { id, name: hit.sectionName ?? null, hits: [] };
    group.hits.push(hit);
    groups.set(id, group);
  }

  const best = (g: SearchGroup) => Math.max(...g.hits.map((h) => h.score));
  return [...groups.values()]
    .map((g) => ({ ...g, hits: [...g.hits].sort((a, b) => b.score - a.score) }))
    .sort((a, b) => {
      if (a.id === UNGROUPED_GROUP) return 1;
      if (b.id === UNGROUPED_GROUP) return -1;
      return best(b) - best(a);
    });
}

/// The filter options worth showing, drawn from the hits themselves. Folders are ordered by how many hits they
/// hold (the ones worth narrowing to come first); speakers alphabetically, since there's no equivalent signal.
export function facetsOf(hits: RecordingSearchHit[]): SearchFacets {
  const sections = new Map<string, { id: string; name: string; count: number }>();
  const speakers = new Set<string>();

  for (const hit of hits) {
    if (hit.sectionId && hit.sectionName) {
      const existing = sections.get(hit.sectionId);
      if (existing) existing.count++;
      else sections.set(hit.sectionId, { id: hit.sectionId, name: hit.sectionName, count: 1 });
    }
    if (hit.speakerName) speakers.add(hit.speakerName);
  }

  return {
    sections: [...sections.values()].sort((a, b) => b.count - a.count || a.name.localeCompare(b.name)),
    speakers: [...speakers].sort((a, b) => a.localeCompare(b)),
  };
}

/// Narrow the hits by the chips. Applied client-side over the returned set rather than re-querying: the chips
/// are built from these hits, so every option is guaranteed to leave something behind, and narrowing is instant.
export function applyFilters(hits: RecordingSearchHit[], filters: SearchFilterState): RecordingSearchHit[] {
  return hits.filter((h) => {
    if (filters.sectionId && h.sectionId !== filters.sectionId) return false;
    if (filters.speaker && h.speakerName !== filters.speaker) return false;
    if (filters.from && new Date(h.createdAt) < new Date(filters.from)) return false;
    // `to` is an inclusive day: compare against the end of it, not midnight, or the chosen day drops out.
    if (filters.to && new Date(h.createdAt) > new Date(new Date(filters.to).getTime() + 86_399_999)) return false;
    return true;
  });
}
