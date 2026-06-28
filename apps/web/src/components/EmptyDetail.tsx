import { useQuery } from "@tanstack/react-query";
import { api } from "../lib/api";
import { useTour } from "../lib/tour";
import { emptyStateCopy } from "../lib/onboarding";

/// The centre panel when no recording is selected. Shows the backdrop image with welcoming copy:
/// brand-new users get a "Welcome / get started" prompt and the tour; returning users get a
/// "pick a recording" nudge. `-m-6` bleeds past the panel's padding so the image fills the area.
export default function EmptyDetail() {
  const { data: recordings = [] } = useQuery({ queryKey: ["recordings"], queryFn: api.listRecordings });
  const tour = useTour();
  const copy = emptyStateCopy(recordings.length);

  return (
    <div className="relative -m-6 flex min-h-[calc(100vh-3.5rem)] items-center justify-center overflow-hidden">
      <img src="/background.webp" alt="" className="absolute inset-0 h-full w-full object-cover" />
      <div className="absolute inset-0 bg-black/30" />
      <div className="relative mx-4 max-w-md rounded-2xl border border-white/20 bg-white/90 p-8 text-center shadow-2xl backdrop-blur dark:bg-gray-900/85">
        <img src="/logo.png" alt="" className="mx-auto h-12 w-auto" />
        <h2 className="mt-4 text-xl font-semibold text-gray-900 dark:text-gray-100">{copy.title}</h2>
        <p className="mt-2 text-sm text-gray-600 dark:text-gray-300">{copy.body}</p>
        <button
          type="button"
          onClick={tour.start}
          className="mt-5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
        >
          {copy.showTour ? "Take the tour" : "Show guided tour"}
        </button>
      </div>
    </div>
  );
}
