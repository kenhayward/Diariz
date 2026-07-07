import { useState } from "react";

/// A round avatar for the signed-in user: the Google profile picture when available, otherwise an initials
/// bubble. Falls back to initials if the image fails to load (e.g. a stale/expired Google URL). `size`
/// picks the diameter: "sm" (h-8, the header avatar) or "lg" (h-16, the Preferences left panel).
export default function Avatar({
  initials,
  pictureUrl,
  size = "sm",
}: {
  initials: string;
  pictureUrl?: string | null;
  size?: "sm" | "lg";
}) {
  const [failed, setFailed] = useState(false);
  const box = size === "lg" ? "h-16 w-16 text-lg" : "h-8 w-8 text-xs";

  if (pictureUrl && !failed) {
    return (
      <img
        src={pictureUrl}
        alt=""
        referrerPolicy="no-referrer"
        onError={() => setFailed(true)}
        className={`${box} rounded-full object-cover`}
      />
    );
  }

  return (
    <span
      className={`${box} flex items-center justify-center rounded-full bg-gray-900 font-medium text-white dark:bg-gray-100 dark:text-gray-900`}
    >
      {initials}
    </span>
  );
}
