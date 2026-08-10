import { useQueryClient } from "@tanstack/react-query";
import Recorder from "../Recorder";

/// The capture bar: the record cluster centred over the content column by two flex spacers. It sits above
/// the routed content and spans that column only - not the left panel, not the chat rail.
///
/// 73px + its 1px bottom border = 74px, which is exactly the left panel's first two rows (h-9 + border,
/// twice), so the bar's lower edge lands on the panel's second divider. That alignment is the point: if
/// those row heights change, change this with them. There is no window edge above the bar any more, so it
/// carries no top border (the header's 2px `--hub-bar-border-top` went with the header).
export default function CaptureBar() {
  const qc = useQueryClient();
  return (
    <div
      className="flex shrink-0 items-center gap-4 bg-[var(--hub-bar-bg)]"
      style={{
        height: 73,
        padding: "0 18px",
        boxSizing: "border-box",
        borderBottom: "1px solid var(--hub-bar-border-bottom)",
      }}
    >
      <div style={{ flex: 1 }} />

      {/* min-w-0 overrides the flex item's default automatic minimum size (its content's min-content
          width), which is what lets this cluster shrink instead of spilling out of the content column when
          the column is narrower than the cluster's natural width (see AudioSourceChip / RecordHero, whose
          labels truncate for the same reason). Do not add overflow-hidden here - the recorder's popovers
          are absolute children of its own relative root and would be clipped. */}
      <div data-tour="capture" className="min-w-0">
        <Recorder compact onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />
      </div>

      <div style={{ flex: 1 }} />
    </div>
  );
}
