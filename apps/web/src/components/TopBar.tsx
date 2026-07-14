import { Link } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import Recorder from "./Recorder";
import UserMenu from "./UserMenu";
import ThemeSync from "./ThemeSync";

/// Persistent "command hub" bar: brand on the left, the record cluster centered by two flex spacers,
/// the account avatar pinned right. Recorder + UserMenu keep their current internals for now (Unit 1
/// only establishes the 80px frame + token layer; later units restyle the cluster and avatar).
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
        <span
          className="text-[var(--hub-text)]"
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

      <UserMenu />
      <ThemeSync />
    </header>
  );
}
