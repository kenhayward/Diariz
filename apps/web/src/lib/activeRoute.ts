import { useMatch } from "react-router-dom";

/// The id from a detail route, matched whether the URL is the top-level (personal-room) form -
/// `/{kind}/:id` - or the shared-room form `/rooms/:roomId/{kind}/:id`. Both useMatch calls are
/// UNCONDITIONAL: joining them with `??` would skip the second hook whenever the first matches, changing
/// the hook count between renders - the Rules-of-Hooks crash fixed in issue #289. Combine the results of
/// the two hooks, never the hook calls themselves.
function useDetailRouteId(kind: "recordings" | "sections"): string | null {
  const legacy = useMatch(`/${kind}/:id`);
  const scoped = useMatch(`/rooms/:roomId/${kind}/:id`);
  return legacy?.params.id ?? scoped?.params.id ?? null;
}

/// The recording id currently open in the middle panel (personal- or shared-room route), or null.
/// Seeds the chat's default context and highlights the active row in the sidebar list.
export function useActiveRecordingId(): string | null {
  return useDetailRouteId("recordings");
}

/// The folder id currently open (personal- or shared-room route), or null. Seeds the chat's "current
/// folder" context.
export function useActiveSectionId(): string | null {
  return useDetailRouteId("sections");
}
