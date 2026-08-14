import { useRef, useState, type ClipboardEvent, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import HubPopover from "../hub/HubPopover";
import { TagIcon } from "../icons";
import { addTag, normalizeTag } from "../../lib/tagInput";

/// The hub's tag editor: type tags, remove tags, and pick or ignore the automatically suggested ones.
/// Presentational and callback-driven - the parent owns the data and the server round-trip, so this file
/// can be tested without a query client. There is no Save button: each action is its own change, which is
/// what the header's "saved as you type" promises.
export default function TagsPopover({
  open,
  onClose,
  tags,
  suggested,
  onAdd,
  onRemove,
  onDismiss,
}: {
  open: boolean;
  onClose: () => void;
  tags: string[];
  suggested: string[];
  onAdd: (tag: string) => void;
  onRemove: (tag: string) => void;
  onDismiss: (tag: string) => void;
}) {
  const { t } = useTranslation(["workspace"]);
  const [draft, setDraft] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);

  /// Commits the draft if it is a tag we do not already have. Returns nothing: the parent decides what a
  /// successful add does to `tags`, and this component re-renders from the new props.
  function commit(raw: string) {
    const { added } = addTag(tags, raw);
    if (added !== null) onAdd(added);
    setDraft("");
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === " ") {
      // Space ends a word. Prevent default so the space never reaches the field - a tag has no spaces.
      e.preventDefault();
      if (draft.trim().length > 0) commit(draft);
      return;
    }
    if (e.key === "Enter") {
      // Enter means "done": commit whatever is there, then close.
      e.preventDefault();
      if (draft.trim().length > 0) commit(draft);
      onClose();
      return;
    }
    if (e.key === "Backspace" && draft.length === 0 && tags.length > 0) {
      // An empty field plus Backspace reaches back into the chips.
      e.preventDefault();
      onRemove(tags[tags.length - 1]);
    }
  }

  function onPaste(e: ClipboardEvent<HTMLInputElement>) {
    // A pasted phrase becomes one hyphenated tag rather than silently losing its spaces.
    const pasted = e.clipboardData.getData("text");
    if (normalizeTag(pasted) === null) return;
    e.preventDefault();
    commit(pasted);
  }

  return (
    <HubPopover open={open} onClose={onClose} width={392} ariaLabel={t("workspace:tagsPopoverTitle")}>
      <div className="flex flex-col gap-3" style={{ padding: "16px 18px 18px" }}>
        {/* a. Header: what this is, that it saves itself, and a way out. */}
        <div className="flex items-center gap-2.5">
          <span
            className="grid shrink-0 place-items-center"
            style={{ width: 19, height: 22, color: "var(--hub-blue)" }}
          >
            <TagIcon size={19} />
          </span>
          <h3 className="text-[16px] font-bold" style={{ color: "var(--hub-text)" }}>
            {t("workspace:tagsPopoverTitle")}
          </h3>
          <span className="text-[11px]" style={{ color: "var(--hub-muted)" }}>
            {t("workspace:tagsPopoverSaved")}
          </span>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("workspace:tagsPopoverClose")}
            className="ml-auto grid h-7 w-7 place-items-center rounded-lg text-base hover:bg-[var(--hub-surface-hover)]"
            style={{ color: "var(--hub-muted)" }}
          >
            &#10005;
          </button>
        </div>

        {/* b. One control: the chips the user has, and the field that adds the next one. */}
        <div>
          <div
            onClick={() => inputRef.current?.focus()}
            className="flex flex-wrap items-center gap-1.5"
            style={{
              minHeight: 46,
              padding: 8,
              borderRadius: 10,
              border: "1px solid var(--hub-field-border)",
              background: "var(--hub-surface)",
              cursor: "text",
            }}
          >
            {tags.map((tag) => (
              <span
                key={tag}
                className="inline-flex items-center text-[12.5px] font-medium"
                style={{
                  height: 28,
                  padding: "0 4px 0 10px",
                  borderRadius: 8,
                  background: "rgba(47,107,237,.16)",
                  border: "1px solid rgba(47,107,237,.35)",
                  color: "var(--hub-blue-text)",
                }}
              >
                {tag}
                <button
                  type="button"
                  onClick={() => onRemove(tag)}
                  aria-label={t("workspace:tagsRemove")}
                  className="ml-0.5 grid place-items-center rounded-md hover:bg-[rgba(15,23,42,.1)] hover:text-white dark:hover:bg-[rgba(255,255,255,.12)]"
                  style={{ width: 22, height: 22, fontSize: 15 }}
                >
                  &#10005;
                </button>
              </span>
            ))}
            <input
              ref={inputRef}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={onKeyDown}
              onPaste={onPaste}
              aria-label={t("workspace:tagsInputLabel")}
              placeholder={t("workspace:tagsInputPlaceholder")}
              className="min-w-24 flex-1 bg-transparent text-[12.5px] outline-none"
              style={{ height: 26, color: "var(--hub-text)" }}
            />
          </div>
          <p className="mt-1.5 text-[11.5px]" style={{ color: "var(--hub-muted)" }}>
            {t("workspace:tagsInputHint")}
          </p>
        </div>

        {/* c. Divider. */}
        <div style={{ height: 1, background: "var(--hub-divider)" }} />

        {/* d. What the machine thought, offered rather than applied. */}
        <div>
          <div className="flex items-center">
            <span
              className="text-[11px] font-bold uppercase"
              style={{ letterSpacing: ".08em", color: "var(--hub-muted)" }}
            >
              {t("workspace:tagsSuggestedLabel")}
            </span>
            {suggested.length > 0 && (
              <span className="ml-auto text-[11px]" style={{ color: "var(--hub-muted-2)" }}>
                {t("workspace:tagsSuggestedLeft", { count: suggested.length })}
              </span>
            )}
          </div>

          {suggested.length === 0 ? (
            <p className="mt-2 text-[12px]" style={{ color: "var(--hub-muted-2)" }}>
              {t("workspace:tagsSuggestedDone")}
            </p>
          ) : (
            <div className="mt-2 flex flex-wrap gap-1.5">
              {suggested.map((tag) => (
                <span
                  key={tag}
                  className="inline-flex items-center"
                  style={{
                    height: 26,
                    borderRadius: 7,
                    border: "1px dashed var(--hub-hint-border)",
                  }}
                >
                  <button
                    type="button"
                    onClick={() => onAdd(tag)}
                    title={t("workspace:tagsSuggestedAdd")}
                    className="inline-flex h-full items-center gap-1 rounded-l-[7px] px-2 hover:bg-[rgba(15,23,42,.07)] dark:hover:bg-[rgba(255,255,255,.07)]"
                  >
                    <span className="text-[11px]" style={{ color: "var(--hub-blue)" }}>
                      +
                    </span>
                    <span className="text-[12.5px]" style={{ color: "var(--hub-text-2)" }}>
                      {tag}
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => onDismiss(tag)}
                    title={t("workspace:tagsSuggestedDismiss")}
                    aria-label={t("workspace:tagsSuggestedDismiss")}
                    className="mr-1 grid place-items-center rounded text-[var(--hub-muted-2)] hover:bg-[rgba(15,23,42,.08)] hover:text-[var(--hub-red-text)] dark:hover:bg-[rgba(255,255,255,.08)]"
                    style={{ width: 18, height: 18, fontSize: 10 }}
                  >
                    &#10005;
                  </button>
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </HubPopover>
  );
}
