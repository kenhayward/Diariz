import { Link } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import Recorder from "./Recorder";

/// Persistent "command hub" bar: brand on the left, the record cluster centered by two flex spacers.
/// The account avatar has moved into the left panel's room row; the shared popover context now lives in
/// WorkspaceLayout so it can span both.
export default function TopBar() {
  const qc = useQueryClient();
  return (
    <header
      className="flex shrink-0 items-center gap-4 bg-[var(--hub-bar-bg)]"
      style={{
        height: 80,
        padding: "0 22px",
        boxSizing: "border-box",
        borderTop: "2px solid var(--hub-bar-border-top)",
        borderBottom: "1px solid var(--hub-bar-border-bottom)",
      }}
    >
      <Link to="/" className="flex shrink-0 items-center" style={{ gap: 12 }}>
        <img src="/logo.png" alt="" style={{ width: 34, height: 34, borderRadius: 9 }} />
        {/* The wordmark collapses to just the mark at very narrow widths; the mark keeps the home link. */}
        <span
          className="hidden text-[var(--hub-text)] sm:inline"
          style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 21, letterSpacing: "-.01em" }}
        >
          Diariz
        </span>
      </Link>

      <div style={{ flex: 1 }} />

      <div data-tour="capture">
        <Recorder compact onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />
      </div>

      <div style={{ flex: 1 }} />
    </header>
  );
}
