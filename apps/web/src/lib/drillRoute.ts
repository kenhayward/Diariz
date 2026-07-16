import { useSearchParams } from "react-router-dom";

/// Where the left nav's drill-in list currently sits, carried in the URL as `?in=<sectionId>` (absent =
/// the room's top level). The rest of the path - including the recording open in the middle panel - is
/// untouched, so you can be drilled into a folder while reading a transcript from somewhere else.
///
/// The URL owns this, not component state, and that buys three things for free: browser **back pops a
/// level** (each drill is a push), the drill survives a reload, and a search takeover cannot disturb it
/// (the query lives in component state, so clearing the search restores the drill by construction rather
/// than by save/restore code that could get it wrong).
///
/// Deliberately **not** a route segment: `/sections/:id` already means "open the folder's page", and the
/// design's central rule is that drilling in and opening the page are distinct targets.
export interface DrillRoute {
  /// The section being browsed, or null at the top level.
  sectionId: string | null;
  /// Push one level deeper (or jump to any node - the breadcrumb pops several at once).
  drillTo: (sectionId: string | null) => void;
  /// Pop to the top level.
  drillOut: () => void;
}

/// The drill position as a query string to hang on a link (`"?in=<id>"`, or `""` at the top level).
///
/// Every link out of the panel needs this. A bare `to="/recordings/:id"` drops `?in=`, which pops the list
/// back to the root behind the page you just opened - you lose your place by using the thing you were
/// browsing towards.
///
/// Carries **only** `in`, never the whole of `location.search`: the URL also holds params that are about
/// one specific page - `?ts=` is a transcript match time - and forwarding those to a different recording
/// would scrub it to a timestamp that means nothing there.
export function useDrillSearch(): string {
  const [params] = useSearchParams();
  const inParam = params.get("in");
  return inParam ? `?in=${encodeURIComponent(inParam)}` : "";
}

export function useDrillSectionId(): DrillRoute {
  // A single unconditional hook. Do not be tempted into `useMatch(...) ?? useMatch(...)` here: joining
  // hook calls with `??` skips one whenever the first matches, which is the Rules-of-Hooks crash of
  // issue #289 (see `activeRoute.ts`).
  const [params, setParams] = useSearchParams();

  const drillTo = (sectionId: string | null) => {
    const next = new URLSearchParams(params);
    if (sectionId === null) next.delete("in");
    else next.set("in", sectionId);
    // Push, not replace - the history entry is what makes browser back pop a level.
    setParams(next);
  };

  return {
    sectionId: params.get("in"),
    drillTo,
    drillOut: () => drillTo(null),
  };
}
