import type { ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { useTranslation } from "react-i18next";
import ErrorBoundary from "./ErrorBoundary";

/// An ErrorBoundary wired for a routed subtree: it reads the current path as the reset key (so navigating to
/// another page clears a crash) and pulls its user-facing copy from i18n. Used at the app level (so a crash
/// anywhere in the workspace shows a message instead of blanking the whole app - issue #289) and around the
/// detail panel.
export default function RouteErrorBoundary({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const { t } = useTranslation("workspace");
  return (
    <ErrorBoundary resetKey={pathname} message={t("detailErrorTitle")} hint={t("detailErrorHint")}>
      {children}
    </ErrorBoundary>
  );
}
