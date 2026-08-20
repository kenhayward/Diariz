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
      // The bar's own chrome is 68px of dead space around the cluster - 36px of padding plus the two gap-4
      // gutters to the centring spacers - which is a quarter of the bar once the cluster is down to its
      // icons, so it tightens too. The padding lives on the class rather than the inline style below
      // because an inline style beats a class and the container query could never win. One shared
      // threshold, not the per-state ones the cluster uses: this bar does not know whether a recording is
      // running, and 44px is noise at the recording tier's 690px.
      className="@container flex shrink-0 items-center gap-4 px-[18px] bg-[var(--hub-bar-bg)] @max-[480px]:gap-1 @max-[480px]:px-2"
      style={{
        height: 72,
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
