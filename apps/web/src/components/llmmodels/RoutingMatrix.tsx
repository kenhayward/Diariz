import { useTranslation } from "react-i18next";
import type { LlmModel } from "../../lib/types";
import { ASSIGNABLE_GROUPS } from "./parameterSchema";

interface Props {
  models: LlmModel[];
  /// Group key -> model id. A group ABSENT from this map is not unrouted - it falls through to the
  /// platform default, which is what the "No model" row draws.
  assignments: Record<string, string>;
  defaultModelId: string | null;
  onRoute: (next: { defaultModelId: string | null; assignments: Record<string, string> }) => void;
  onEdit: (model: LlmModel) => void;
}

/// The routing grid: models down the side, call types across the top, one selection per column.
///
/// The last row is where the design earns its "one dot per column" claim. An unassigned call type has to
/// be drawn SOMEWHERE, and drawing it on the default model's row would assert an assignment that does not
/// exist - which then silently re-points that call type the next time the default moves. So there is a
/// row meaning "nothing explicit here": for a call type it deletes the entry (falling through to the
/// default), and for the Default column it clears the default (falling through to the server environment).
/// Every cell in it means the same thing one level up from its column.
export default function RoutingMatrix({ models, assignments, defaultModelId, onRoute, onEdit }: Props) {
  const { t } = useTranslation("account");

  /// The API replaces the whole routing set on every write, so every change sends both halves - a payload
  /// carrying only what changed would silently drop the rest.
  function route(group: string, modelId: string | null) {
    const next = { ...assignments };
    if (modelId) next[group] = modelId;
    else delete next[group];
    onRoute({ defaultModelId, assignments: next });
  }

  function setDefault(modelId: string | null) {
    onRoute({ defaultModelId: modelId, assignments });
  }

  const columns = [{ key: "__default__", label: t("llmModelsRoleDefault") }, ...ASSIGNABLE_GROUPS.map((g) => ({ key: g.key, label: t(g.column) }))];

  function selectedIn(columnKey: string): string | null {
    return columnKey === "__default__" ? defaultModelId : (assignments[columnKey] ?? null);
  }

  function choose(columnKey: string, modelId: string | null) {
    if (selectedIn(columnKey) === modelId) return;
    if (columnKey === "__default__") setDefault(modelId);
    else route(columnKey, modelId);
  }

  /// How many call types each model actually serves - the consequence of the grid above, stated as a
  /// number so it does not have to be counted by eye.
  function tally(model: LlmModel): number {
    return ASSIGNABLE_GROUPS.filter((g) => assignments[g.key] === model.id).length;
  }

  const grid = "grid grid-cols-[minmax(0,1fr)_repeat(7,86px)_128px] items-center";

  return (
    <div className="overflow-x-auto">
      <div className="min-w-[900px] overflow-hidden rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
        <div className={`${grid} items-end border-b border-gray-200 px-3.5 pb-2 pt-2.5 dark:border-gray-800`}>
          <span className="text-[10.5px] font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">
            {t("llmModelsColModel")}
          </span>
          {columns.map((c) => (
            <span
              key={c.key}
              className="px-1 text-center text-[10.5px] font-semibold leading-tight tracking-wide text-gray-500 dark:text-gray-400"
            >
              {c.label}
            </span>
          ))}
          <span />
        </div>

        {models.map((m) => (
          <div key={m.id} className={`${grid} border-b border-gray-100 px-3.5 py-3 dark:border-gray-800/60`}>
            <div className="min-w-0 pr-4">
              <div className="flex items-center gap-2">
                <span className="size-[7px] shrink-0 rounded-full bg-gray-400 dark:bg-gray-500" />
                <span className="truncate text-[13.5px] font-semibold tracking-tight text-gray-900 dark:text-gray-100">
                  {m.name}
                </span>
              </div>
              <p className="mt-[3px] truncate pl-[15px] text-[11px] tabular-nums text-gray-500 dark:text-gray-400">
                {m.apiBase} · {m.contextLength.toLocaleString()} ctx
              </p>
            </div>

            {columns.map((c) => (
              <Dot
                key={c.key}
                label={t("llmModelsRouteAria", { group: c.label, model: m.name })}
                selected={selectedIn(c.key) === m.id}
                onSelect={() => choose(c.key, m.id)}
              />
            ))}

            <div className="flex justify-end">
              <button
                type="button"
                onClick={() => onEdit(m)}
                className="whitespace-nowrap rounded-md border border-gray-300 px-2.5 py-1 text-[11.5px] dark:border-gray-700"
              >
                {t("llmModelsEdit")}
              </button>
            </div>
          </div>
        ))}

        <div className={`${grid} border-b border-gray-100 px-3.5 py-3 dark:border-gray-800/60`}>
          <div className="min-w-0 pr-4">
            <span className="pl-[15px] text-[13.5px] font-medium tracking-tight text-gray-500 dark:text-gray-400">
              {t("llmModelsNoModelRow")}
            </span>
            <p className="mt-[3px] truncate pl-[15px] text-[11px] text-gray-400 dark:text-gray-500">
              {t("llmModelsNoModelHint")}
            </p>
          </div>

          {columns.map((c) => (
            <Dot
              key={c.key}
              label={
                c.key === "__default__"
                  ? t("llmModelsUseEnvironment")
                  : t("llmModelsRouteDefaultAria", { group: c.label })
              }
              selected={selectedIn(c.key) === null}
              onSelect={() => choose(c.key, null)}
            />
          ))}
          <span />
        </div>

        <div className="flex flex-wrap items-center justify-between gap-2 px-3.5 py-2.5 text-[11.5px] text-gray-500 dark:text-gray-400">
          <span>{t("llmModelsRoutingFooter")}</span>
          <span className="tabular-nums">
            {models.map((m) => t("llmModelsTally", { count: tally(m), model: m.name })).join("  ·  ")}
          </span>
        </div>
      </div>
    </div>
  );
}

function Dot({ label, selected, onSelect }: { label: string; selected: boolean; onSelect: () => void }) {
  return (
    <div className="flex justify-center">
      <button
        type="button"
        role="radio"
        aria-checked={selected}
        aria-label={label}
        title={label}
        onClick={onSelect}
        className={`flex size-[22px] items-center justify-center rounded-full border transition-colors duration-100 ${
          selected
            ? "border-indigo-400 bg-indigo-600"
            : "border-gray-300 hover:border-indigo-400 hover:bg-indigo-600/20 dark:border-gray-700"
        }`}
      >
        {selected && <span className="size-[7px] rounded-full bg-white" />}
      </button>
    </div>
  );
}
