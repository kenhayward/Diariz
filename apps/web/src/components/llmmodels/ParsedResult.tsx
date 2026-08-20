import { useTranslation } from "react-i18next";
import type { ParsedAction, ParsedSummary, ParsedTag } from "../../lib/types";

interface Props {
  parsedKind: string;
  parsedJson: string;
}

function parse<T>(json: string): T | null {
  try {
    return JSON.parse(json) as T;
  } catch {
    // A display concern must never take the drawer down, and the raw reply is shown alongside this anyway.
    return null;
  }
}

/// The model's reply as the PIPELINE understood it - not as the model wrote it.
///
/// This is what makes the test panel answer "is this model any good at this job" rather than only "does
/// this endpoint answer". An empty extraction is the state worth designing for: the call succeeded, spent
/// tokens, and would still have left the recording with no tags. It gets a sentence, not an empty box.
export default function ParsedResult({ parsedKind, parsedJson }: Props) {
  const { t } = useTranslation("account");

  if (parsedKind === "Summary") {
    const summary = parse<ParsedSummary>(parsedJson);
    if (!summary?.summary) return <Nothing kind={t("llmTestParsedSummary")} />;
    return (
      <div className="space-y-1">
        {summary.name && (
          <p className="text-[11px] text-gray-500 dark:text-gray-400">
            {t("llmTestSummaryName")}:{" "}
            <span className="font-medium text-gray-800 dark:text-gray-200">{summary.name}</span>
          </p>
        )}
        <p className="text-xs leading-relaxed text-gray-700 dark:text-gray-300">{summary.summary}</p>
      </div>
    );
  }

  if (parsedKind === "Tags") {
    const tags = parse<ParsedTag[]>(parsedJson);
    if (!tags?.length) return <Nothing kind={t("llmTestParsedTags")} />;
    return (
      <ul className="flex flex-wrap gap-1.5">
        {tags.map((tag) => (
          <li
            key={tag.tag}
            className="flex items-baseline gap-1.5 rounded-full border border-gray-200 px-2 py-0.5 text-[11px] dark:border-gray-700"
          >
            <span className="text-gray-800 dark:text-gray-200">{tag.tag}</span>
            <span className="tabular-nums text-gray-400 dark:text-gray-500">{tag.weight.toFixed(2)}</span>
          </li>
        ))}
      </ul>
    );
  }

  if (parsedKind === "Actions") {
    const actions = parse<ParsedAction[]>(parsedJson);
    if (!actions?.length) return <Nothing kind={t("llmTestParsedActions")} />;
    return (
      // Its own scroll container: a long meeting's actions must not push the drawer sideways.
      <div className="overflow-x-auto">
        <table className="w-full text-left text-[11px]">
          <thead className="text-gray-400 dark:text-gray-500">
            <tr>
              <th className="py-1 pr-2 font-medium">{t("llmTestActionTask")}</th>
              <th className="py-1 pr-2 font-medium">{t("llmTestActionOwner")}</th>
              <th className="py-1 font-medium">{t("llmTestActionDue")}</th>
            </tr>
          </thead>
          <tbody className="text-gray-700 dark:text-gray-300">
            {actions.map((action, i) => (
              <tr key={`${action.text}-${i}`} className="border-t border-gray-100 dark:border-gray-800/60">
                <td className="py-1 pr-2">{action.text}</td>
                <td className="py-1 pr-2 text-gray-500 dark:text-gray-400">{action.actor}</td>
                <td className="py-1 text-gray-500 dark:text-gray-400">{action.deadline}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  }

  // An unrecognised kind means the server grew a fourth parsed shape and this component was not updated.
  // Render nothing rather than a wrong label - the raw reply is still shown alongside.
  return null;
}

function Nothing({ kind }: { kind: string }) {
  const { t } = useTranslation("account");
  return (
    <p className="rounded-r-md border border-l-2 border-amber-200 border-l-amber-500 bg-amber-50 px-2.5 py-1.5 text-[11px] text-amber-800 dark:border-amber-900 dark:border-l-amber-500 dark:bg-amber-500/10 dark:text-amber-300">
      {t("llmTestParsedNothing", { kind })}
    </p>
  );
}
