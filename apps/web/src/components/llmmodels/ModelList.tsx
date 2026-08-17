import { useTranslation } from "react-i18next";
import type { LlmModel } from "../../lib/types";

interface Props {
  models: LlmModel[];
  /// Group key -> model id, so each row can say what it currently serves.
  assignments: Record<string, string>;
  defaultModelId: string | null;
  onEdit: (model: LlmModel) => void;
  onDelete: (model: LlmModel) => void;
}

import { ASSIGNABLE_GROUPS } from "./parameterSchema";

export default function ModelList({ models, assignments, defaultModelId, onEdit, onDelete }: Props) {
  const { t } = useTranslation("account");

  /// What this model is used for, so an administrator can see the consequence of editing or deleting it
  /// without cross-referencing the assignments panel below.
  function rolesOf(id: string): string[] {
    const roles = ASSIGNABLE_GROUPS.filter((g) => assignments[g.key] === id).map((g) => t(g.label));
    if (defaultModelId === id) roles.unshift(t("llmModelsRoleDefault"));
    return roles;
  }

  return (
    <table className="w-full text-sm">
      <thead className="text-left text-xs uppercase text-gray-500 dark:text-gray-400">
        <tr>
          <th className="py-1">{t("llmModelsColModel")}</th>
          <th className="py-1">{t("llmModelsColEndpoint")}</th>
          <th className="py-1">{t("llmModelsColContext")}</th>
          <th className="py-1">{t("llmModelsColKey")}</th>
          <th className="py-1">{t("llmModelsColUsedFor")}</th>
          <th className="py-1" />
        </tr>
      </thead>
      <tbody>
        {models.map((m) => {
          const roles = rolesOf(m.id);
          return (
            <tr key={m.id} className="border-t dark:border-gray-800">
              <td className="py-1.5 font-medium text-gray-800 dark:text-gray-100">{m.name}</td>
              <td className="py-1.5 text-gray-600 dark:text-gray-300">{m.apiBase}</td>
              <td className="py-1.5 text-gray-600 dark:text-gray-300">{m.contextLength.toLocaleString()}</td>
              <td className="py-1.5 text-gray-600 dark:text-gray-300">{m.hasApiKey ? t("llmModelsKeySet") : t("llmModelsKeyNone")}</td>
              <td className="py-1.5 text-gray-600 dark:text-gray-300">
                {roles.length ? roles.join(", ") : <span className="text-gray-400">{t("llmModelsUnused")}</span>}
              </td>
              <td className="py-1.5 text-right">
                <button
                  type="button"
                  onClick={() => onEdit(m)}
                  className="text-indigo-600 hover:underline dark:text-indigo-400"
                >
                  {t("llmModelsEdit")}
                </button>
                <button
                  type="button"
                  onClick={() => onDelete(m)}
                  className="ml-3 text-red-600 hover:underline dark:text-red-400"
                >
                  {t("llmModelsDelete")}
                </button>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
