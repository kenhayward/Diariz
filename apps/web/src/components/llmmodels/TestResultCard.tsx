import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { usageFilterToParams } from "../llmusage/usageFilterParams";
import type { LlmTestOutcome } from "../../lib/types";
import { PARAMETERS, type ParameterValue } from "./parameterSchema";

interface Props {
  result: LlmTestOutcome;
  /// i18n key for the call group this ran as, named in the verdict line.
  group: string;
  /// What Timeout currently resolves to here, so the timeout fix can say what it is raising FROM.
  resolvedTimeoutSeconds: number;
  /// Applies a one-click fix to the open tab's layer. `null` means omit, never undefined - see
  /// `ParameterValue`.
  onFix: (fix: { key: string; value: ParameterValue }) => void;
  /// The stored endpoint and model name, used to reproduce the call outside Diariz.
  apiBase: string;
  modelName: string;
  onRetry: () => void;
  /// Given when the card is hosted somewhere a route change would be wrong - inside the settings modal,
  /// where navigating drops the admin out of the modal and, in the desktop shell, out of the app. Absent
  /// on the standalone page, where an ordinary link is right.
  onOpenUsageLog?: (query: string) => void;
}

/// The exact request, as a command that can be pasted into a terminal.
///
/// The key is a PLACEHOLDER, and not by omission: the browser is never given the stored key - it is
/// write-only over the API - so there is nothing here to leak even if this were careless. Naming
/// `$LLM_API_KEY` rather than dropping the header entirely means the command fails for an obvious reason
/// (an unset variable) instead of an obscure one (a 401 the admin then has to diagnose separately).
const LINK = "text-indigo-600 hover:underline dark:text-indigo-400";

export function toCurl(apiBase: string, requestBodyJson: string): string {
  const url = `${apiBase.replace(/\/+$/, "")}/chat/completions`;
  const body = requestBodyJson.split("'").join(`'\\''`);
  return [
    `curl -N ${url} \\`,
    `  -H "Content-Type: application/json" \\`,
    `  -H "Authorization: Bearer $LLM_API_KEY" \\`,
    `  -d '${body}'`,
  ].join("\n");
}

/// The outcome of one test call: a verdict strip, the numbers, and - on a failure - the endpoint's own
/// words plus the single change that would address them.
///
/// The fix buttons are the reason this component earns its place. "Omit" is the least obvious of the three
/// parameter states and the hardest to discover, and the moment an administrator needs it is exactly the
/// moment an endpoint has just rejected a parameter by name. Offering it there, on that row, is what makes
/// the state legible at all.
export default function TestResultCard({
  result, group, resolvedTimeoutSeconds, onFix, apiBase, modelName, onRetry, onOpenUsageLog,
}: Props) {
  const { t } = useTranslation("account");
  const [copied, setCopied] = useState<string | null>(null);

  async function copy(what: string, text: string) {
    await navigator.clipboard?.writeText(text);
    setCopied(what);
  }

  const usageQuery = usageFilterToParams({ kinds: ["AdminTest"], models: [modelName] });
  const usageLink = `/admin/llm-usage?${usageQuery}`;

  const seconds = (ms: number) => `${(ms / 1000).toFixed(2)} s`;
  const dash = t("llmTestNotMeasured");

  /// Completion tokens over the wall-clock duration - the same definition as the usage log's column.
  /// Dividing TOTAL tokens instead would count the prompt the model only read, inflating a local model's
  /// throughput by more than an order of magnitude on a long transcript.
  const tokensPerSecond =
    result.completionTokens !== null && result.durationMs > 0
      ? (result.completionTokens / (result.durationMs / 1000)).toFixed(1)
      : null;

  const cutOff = result.finishReason === "length";
  const offending = result.offendingParameter;
  const offendingLabel = PARAMETERS.find((p) => p.key === offending)?.label;

  return (
    <div className="overflow-hidden rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div
        className={`flex items-baseline justify-between gap-2 border-b border-gray-100 px-3 py-2 dark:border-gray-800/60 ${
          result.ok ? "bg-green-50 dark:bg-green-500/10" : "bg-red-50 dark:bg-red-500/10"
        }`}
      >
        <div className="flex items-center gap-2 text-xs">
          <span
            className={`size-2 shrink-0 rounded-full ${result.ok ? "bg-green-600 dark:bg-green-500" : "bg-red-600 dark:bg-red-500"}`}
          />
          <span className={`font-semibold ${result.ok ? "text-green-700 dark:text-green-500" : "text-red-600 dark:text-red-400"}`}>
            {result.ok ? t("llmUsageOutcomeSuccess") : t("llmUsageOutcomeFailure")}
          </span>
          <span className="text-gray-500 dark:text-gray-400">
            {[result.httpStatus ?? result.errorKind, t(group)].filter(Boolean).join(" · ")}
          </span>
          {cutOff && (
            <span className="rounded border border-amber-300 bg-amber-50 px-1.5 text-[10px] font-semibold text-amber-700 dark:border-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
              {t("llmTestCutOff")}
            </span>
          )}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-px bg-gray-100 dark:bg-gray-800/60">
        <Stat caption={t("llmTestFirstToken")} value={result.ttftMs === null ? dash : seconds(result.ttftMs)} />
        <Stat
          caption={result.errorKind === "Timeout" ? t("llmTestGaveUpAfter") : t("llmTestDuration")}
          value={seconds(result.durationMs)}
          danger={result.errorKind === "Timeout"}
        />
        {result.ok && (
          <>
            <Stat caption={t("llmTestTokensPerSecond")} value={tokensPerSecond ?? dash} />
            <Stat
              caption={t("llmTestTotalTokens")}
              value={result.totalTokens === null ? dash : result.totalTokens.toLocaleString()}
            />
          </>
        )}
      </div>

      {result.ok && (result.promptTokens !== null || result.completionTokens !== null) && (
        <p className="border-t border-gray-100 px-3.5 py-2 text-[11px] tabular-nums text-gray-500 dark:border-gray-800/60 dark:text-gray-400">
          {t("llmUsageColPromptTokens")} {result.promptTokens?.toLocaleString() ?? dash}
          {"   "}
          {t("llmUsageColCompletionTokens")} {result.completionTokens?.toLocaleString() ?? dash}
          {"   "}
          {t("llmUsageColReasoningTokens")} {result.reasoningTokens?.toLocaleString() ?? dash}
        </p>
      )}

      <div className="border-t border-gray-100 px-3.5 py-2.5 dark:border-gray-800/60">
        <h4 className="text-[10px] font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500">
          {result.ok ? t("llmTestResponse") : t("llmTestWhatCameBack")}
        </h4>
        {result.ok ? (
          <p className="mt-1 max-h-[132px] overflow-auto text-xs leading-relaxed text-gray-700 dark:text-gray-300">
            {result.response}
          </p>
        ) : (
          <p className="mt-1 max-h-[132px] overflow-auto font-mono text-[11px] leading-relaxed text-red-600 dark:text-red-400">
            {result.message}
          </p>
        )}

        {!result.ok && result.errorKind === "Timeout" && (
          <Callout
            text={t("llmTestFixTimeoutHint", { seconds: resolvedTimeoutSeconds })}
            action={t("llmTestFixTimeout", { seconds: 600 })}
            onClick={() => onFix({ key: "timeout_seconds", value: 600 })}
          />
        )}

        {!result.ok && offending && (
          <Callout
            danger
            text={t("llmTestFixOmitHint", { parameter: offendingLabel ? t(offendingLabel) : offending })}
            action={t("llmTestFixOmit", { parameter: offendingLabel ? t(offendingLabel) : offending })}
            // null, not undefined: "stop sending this", not "let a lower layer decide" - which would put
            // the rejected value straight back on the wire.
            onClick={() => onFix({ key: offending, value: null })}
          />
        )}
      </div>

      <div className="flex flex-wrap items-center gap-3 border-t border-gray-100 px-3.5 py-2 text-[11.5px] dark:border-gray-800/60">
        <button type="button" onClick={() => copy("curl", toCurl(apiBase, result.requestBodyJson))} className={LINK}>
          {copied === "curl" ? t("llmTestCopied") : t("llmTestCopyCurl")}
        </button>
        <button
          type="button"
          onClick={() => copy("json", JSON.stringify(result, null, 2))}
          className={LINK}
        >
          {copied === "json" ? t("llmTestCopied") : t("llmTestRawJson")}
        </button>
        {result.ok ? (
          // Deep-links to this model's test calls rather than the whole week, which is what the label
          // promises. Filtered on the model NAME because that is what LlmCalls records - the snapshot, not
          // the id - so a renamed model's older tests stay under their old name.
          onOpenUsageLog ? (
            <button type="button" onClick={() => onOpenUsageLog(usageQuery)} className={LINK}>
              {t("llmTestOpenUsage")}
            </button>
          ) : (
            <Link to={usageLink} className={LINK}>
              {t("llmTestOpenUsage")}
            </Link>
          )
        ) : (
          <button type="button" onClick={onRetry} className={LINK}>
            {t("llmTestRetry")}
          </button>
        )}
      </div>
    </div>
  );
}

function Stat({ caption, value, danger }: { caption: string; value: string; danger?: boolean }) {
  return (
    <div className="bg-white px-3.5 py-2 dark:bg-gray-900">
      <div className="text-[10px] uppercase tracking-wider text-gray-400 dark:text-gray-500">{caption}</div>
      <div
        className={`text-[15px] font-semibold tabular-nums ${
          danger ? "text-red-600 dark:text-red-400" : "text-gray-900 dark:text-gray-100"
        }`}
      >
        {value}
      </div>
    </div>
  );
}

function Callout({
  text, action, onClick, danger,
}: { text: string; action: string; onClick: () => void; danger?: boolean }) {
  return (
    <div className="mt-2 rounded-r-md border border-l-2 border-gray-200 border-l-indigo-500 bg-slate-50 px-3 py-2 dark:border-gray-800 dark:border-l-indigo-400 dark:bg-gray-950/60">
      <p className="text-[11px] text-gray-600 dark:text-gray-400">{text}</p>
      <button
        type="button"
        onClick={onClick}
        className={`mt-1.5 rounded-md border px-2 py-1 text-[11.5px] ${
          danger
            ? "border-red-300 text-red-600 dark:border-red-900 dark:text-red-400"
            : "border-indigo-300 text-indigo-700 dark:border-indigo-400/50 dark:text-indigo-300"
        }`}
      >
        {action}
      </button>
    </div>
  );
}
