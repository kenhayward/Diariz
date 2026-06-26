import { useMatch } from "react-router-dom";

/// The recording id currently open in the middle panel (from the `/recordings/:id` route), or null.
/// Used to seed the chat's default context selection.
export function useActiveRecordingId(): string | null {
  const match = useMatch("/recordings/:id");
  return match?.params.id ?? null;
}
