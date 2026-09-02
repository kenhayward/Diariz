import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { api } from "../lib/api";
import { useTour } from "../lib/tour";

/// The centre panel when no recording is selected. Shows the backdrop image with welcoming copy:
/// brand-new users get a "Welcome / get started" prompt and the tour; returning users get a
/// "pick a recording" nudge. `-m-6` bleeds past the panel's padding so the image fills the area.
export default function EmptyDetail() {
  const { t } = useTranslation("workspace");
  const { data: recordings = [] } = useQuery({ queryKey: ["recordings"], queryFn: () => api.listRecordings() });
  const tour = useTour();
  const isNew = recordings.length === 0;

  return (
    <div className="relative -m-6 flex min-h-[calc(100vh-3.5rem)] items-center justify-center overflow-hidden">
      <img src="/background.webp" alt="" className="absolute inset-0 h-full w-full object-cover" />
      <div className="absolute inset-0 bg-black/30" />
      <div className="relative mx-4 max-w-md rounded-2xl border border-white/20 bg-white/90 p-8 text-center shadow-2xl backdrop-blur dark:bg-gray-900/85">
        <img src="/logo.png" alt="" className="mx-auto h-12 w-auto" />
        <h2 className="mt-4 text-xl font-semibold text-gray-900 dark:text-gray-100">
          {isNew ? t("emptyWelcomeTitle") : t("emptySelectTitle")}
        </h2>
        <p className="mt-2 text-sm text-gray-600 dark:text-gray-300">
          {isNew ? t("emptyWelcomeBody") : t("emptySelectBody")}
        </p>
        <div className="mt-5 flex items-center justify-center gap-4">
          <button
            type="button"
            onClick={tour.start}
            className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            {isNew ? t("takeTour") : t("account:showTour")}
          </button>
          {/* In place, not a new tab: /help is an app route behind the login, so a new tab leaves the
              desktop shell and the installed PWA for the system browser where nobody is signed in. */}
          <Link
            to="/help"
            className="text-sm text-blue-600 hover:underline dark:text-blue-400"
          >
            {t("help:browseHelp")}
          </Link>
        </div>
      </div>
    </div>
  );
}
