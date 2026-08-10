import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { useRoomBasePath } from "../lib/rooms";
import { formatLongDate } from "../lib/format";

/// The other recordings of a recurring meeting, for jumping back to what was said last time.
///
/// Owns its own query rather than taking a list, which is what keeps `CalendarEventDetails` presentational -
/// that component is rendered in two places and neither should have to know how to fetch this.
///
/// Renders nothing when there is no history. A heading above an empty list would put a permanently dead
/// section on every one-off event, which is most of them.
export default function SeriesRecordings({ eventId }: { eventId: string }) {
  const { t, i18n } = useTranslation(["workspace"]);
  // Keep the links inside the current room, exactly as every other recording link in the app must.
  const basePath = useRoomBasePath();

  const { data = [] } = useQuery({
    queryKey: ["series-recordings", eventId],
    queryFn: () => api.getSeriesRecordings(eventId),
    retry: false,
  });

  if (data.length === 0) return null;

  return (
    <div className="mt-4 border-t pt-3 dark:border-gray-700">
      <h3 className="mb-1 text-sm font-medium text-gray-700 dark:text-gray-200">{t("workspace:calSeriesTitle")}</h3>
      <ul className="space-y-0.5">
        {data.map((r) => (
          <li key={r.id}>
            <Link
              to={`${basePath}/recordings/${r.id}`}
              className="flex items-baseline justify-between gap-2 text-sm text-indigo-600 hover:underline dark:text-indigo-400"
            >
              <span className="min-w-0 truncate">{r.name ?? r.title}</span>
              <span className="shrink-0 text-xs text-gray-500 dark:text-gray-400">
                {formatLongDate(r.startsAt, i18n.language)}
              </span>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
