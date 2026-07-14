import type { ReactNode } from "react";
import type { SectionKey } from "../../lib/detailSection";
import SectionBreadcrumb from "./SectionBreadcrumb";

/// One drilled-in section of the recording-detail page: the same `{ key, label, toolbar, content }` shape
/// the tab strip used, so the page's existing section bodies carry over unchanged.
export interface DetailSection {
  key: SectionKey;
  label: string;
  icon?: ReactNode;
  toolbar?: ReactNode;
  /// Shown at the right of the breadcrumb (the Transcript's segment count).
  meta?: ReactNode;
  content: ReactNode;
}

/// The recording-detail router: the hub, or one section drilled into from it.
///
/// Like the tab strip it replaces, this renders **only** the active section — which is why the page's
/// `<audio>` element is mounted outside it. Moving the audio in here would unmount it (and null its ref)
/// the moment you left the Transcript, breaking speaker playback everywhere else.
export default function DetailSections({
  sections,
  active,
  onSelect,
  hub,
}: {
  sections: DetailSection[];
  active: SectionKey;
  onSelect: (key: SectionKey) => void;
  hub: ReactNode;
}) {
  if (active === "hub") return <>{hub}</>;

  const current = sections.find((s) => s.key === active);
  // A persisted key for a section this recording doesn't offer: fall back to the hub rather than blank.
  if (!current) return <>{hub}</>;

  return (
    <div>
      <div className="sticky top-0 z-10 -mx-6 -mt-6 space-y-2.5 bg-gray-50 px-6 pt-6 pb-2.5 dark:bg-gray-950">
        <SectionBreadcrumb
          title={current.label}
          icon={current.icon}
          onBack={() => onSelect("hub")}
          right={current.meta}
        />
        {current.toolbar && (
          <div className="flex flex-wrap items-center gap-0.5 rounded-lg border border-gray-200 bg-white px-2 py-1.5 dark:border-gray-700 dark:bg-gray-800">
            {current.toolbar}
          </div>
        )}
      </div>
      <div className="pt-3">{current.content}</div>
    </div>
  );
}
