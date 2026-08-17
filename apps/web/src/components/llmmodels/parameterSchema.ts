/// The 13 parameters an administrator can set, in the order they appear in a panel.
///
/// The `key` values must match `LlmParameterLayers.ParameterNames` on the API exactly - the API rejects any
/// key it does not recognise, so a typo here becomes a 400 the admin cannot act on. `parameterSchema.test.ts`
/// pins the list against that contract.
export type ParameterKind = "number" | "integer" | "text" | "boolean";

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
  { key: "top_k", label: "llmParamTopK", kind: "integer", min: 0 },
  { key: "repeat_penalty", label: "llmParamRepeatPenalty", kind: "number", min: 0 },
  { key: "frequency_penalty", label: "llmParamFrequencyPenalty", kind: "number", min: -2, max: 2 },
  { key: "presence_penalty", label: "llmParamPresencePenalty", kind: "number", min: -2, max: 2 },
  { key: "max_tokens", label: "llmParamMaxTokens", kind: "integer", min: 0 },
  { key: "max_completion_tokens", label: "llmParamMaxCompletionTokens", kind: "integer", min: 0 },
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
];

/// The parameter groups, in editing order. `ModelBase` is the model's own defaults rather than a call type,
/// which is why it is labelled differently and can never be assigned to.
export const GROUPS: { key: string; label: string }[] = [
  { key: "ModelBase", label: "llmGroupModelBase" },
  { key: "Tags", label: "llmGroupTags" },
  { key: "Actions", label: "llmGroupActions" },
  { key: "Summaries", label: "llmGroupSummaries" },
  { key: "MinutesAndFormulas", label: "llmGroupMinutesAndFormulas" },
  { key: "Translation", label: "llmGroupTranslation" },
  { key: "Chat", label: "llmGroupChat" },
];

/// The groups a model can actually be assigned to - everything except the parameter-only base scope.
export const ASSIGNABLE_GROUPS = GROUPS.filter((g) => g.key !== "ModelBase");
