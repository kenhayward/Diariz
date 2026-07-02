import { useState } from "react";

/// A round avatar for the signed-in user: the Google profile picture when available, otherwise an initials
/// bubble. Falls back to initials if the image fails to load (e.g. a stale/expired Google URL).
export default function Avatar({ initials, pictureUrl }: { initials: string; pictureUrl?: string | null }) {
  const [failed, setFailed] = useState(false);

  if (pictureUrl && !failed) {
    return (
      <img
        src={pictureUrl}
        alt=""
        referrerPolicy="no-referrer"
        onError={() => setFailed(true)}
        className="h-8 w-8 rounded-full object-cover"
      />
    );
  }

  return (
    <span className="flex h-8 w-8 items-center justify-center rounded-full bg-gray-900 text-xs font-medium text-white dark:bg-gray-100 dark:text-gray-900">
      {initials}
    </span>
  );
}
