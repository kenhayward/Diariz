# Security Policy

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report vulnerabilities privately through GitHub's **[private vulnerability reporting](https://github.com/kenhayward/Diariz/security/advisories/new)**
(the **Report a vulnerability** button under the repository's **Security** tab). This keeps the details
confidential until a fix is available.

Please include, as far as you can:

- the component affected (API, worker, web, or desktop) and the version (see `version.json` / `GET /health`),
- steps to reproduce or a proof of concept,
- the impact you believe it has.

You should get an acknowledgement within a few days. Because this is a small project, please allow a
reasonable window for a fix before any public disclosure.

## Supported versions

This project is pre-1.0 and ships from `main`. Only the **latest released version** receives security
fixes; there are no backports to older versions. Always run the most recent release.

## Operating Diariz securely

A few operator responsibilities are worth calling out — none are bugs, but getting them wrong exposes a
deployment:

- **Change the seeded admin.** Set `SEED_EMAIL` / `SEED_PASSWORD` before first run; never ship the
  `admin@example.com` / `ChangeMe123!` defaults to production.
- **Set strong shared secrets.** Provide your own `JWT_KEY` (32+ random chars) and `CALLBACK_SECRET`; keep
  MinIO/S3, SMTP, and LLM credentials in `.env` (which is git-ignored), never in tracked files.
- **The worker callback** (`internal/transcriptions/*`) is authenticated by `CALLBACK_SECRET`, not JWT —
  keep the worker on a trusted network and do not expose that route publicly.
- **Third-party model terms** and the LLM endpoint you configure are your responsibility — see the
  Licensing section of the [README](README.md).
