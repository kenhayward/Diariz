import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { ApiReferenceReact } from "@scalar/api-reference-react";
import "@scalar/api-reference-react/style.css";
import { api } from "../lib/api";

/// Full-page, in-app API reference (Scalar) backed by the curated, authed OpenAPI document. Reached at
/// /developers/api behind the app login, so it's signed-in users only.
export default function ApiReference() {
  const { t } = useTranslation("account");
  const { data: spec } = useQuery({ queryKey: ["openapi-doc"], queryFn: api.getOpenApiDocument });

  return (
    <div className="flex h-screen flex-col">
      <div className="flex items-center gap-3 border-b bg-white px-4 py-2 text-sm dark:border-gray-700 dark:bg-gray-900">
        <Link to="/" className="text-indigo-600 hover:underline dark:text-indigo-400">
          ← {t("apiBackToApp")}
        </Link>
        <span className="font-medium text-gray-700 dark:text-gray-200">{t("apiReferenceTitle")}</span>
      </div>
      <div className="min-h-0 flex-1 overflow-auto">
        {spec ? <ApiReferenceReact configuration={{ content: spec }} /> : null}
      </div>
    </div>
  );
}
