/// React Query keys for the platform's model configuration.
///
/// These live together because of a defect they exist to prevent. The chat model picker keeps its own query,
/// and it was originally keyed `["chat-models"]` - a fourth key that none of the admin page's mutations knew
/// to invalidate. Ticking a model into the chat picker, renaming one, or deleting one updated the grid and
/// left the picker showing the previous set until the whole app was reloaded, because the settings modal
/// opens over the app without ever blurring the window, so React Query's refetch-on-focus never fired either.
///
/// The fix is structural rather than a matter of remembering: the picker's key is a CHILD of the model list's
/// key, and React Query invalidates by prefix, so every existing `invalidateQueries(MODELS_KEY)` reaches it
/// automatically. A new model mutation added later gets the picker refresh for free.
///
/// The one case prefix matching cannot cover is routing: `ASSIGNMENTS_KEY` is a different resource, but
/// moving the Chat dot changes which model the picker marks as default and offers implicitly. That write has
/// to invalidate `MODELS_KEY` as well, and `LlmModels.tsx` says so where it does it.
export const MODELS_KEY = ["llm-models"] as const;

/// The models offered in the chat picker. Deliberately nested under MODELS_KEY - see above.
export const CHAT_MODELS_KEY = ["llm-models", "chat"] as const;

export const ASSIGNMENTS_KEY = ["llm-assignments"] as const;
