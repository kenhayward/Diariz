import type { ReactNode } from "react";

/// Full-screen shell for the unauthenticated pages (sign in, request access, account setup).
/// Renders the shared photographic backdrop with a scrim for card contrast, then centres its child card.
export default function AuthShell({ children }: { children: ReactNode }) {
  return (
    <div
      className="relative flex min-h-screen items-center justify-center bg-gray-50 bg-cover bg-center p-4 dark:bg-gray-950"
      style={{ backgroundImage: "url(/background.webp)" }}
    >
      {/* Scrim keeps the cards readable over the photo and looks consistent in light/dark. */}
      <div className="absolute inset-0 bg-black/30 dark:bg-black/50" aria-hidden />
      <div className="relative z-10 flex w-full justify-center">{children}</div>
    </div>
  );
}
