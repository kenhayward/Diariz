import { useEffect } from "react";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { renderMarkdown } from "../lib/markdown";
import { APP_VERSION, BUILD_COMMIT } from "../lib/version";
import { TAGLINE, GITHUB_URL, COPYRIGHT, LICENSE, CAPABILITIES } from "../lib/appInfo";

/// About box: app identity, version, what it does, links, third-party disclaimers, and copyright.
export default function AboutModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("aboutAria")}
        className="max-h-[85vh] w-[60vw] min-w-80 max-w-5xl overflow-y-auto rounded-lg border bg-white p-6 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Identity, with the links opposite it rather than buried under the disclaimers. `items-center`
            rather than `items-start` so the link row sits level with the logo: the identity block is the
            same 40px height as the icon, so centring against it centres against the icon. */}
        <div className="flex flex-wrap items-center justify-between gap-4">
          <div className="flex min-w-0 items-center gap-3">
            <img src="/logo.png" alt="" className="h-10 w-auto" />
            <div>
              <h2 className="text-xl font-semibold dark:text-gray-100">Diariz</h2>
              <p className="text-sm text-gray-500 dark:text-gray-400">{TAGLINE}</p>
            </div>
          </div>
          {/* Help and the release notes are app routes, so they navigate IN PLACE. As `target="_blank"`
              they left the app for the system browser, where the user is not signed in and both pages sit
              behind the login - the same fault PanelModal and SettingsModal already record for the admin
              panels, missed here. Closing the dialog on the way keeps it from sitting over the page it
              just navigated to. GitHub is genuinely external and keeps its new tab. */}
          <div className="ml-auto flex flex-wrap items-center justify-end gap-4 text-sm">
            <Link
              to="/help"
              onClick={onClose}
              className="text-blue-600 hover:underline dark:text-blue-400"
            >
              {t("help:browseHelp")}
            </Link>
            <Link
              to="/release-notes"
              onClick={onClose}
              className="text-blue-600 hover:underline dark:text-blue-400"
            >
              {t("releaseNotesLink")}
            </Link>
            <a
              href={GITHUB_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="text-blue-600 hover:underline dark:text-blue-400"
            >
              {t("githubLink")}
            </a>
          </div>
        </div>

        <p className="mt-3 text-xs text-gray-500 dark:text-gray-400">
          {t("aboutVersion", { version: APP_VERSION })}
          {BUILD_COMMIT ? ` · ${BUILD_COMMIT}` : ""}
        </p>

        {/* Capabilities */}
        <div
          className="chat-md mt-4 space-y-2 text-sm text-gray-700 dark:text-gray-300 [&_strong]:font-semibold"
          dangerouslySetInnerHTML={{ __html: renderMarkdown(CAPABILITIES) }}
        />

        {/* Disclaimers */}
        <div className="mt-4 border-t pt-3 text-xs leading-relaxed text-gray-500 dark:border-gray-700 dark:text-gray-400">
          <p>
            Diariz is built on open-source software - including ASP.NET Core, React, react-i18next,
            @sentry/react, WhisperX, pyannote.audio, SpeechBrain, mediabunny, PostgreSQL/pgvector, Redis, MinIO/S3, MailKit/MimeKit,
            PdfPig, Open XML SDK, Markdig, Ical.Net, SkiaSharp, TipTap/ProseMirror, marked, DOMPurify, OpenIddict, and
            the Model Context Protocol C# SDK - each under its own licence.
          </p>
          <p className="mt-2">
            Speaker diarization uses the <strong>gated</strong> pyannote models
            (<code>speaker-diarization-3.1</code>, <code>segmentation-3.0</code>): they are
            <strong> MIT-licensed</strong> but gated - you must accept the model terms on Hugging Face and
            supply an access token (<code>HF_TOKEN</code>).
          </p>
          <p className="mt-2">
            Speaker identification stores <strong>voiceprints</strong> (SpeechBrain ECAPA embeddings) -
            biometric data. Only enrol people with their consent, and use “delete person” to erase a
            voiceprint when required. The ECAPA model is Apache-2.0 but trained on <strong>VoxCeleb</strong>,
            which is published for <strong>research / non-commercial</strong> use - review those terms before
            any commercial deployment.
          </p>
          <p className="mt-2">
            Extracting audio from a dropped video happens entirely in your browser using{" "}
            <strong>mediabunny</strong> (MPL-2.0) and the browser's own WebCodecs decoders. The video is
            never uploaded and never stored.
          </p>
          <p className="mt-2">
            Summaries and chat use an OpenAI-compatible LLM endpoint you configure; that provider's terms
            and privacy policy apply to any text you send to it.
          </p>
          <p className="mt-2">
            Optional <strong>Sign in with Google</strong> uses Google's OAuth 2.0 sign-in (via the
            Apache-2.0 <code>Google.Apis.Auth</code> library); Google's terms and privacy policy apply, and
            it is available only when the operator has configured it. You can also <strong>opt in</strong> to
            let Diariz read your <strong>Google Calendar</strong> (read-only); that access is revocable any
            time from Preferences.
          </p>
        </div>

        <div className="mt-4 flex items-center justify-between border-t pt-3 dark:border-gray-700">
          <p className="text-xs text-gray-500 dark:text-gray-400">
            {COPYRIGHT} · {LICENSE}
          </p>
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}
