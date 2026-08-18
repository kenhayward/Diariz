import { LLM_CALL_KINDS, type LlmCallKind } from "../../lib/types";
import { defaultUsageFilter, type UsageFilterState } from "./UsageFilterBar";

/// Reading and writing the usage log's filter as a query string, so it can be linked to.
///
/// The rule throughout is that a BAD link degrades to a broader view, never to an empty one or an error.
/// These links are hand-edited, pasted into chat clients that truncate them, and go stale the moment a
/// model is renamed - and "there is no usage" is a far more misleading answer than "here is the usual
/// week", because it looks like a fact about the platform rather than about the link.

const OUTCOMES = ["ok", "failed", "all"] as const;

function list(params: URLSearchParams, key: string): string[] {
  const raw = params.get(key);
  if (!raw) return [];
  return raw.split(",").map((v) => v.trim()).filter(Boolean);
}

/// An ISO timestamp, or null when the value is not one. Deliberately strict: an unparseable date reaching
/// the API as `from` filters the whole table away.
function timestamp(params: URLSearchParams, key: string): string | null {
  const raw = params.get(key);
  if (!raw) return null;
  const parsed = new Date(raw);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

export function usageFilterFromParams(params: URLSearchParams, now: Date = new Date()): UsageFilterState {
  const base = defaultUsageFilter(now);

  const from = timestamp(params, "from");
  const to = timestamp(params, "to");

  const outcome = params.get("outcome");
  const kinds = list(params, "kinds").filter((k): k is LlmCallKind =>
    (LLM_CALL_KINDS as readonly string[]).includes(k),
  );

  return {
    // A range named in the URL is by definition not one of the presets.
    preset: from || to ? "custom" : base.preset,
    from: from ?? base.from,
    to: to ?? base.to,
    userIds: list(params, "userIds"),
    kinds,
    // Model names are whatever the endpoint calls them - slashes, @ and : are all normal - so they are
    // taken verbatim rather than validated against anything.
    models: list(params, "models"),
    outcome: (OUTCOMES as readonly string[]).includes(outcome ?? "")
      ? (outcome as UsageFilterState["outcome"])
      : base.outcome,
  };
}

/// The query string for a link INTO the usage log. Only what the caller names is written, so the page's
/// own defaults still decide everything else.
export function usageFilterToParams(filter: {
  kinds?: readonly string[];
  models?: readonly string[];
  outcome?: string;
}): string {
  const params = new URLSearchParams();
  if (filter.kinds?.length) params.set("kinds", filter.kinds.join(","));
  if (filter.models?.length) params.set("models", filter.models.join(","));
  if (filter.outcome) params.set("outcome", filter.outcome);
  return params.toString();
}
