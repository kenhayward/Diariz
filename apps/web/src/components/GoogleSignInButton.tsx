/// "Sign in with Google" button. Default (web) is a plain link (full-page navigation) to the API's
/// server-side OAuth start endpoint, so the state cookie and redirect to Google work without any
/// XHR/CORS. When `onClick` is given (desktop shell) it renders a button instead - the desktop flow
/// opens the system browser via IPC, since an in-window navigation to Google would be ejected by the
/// shell and lose the flow.
const CLASS =
  "flex w-full items-center justify-center gap-2 rounded border py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800";

export default function GoogleSignInButton({ label, onClick }: { label: string; onClick?: () => void }) {
  if (onClick) {
    return (
      <button type="button" onClick={onClick} className={CLASS}>
        <GoogleLogo />
        {label}
      </button>
    );
  }
  return (
    <a href="/api/auth/google/start" className={CLASS}>
      <GoogleLogo />
      {label}
    </a>
  );
}

/// Google's "G" mark (official four-colour logo), rendered inline so it needs no asset.
function GoogleLogo() {
  return (
    <svg viewBox="0 0 18 18" className="h-4 w-4" aria-hidden="true">
      <path fill="#4285F4" d="M17.64 9.2c0-.64-.06-1.25-.16-1.84H9v3.48h4.84a4.14 4.14 0 0 1-1.8 2.72v2.26h2.92c1.7-1.57 2.68-3.88 2.68-6.62z" />
      <path fill="#34A853" d="M9 18c2.43 0 4.47-.8 5.96-2.18l-2.92-2.26c-.81.54-1.84.86-3.04.86-2.34 0-4.32-1.58-5.03-3.7H.96v2.33A9 9 0 0 0 9 18z" />
      <path fill="#FBBC05" d="M3.97 10.72a5.4 5.4 0 0 1 0-3.44V4.95H.96a9 9 0 0 0 0 8.1l3.01-2.33z" />
      <path fill="#EA4335" d="M9 3.58c1.32 0 2.5.45 3.44 1.35l2.58-2.58C13.47.9 11.43 0 9 0A9 9 0 0 0 .96 4.95l3.01 2.33C4.68 5.16 6.66 3.58 9 3.58z" />
    </svg>
  );
}
