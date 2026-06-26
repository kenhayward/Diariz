import { Link } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import Recorder from "./Recorder";
import UserMenu from "./UserMenu";

/// Persistent bar: brand on the left, the record control in the middle, the account menu on the right.
export default function TopBar() {
  const qc = useQueryClient();
  return (
    <header className="flex h-14 shrink-0 items-center justify-between gap-4 border-b bg-white px-4 dark:border-gray-700 dark:bg-gray-900">
      <Link to="/" className="flex shrink-0 items-center gap-2">
        <img src="/logo.png" alt="" className="h-7 w-auto" />
        <span className="text-lg font-semibold dark:text-gray-100">Diariz</span>
      </Link>

      <Recorder compact onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />

      <UserMenu />
    </header>
  );
}
