import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth";
import { api } from "../lib/api";
import { useTour } from "../lib/tour";
import { formatBytes, storagePercent } from "../lib/format";
import { transcriptionTimeParts } from "../lib/transcriptionTime";
import Avatar from "./Avatar";
import SettingsModal from "./SettingsModal";
import PreferencesModal from "./PreferencesModal";
import ManageUsersModal from "./ManageUsersModal";
import AboutModal from "./AboutModal";

export default function UserMenu() {
  const { t } = useTranslation("account");
  const { initials, pictureUrl, email, fullName, isAdmin, logout } = useAuth();
  const tour = useTour();
  const { data: storage } = useQuery({ queryKey: ["user-storage"], queryFn: api.getUserStorage });
  const [open, setOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [preferencesOpen, setPreferencesOpen] = useState(false);
  const [usersOpen, setUsersOpen] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-label="Account"
        aria-haspopup="menu"
        aria-expanded={open}
        data-tour="account"
        onClick={() => setOpen((v) => !v)}
      >
        <Avatar initials={initials} pictureUrl={pictureUrl} />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 z-20 mt-2 w-48 overflow-hidden rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {(fullName || email) && (
            <div className="border-b px-3 py-2 dark:border-gray-700">
              <div className="truncate text-sm font-medium dark:text-gray-100">{fullName ?? email}</div>
              {storage && (
                <div className="mt-0.5 text-xs text-gray-500 dark:text-gray-400">
                  {t("storageUsage", {
                    used: formatBytes(storage.usedBytes),
                    total: formatBytes(storage.quotaBytes),
                    percent: storagePercent(storage.usedBytes, storage.quotaBytes),
                  })}
                </div>
              )}
              {storage && storage.totalTranscriptionMs > 0 && (
                <div className="mt-0.5 text-xs text-gray-500 dark:text-gray-400">
                  {(() => {
                    const { days, clock } = transcriptionTimeParts(storage.totalTranscriptionMs);
                    return days > 0
                      ? t("transcriptionTotalDays", { days, clock })
                      : t("transcriptionTotal", { clock });
                  })()}
                </div>
              )}
            </div>
          )}
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              setPreferencesOpen(true);
            }}
            className="block w-full px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("preferences")}
          </button>
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              setSettingsOpen(true);
            }}
            className="block w-full px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("settings")}
          </button>
          {isAdmin && (
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                setUsersOpen(true);
              }}
              className="block w-full px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("manageUsers")}
            </button>
          )}

          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              tour.start();
            }}
            className="block w-full px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("showTour")}
          </button>

          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              setAboutOpen(true);
            }}
            className="block w-full px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("about")}
          </button>

          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              logout();
            }}
            className="block w-full border-t px-3 py-1.5 text-left text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("signOut")}
          </button>
        </div>
      )}

      {preferencesOpen && <PreferencesModal onClose={() => setPreferencesOpen(false)} />}
      {settingsOpen && <SettingsModal onClose={() => setSettingsOpen(false)} />}
      {usersOpen && <ManageUsersModal onClose={() => setUsersOpen(false)} />}
      {aboutOpen && <AboutModal onClose={() => setAboutOpen(false)} />}
    </div>
  );
}
