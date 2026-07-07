import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { api } from "../lib/api";
import { useTheme } from "../theme";

/// Adopts the user's server-persisted theme once signed in, so it follows them across devices/browsers.
/// localStorage (via the ThemeProvider) is the fast pre-auth cache; this reconciles it with the authoritative
/// profile value. Renders nothing.
export default function ThemeSync() {
  const { setTheme } = useTheme();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });

  useEffect(() => {
    if (profile?.theme) setTheme(profile.theme);
  }, [profile?.theme, setTheme]);

  return null;
}
