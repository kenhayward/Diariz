import { useState, type CSSProperties } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth";
import { api } from "../lib/api";
import { useTour } from "../lib/tour";
import { formatBytes, storagePercent } from "../lib/format";
import { transcriptionTimeParts } from "../lib/transcriptionTime";
import { useHubPopover } from "./hub/hubPopovers";
import HubPopover from "./hub/HubPopover";
import Avatar from "./Avatar";
import SettingsModal from "./SettingsModal";
import PreferencesModal from "./PreferencesModal";
import ManageUsersModal from "./ManageUsersModal";
import ManageFormulasModal from "./ManageFormulasModal";
import AboutModal from "./AboutModal";

// One menu row inside the account popover. Rows are padded, rounded and highlight on hover; the footer
// "Sign out" row is red-tinted. Kept as a real <button role="menuitem"> so the accessible name / gating
// assertions carry over from the old menu.
function MenuRow({
  label,
  onSelect,
  variant = "default",
}: {
  label: string;
  onSelect: () => void;
  variant?: "default" | "danger";
}) {
  const danger = variant === "danger";
  const base: CSSProperties = {
    display: "block",
    width: "100%",
    padding: 10,
    borderRadius: 9,
    border: "none",
    background: "transparent",
    color: danger ? "var(--hub-red-text)" : "var(--hub-text)",
    fontFamily: "system-ui",
    fontWeight: danger ? 600 : 500,
    fontSize: 14.5,
    textAlign: "left",
    cursor: "pointer",
  };
  const hoverBg = danger ? "var(--hub-red-soft-bg)" : "var(--hub-surface-hover)";
  return (
    <button
      type="button"
      role="menuitem"
      onClick={onSelect}
      style={base}
      onMouseEnter={(e) => (e.currentTarget.style.background = hoverBg)}
      onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
    >
      {label}
    </button>
  );
}

/**
 * The command-hub account menu: a 46px avatar button that toggles a 308px account popover (header, usage
 * stats, menu rows, Sign out). The avatar shares the hub's single-open popover state (`useHubPopover`, id
 * "acct") so opening it closes the recorder popovers and vice-versa; `HubPopover` owns the backdrop +
 * Escape. Behaviour, gating and data are unchanged from the old dropdown - this is a restyle.
 */
export default function UserMenu() {
  const { t } = useTranslation("account");
  const { initials, pictureUrl, email, fullName, isAdmin, isPlatformAdmin, canManageFormulas, logout } = useAuth();
  const tour = useTour();
  const { data: storage } = useQuery({ queryKey: ["user-storage"], queryFn: api.getUserStorage });
  const { isOpen, toggle, close } = useHubPopover();
  const open = isOpen("acct");
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [preferencesOpen, setPreferencesOpen] = useState(false);
  const [usersOpen, setUsersOpen] = useState(false);
  const [formulasOpen, setFormulasOpen] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);

  // Run a menu action: close the popover, then open the chosen modal / start the tour.
  const run = (fn: () => void) => () => {
    close();
    fn();
  };

  const name = fullName ?? email;

  return (
    <div className="relative">
      <button
        type="button"
        aria-label="Account"
        aria-haspopup="menu"
        aria-expanded={open}
        data-tour="account"
        onClick={() => toggle("acct")}
        style={{
          padding: 0,
          borderRadius: "50%",
          border: "2px solid var(--hub-avatar-border)",
          overflow: "hidden",
          display: "grid",
          placeItems: "center",
          lineHeight: 0,
          cursor: "pointer",
          background: "transparent",
        }}
        onMouseEnter={(e) => (e.currentTarget.style.borderColor = "var(--hub-border-hover)")}
        onMouseLeave={(e) => (e.currentTarget.style.borderColor = "var(--hub-avatar-border)")}
      >
        <Avatar initials={initials} pictureUrl={pictureUrl} size="md" />
      </button>

      <HubPopover open={open} onClose={close} width={308} anchorClassName="right-0" ariaLabel="Account">
        <div style={{ overflow: "hidden", borderRadius: 14 }}>
          {/* Header: avatar + name + email. */}
          <div
            style={{
              display: "flex",
              alignItems: "center",
              gap: 12,
              padding: 16,
              borderBottom: "1px solid var(--hub-popover-border)",
            }}
          >
            <Avatar initials={initials} pictureUrl={pictureUrl} size="md" />
            <div style={{ minWidth: 0 }}>
              <div
                style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 16, color: "var(--hub-text)" }}
                className="truncate"
              >
                {name}
              </div>
              {fullName && (
                <div
                  style={{ fontFamily: "system-ui", fontWeight: 400, fontSize: 13, color: "var(--hub-muted)" }}
                  className="truncate"
                >
                  {email}
                </div>
              )}
            </div>
          </div>

          {/* Usage stats: storage line + progress track, and total transcription time when present. */}
          {storage && (
            <div style={{ padding: 16, borderBottom: "1px solid var(--hub-popover-border)" }}>
              <div
                style={{ fontFamily: "system-ui", fontWeight: 500, fontSize: 12.5, color: "var(--hub-text-2)" }}
              >
                {t("storageUsage", {
                  used: formatBytes(storage.usedBytes),
                  total: formatBytes(storage.quotaBytes),
                  percent: storagePercent(storage.usedBytes, storage.quotaBytes),
                })}
              </div>
              <div
                style={{
                  marginTop: 8,
                  height: 5,
                  borderRadius: 3,
                  background: "var(--hub-surface-hover)",
                  overflow: "hidden",
                }}
              >
                <div
                  style={{
                    height: "100%",
                    width: `${storagePercent(storage.usedBytes, storage.quotaBytes)}%`,
                    background: "var(--hub-blue)",
                  }}
                />
              </div>
              {storage.totalTranscriptionMs > 0 && (
                <div
                  style={{
                    marginTop: 10,
                    fontFamily: "system-ui",
                    fontWeight: 500,
                    fontSize: 12.5,
                    color: "var(--hub-text-2)",
                  }}
                >
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

          {/* Menu rows (same gating + modal handlers as before). */}
          <div role="menu" style={{ padding: 8, display: "flex", flexDirection: "column" }}>
            <MenuRow label={t("preferences")} onSelect={run(() => setPreferencesOpen(true))} />
            {isPlatformAdmin && <MenuRow label={t("settings")} onSelect={run(() => setSettingsOpen(true))} />}
            {isAdmin && <MenuRow label={t("manageUsers")} onSelect={run(() => setUsersOpen(true))} />}
            {canManageFormulas && (
              <MenuRow label={t("manageFormulas")} onSelect={run(() => setFormulasOpen(true))} />
            )}
            <MenuRow label={t("showTour")} onSelect={run(() => tour.start())} />
            <MenuRow label={t("about")} onSelect={run(() => setAboutOpen(true))} />
          </div>

          {/* Footer: Sign out. */}
          <div style={{ padding: 8, borderTop: "1px solid var(--hub-popover-border)" }}>
            <MenuRow label={t("signOut")} variant="danger" onSelect={run(logout)} />
          </div>
        </div>
      </HubPopover>

      {preferencesOpen && <PreferencesModal onClose={() => setPreferencesOpen(false)} />}
      {settingsOpen && <SettingsModal onClose={() => setSettingsOpen(false)} />}
      {usersOpen && <ManageUsersModal onClose={() => setUsersOpen(false)} />}
      {formulasOpen && <ManageFormulasModal onClose={() => setFormulasOpen(false)} />}
      {aboutOpen && <AboutModal onClose={() => setAboutOpen(false)} />}
    </div>
  );
}
