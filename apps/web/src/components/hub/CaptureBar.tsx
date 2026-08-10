import { useQueryClient } from "@tanstack/react-query";
import Recorder from "../Recorder";

/// The capture bar: the record cluster centred over the content column by two flex spacers. It sits above
/// the routed content and spans that column only - not the left panel, not the chat rail.
///
/// 72px, which is exactly the left panel's first two rows, so the bar's lower edge lands on the panel's
/// second divider (the one under the list toolbar). That alignment is the point: if those row heights
/// change, change this with them.
///
/// The arithmetic is 36 + 36, NOT 37 + 37: Tailwind's preflight sets `box-sizing: border-box` globally, so
/// a row's `h-9` (36px) already contains its 1px `border-b` rather than adding to it. The design handoff
/// specified 73px on the content-box reading and was 1px out in the build - measured in the browser, the
/// panel's second divider sits at y=72. This bar is border-box too, so its own 1px bottom border is inside
/// the 72 as well. There is no window edge above the bar any more, so it carries no top border (the
/// header's 2px `--hub-bar-border-top` went with the header).
export default function CaptureBar() {
  const qc = useQueryClient();
  return (
    <div
      // `@container` makes this bar the size reference for the cluster inside it. The bar's width is
      // `window - left panel - chat panel`, which is not tied to any viewport breakpoint, so the cluster's
      // collapsible labels ask THIS box how much room there is (see AudioSourceChip / RecordHero's
      // `@xl:inline`) rather than asking the window and getting the wrong answer.
      className="@container flex shrink-0 items-center gap-4 bg-[var(--hub-bar-bg)]"
      style={{
        height: 72,
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
