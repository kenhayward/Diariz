"""Shared REST helpers for the transcription-bench tools. Stdlib only.

The token may be passed with --token or, preferably, in the DIARIZ_TOKEN environment
variable so it does not land in shell history. A personal API token (dz_api_...) with
ReadWrite scope is what the write paths need; the read-only tools work with either scope.
"""
import json
import os
import sys
import urllib.error
import urllib.request


def add_common_args(parser) -> None:
    """Register --base and --token on an argparse parser."""
    parser.add_argument("--base", required=True, help="instance root, e.g. http://192.168.1.129:8080")
    parser.add_argument("--token", default=None,
                        help="personal API token (dz_api_...); defaults to $DIARIZ_TOKEN")


def resolve(args) -> tuple[str, str]:
    """Return (base, token), preferring the environment for the token."""
    token = args.token or os.environ.get("DIARIZ_TOKEN")
    if not token:
        sys.exit("no token: pass --token or set DIARIZ_TOKEN")
    return args.base.rstrip("/"), token


def request(method: str, url: str, token: str, body=None, content_type=None, timeout=120):
    """One REST call. Returns (status, parsed-json-or-bytes); HTTP errors are returned,
    not raised, so a caller can report them per item instead of aborting a whole run."""
    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    if content_type:
        req.add_header("Content-Type", content_type)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            return resp.status, (json.loads(raw) if raw and raw[:1] in (b"{", b"[") else raw)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def get_json(base: str, path: str, token: str, timeout: int = 60):
    """GET that raises on anything but 200 - for calls where failure is fatal."""
    status, data = request("GET", f"{base}{path}", token, timeout=timeout)
    if status != 200:
        sys.exit(f"GET {path} failed: HTTP {status}")
    return data


def list_recordings(base: str, token: str) -> list:
    """The caller's recordings, as a list regardless of envelope shape."""
    data = get_json(base, "/api/recordings", token)
    return data if isinstance(data, list) else data.get("items", [])


# Benchmark recordings are titled with this prefix. Cleanup matches on Recording.Title and
# never on Name: the summariser overwrites Name with a generated title, so a name-based
# filter would stop matching the moment summarisation ran.
MARKER = "floor-bench"


def bench_recordings(base: str, token: str) -> list:
    return [r for r in list_recordings(base, token) if str(r.get("title", "")).startswith(MARKER)]
