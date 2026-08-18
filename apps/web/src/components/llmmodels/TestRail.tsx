import { useTranslation } from "react-i18next";
import type { LlmTestOutcome } from "../../lib/types";
import TestResultCard from "./TestResultCard";
import type { ParameterValue } from "./parameterSchema";
import type { ResolvedRequest } from "./requestPreview";

export type TestState =
  | { status: "idle" }
  | { status: "running" }
  | { status: "done"; result: LlmTestOutcome };

interface Props {
  /// i18n key for the open call group.
  group: string;
  preview: ResolvedRequest;
  test: TestState;
  /// Null when the model has not been saved yet, which is when there is nothing to test against.
  onRun: (() => void) | null;
  onFix: (fix: { key: string; value: ParameterValue }) => void;
}

/// The drawer's right-hand column: run a real call, see what came back, and see the exact body that would
/// be sent as it is typed.
export default function TestRail({ group, preview, test, onRun, onFix }: Props) {
  const { t } = useTranslation("account");

  const runLabel =
    test.status === "running"
      ? t("llmTestRunning")
      : test.status === "done"
        ? t("llmTestRunAgain")
        : t("llmTestRun");

  return (
    <aside className="min-w-0 overflow-auto border-t border-gray-200 bg-slate-50 lg:border-l lg:border-t-0 dark:border-gray-800 dark:bg-gray-950/60">
      <div className="flex items-start justify-between gap-3 border-b border-gray-200 px-4 py-2.5 dark:border-gray-800">
        <div className="min-w-0">
          <h3 className="text-[11px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">
            {t("llmTestTitle")}
          </h3>
          <p className="truncate text-[11px] text-gray-400 dark:text-gray-500">
            {t("llmTestSubtitle", { group: t(group) })}
          </p>
        </div>
        {onRun ? (
          <button
            type="button"
            onClick={onRun}
            disabled={test.status === "running"}
            className="shrink-0 rounded-md border border-indigo-500 bg-indigo-50 px-3 py-1 text-xs font-medium text-indigo-700 disabled:opacity-60 dark:bg-indigo-600/20 dark:text-indigo-100"
          >
            {runLabel}
          </button>
        ) : (
          <span className="shrink-0 text-[11px] text-gray-400 dark:text-gray-500">{t("llmTestSaveFirst")}</span>
        )}
      </div>

      <div className="space-y-3 p-3">
        {test.status === "running" && (
          <p className="flex items-center gap-2 px-1 text-[11.5px] text-gray-500 dark:text-gray-400">
            <span className="size-[7px] animate-pulse rounded-full bg-indigo-500" />
            {t("llmTestWaiting")}
          </p>
        )}

        {test.status === "done" && (
          <TestResultCard
            result={test.result}
            group={group}
            resolvedTimeoutSeconds={preview.flags.timeoutSeconds}
            onFix={onFix}
          />
        )}

        <div className="overflow-hidden rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <div className="flex items-baseline justify-between border-b border-gray-100 px-3.5 py-2 dark:border-gray-800/60">
            <h3 className="text-[11px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">
              {t("llmTestRequestSent")}
            </h3>
            <span className="text-[10.5px] text-gray-400 dark:text-gray-500">
              {t("llmTestParamCount", { count: Object.keys(preview.body).length - 1 })}
            </span>
          </div>
          <pre
            data-testid="request-preview"
            className="max-h-[210px] overflow-auto px-3.5 py-2.5 text-[11px] leading-relaxed text-gray-600 dark:text-gray-400"
          >
            {JSON.stringify(preview.body, null, 2)}
          </pre>
          {/* Outside the body on purpose: these four govern Diariz, not the request, and an endpoint never
              receives them. Listing them inside would make the panel above a claim about the wire that is
              not true. */}
          <p className="border-t border-gray-100 px-3.5 py-2 text-[11px] text-gray-400 dark:border-gray-800/60 dark:text-gray-500">
            {t("llmTestClientFlags", {
              timeout: preview.flags.timeoutSeconds,
              tools: preview.flags.toolsSupported ? t("llmParamOn") : t("llmParamOff"),
              images: preview.flags.imagesSupported ? t("llmParamOn") : t("llmParamOff"),
            })}
          </p>
        </div>
      </div>
    </aside>
  );
}
