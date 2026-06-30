/// Pure helpers for the Actions tab (the cross-transcript action list). The view layer drives these so the
/// person/hide-complete filtering and the mark-complete toggle logic are unit-testable without React.

import type { ActionListItem } from "./types";

/// The distinct, sorted, non-empty actor names across the given actions — drives the "filter by person" dropdown.
export function distinctActors(actions: ActionListItem[]): string[] {
  const set = new Set<string>();
  for (const a of actions) {
    const name = a.actor.trim();
    if (name) set.add(name);
  }
  return [...set].sort((x, y) => x.localeCompare(y));
}

export interface ActionFilters {
  /// Exact actor name to keep, or null/"" for everyone.
  person: string | null;
  /// When true, drop completed actions.
  hideComplete: boolean;
}

/// Apply the person + hide-complete filters (order preserved).
export function filterActions(actions: ActionListItem[], { person, hideComplete }: ActionFilters): ActionListItem[] {
  return actions.filter((a) => {
    if (hideComplete && a.completed) return false;
    if (person && a.actor.trim() !== person) return false;
    return true;
  });
}

/// What "Mark complete" should set the selected actions to: un-complete (false) only when every selected
/// action is already complete, otherwise complete (true). Empty selection → true (no-op upstream).
export function completeTarget(selected: { completed: boolean }[]): boolean {
  if (selected.length === 0) return true;
  return !selected.every((a) => a.completed);
}
