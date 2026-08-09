import { useTranslation } from "react-i18next";

/// The no-SMTP fallback: when the server could not email a new user their one-time setup link, it hands the
/// link back and the administrator sends it themselves. Shared by the Users tab (Add user) and the Requests
/// tab (Grant), because both calls can come back `emailed: false`.
export default function SetupLinkBanner({ url }: { url: string }) {
  const { t } = useTranslation("admin");
  return (
    <div className="rounded border border-blue-300 bg-blue-50 p-2 text-xs dark:border-blue-800 dark:bg-blue-950/40">
      <p className="mb-1 font-medium text-blue-800 dark:text-blue-300">{t("grantLinkMsg")}</p>
      {/* Selectable, and never truncated - it is useless if it cannot be copied whole. */}
      <code className="block break-all text-blue-700 dark:text-blue-300">{url}</code>
    </div>
  );
}
