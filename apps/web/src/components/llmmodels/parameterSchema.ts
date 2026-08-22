/// The 15 parameters an administrator can set, in the order they appear in a panel.
///
/// The `key` values must match `LlmParameterLayers.ParameterNames` on the API exactly - the API rejects any
/// key it does not recognise, so a typo here becomes a 400 the admin cannot act on. `parameterSchema.test.ts`
/// pins the list against that contract.
export type ParameterKind = "number" | "integer" | "text" | "boolean";

/// One parameter's stored instruction. The three states are three different instructions, and the last two
/// are the ones easily confused:
///
///   `undefined` - Inherit. The key is absent from this layer, so the next layer down decides.
///   `null`      - Omit. The key is present with a null value, so the parameter is left out of the request
///                 body entirely. This is NOT the same as inheriting - it actively suppresses whatever a
///                 lower layer would have supplied.
///   a value     - the key is present with that value.
///
/// Returning a row to Inherit therefore has to emit `undefined`, not `null`. Emitting null would turn an
/// inherited 0.3 into an omitted parameter - a behaviour change the administrator never asked for, and one
/// they could not see afterwards without reading the JSON.
export type ParameterValue = string | number | boolean | null | undefined;

/// One group's worth of parameters. Never filled in with undefined placeholders: `layer[key]` returning
/// undefined IS the inherit state, and has to stay distinguishable from an explicit null.
export type ParameterLayer = Record<string, ParameterValue>;

export interface ParameterSpec {
  key: string;
  /// i18n key in the `account` namespace, not display text.
  label: string;
  kind: ParameterKind;
  min?: number;
  max?: number;
  /// i18n key for the note under the control. Kept short - the panel repeats it per group.
  hint?: string;
}

export const PARAMETERS: ParameterSpec[] = [
  { key: "temperature", label: "llmParamTemperature", kind: "number", min: 0, max: 2 },
  { key: "top_p", label: "llmParamTopP", kind: "number", min: 0, max: 1 },
  // -1 disables top-k on several servers, which is a deliberate choice rather than a mistake.
  { key: "top_k", label: "llmParamTopK", kind: "integer", min: -1 },
  { key: "repeat_penalty", label: "llmParamRepeatPenalty", kind: "number", min: 0, max: 2 },
  { key: "frequency_penalty", label: "llmParamFrequencyPenalty", kind: "number", min: -2, max: 2 },
  { key: "presence_penalty", label: "llmParamPresencePenalty", kind: "number", min: -2, max: 2 },
  // -1 means "no cap" on several servers - see LlmParameterLayers, where these two are the reason the
  // three-state design uses absence and null instead of a sentinel.
  { key: "max_tokens", label: "llmParamMaxTokens", kind: "integer", min: -1 },
  { key: "max_completion_tokens", label: "llmParamMaxCompletionTokens", kind: "integer", min: -1 },
  {
    key: "reasoning_effort",
    label: "llmParamReasoningEffort",
    kind: "text",
    // Free text on purpose: gpt-oss takes low/medium/high, qwen3 also takes xhigh, and the next model
    // will take something else again.
    hint: "llmParamReasoningEffortHint",
  },
  { key: "reasoning_enabled", label: "llmParamReasoningEnabled", kind: "boolean" },
  { key: "timeout_seconds", label: "llmParamTimeoutSeconds", kind: "integer", min: 1 },
  { key: "tools_supported", label: "llmParamToolsSupported", kind: "boolean" },
  { key: "images_supported", label: "llmParamImagesSupported", kind: "boolean" },
  // OCR only, and free text for a measured reason: one OCR model wants the terse "Text Recognition:",
  // another a full sentence, and swapping them narrows a model to a single region of the capture.
  { key: "ocr_prompt", label: "llmParamOcrPrompt", kind: "text", hint: "llmParamOcrPromptHint" },
  // Not a maximum - a calibration. Quality is NOT monotonic in resolution: measured on one capture the
  // best size ranged from 1288 to 2560 px across models, and one model degraded above its own best size.
  { key: "ocr_max_edge", label: "llmParamOcrMaxEdge", kind: "integer", min: 256, hint: "llmParamOcrMaxEdgeHint" },
];

export interface GroupSpec {
  key: string;
  /// i18n key in the `account` namespace, not display text. The full name, used in prose.
  label: string;
  /// Label for the editor's tab strip, where the full name would not fit.
  short: string;
  /// Label for the routing matrix's 86px column header, which wraps to two lines.
  column: string;
}

/// The parameter groups, in editing order. `ModelBase` is the model's own defaults rather than a call type,
/// which is why it is labelled differently and can never be assigned to.
///
/// `short` and `column` point at the full `label` for every group whose name already fits - only
/// MinutesAndFormulas is long enough to need its own wording, and duplicating eleven identical strings
/// across four catalogues would be eleven more chances for them to drift apart.
export const GROUPS: GroupSpec[] = [
  { key: "ModelBase", label: "llmGroupModelBase", short: "llmGroupModelBase", column: "llmGroupModelBase" },
  { key: "Tags", label: "llmGroupTags", short: "llmGroupTags", column: "llmGroupTags" },
  { key: "Actions", label: "llmGroupActions", short: "llmGroupActions", column: "llmGroupActions" },
  { key: "Summaries", label: "llmGroupSummaries", short: "llmGroupSummaries", column: "llmGroupSummaries" },
  {
    key: "MinutesAndFormulas",
    label: "llmGroupMinutesAndFormulas",
    short: "llmGroupShortMinutes",
    column: "llmGroupColumnMinutes",
  },
  { key: "Translation", label: "llmGroupTranslation", short: "llmGroupTranslation", column: "llmGroupTranslation" },
  { key: "Chat", label: "llmGroupChat", short: "llmGroupChat", column: "llmGroupChat" },
  { key: "Ocr", label: "llmGroupOcr", short: "llmGroupOcr", column: "llmGroupOcr" },
];

/// The groups a model can actually be assigned to - everything except the parameter-only base scope.
export const ASSIGNABLE_GROUPS = GROUPS.filter((g) => g.key !== "ModelBase");
