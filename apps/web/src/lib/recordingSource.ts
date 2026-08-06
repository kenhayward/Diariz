import type { TFunction } from "i18next";
import type { RecordingSource } from "./types";

/// How a recording was captured, as a label for the row's source line.
///
/// Keys are `workspace:`-prefixed because every caller passes the `t` from an un-namespaced
/// `useTranslation()` - dropping the prefix would resolve against whatever namespace the caller happened
/// to load. Microphone is the fallback as well as a case: `RecordingSource` is append-only on the server
/// (ints in Postgres), so a build can be handed a source it has no label for.
export function sourceLabel(s: RecordingSource, t: TFunction): string {
  if (s === "System") return t("workspace:sourceSystem");
  if (s === "Combined") return t("workspace:sourceCombined");
  if (s === "Upload") return t("workspace:sourceUpload");
  return t("workspace:sourceMicrophone");
}
