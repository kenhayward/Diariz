import { PARAMETERS, type ParameterLayer, type ParameterValue } from "./parameterSchema";

export interface ResolvedRequest {
  /// Exactly what the API's `LlmRequestBody.Apply` would write, with `model` first.
  body: Record<string, string | number>;

  /// Decided, but never serialised into a request: these govern the client rather than the body. Shown
  /// beside the preview rather than inside it, because a body that lists `timeout_seconds` is a claim
  /// about the request that no server ever sees.
  flags: {
    reasoningEnabled: boolean;
    timeoutSeconds: number;
    toolsSupported: boolean;
    imagesSupported: boolean;
  };
}

/// The platform's own fallbacks, from `LlmParameters` - what applies when no layer decides.
const DEFAULT_TIMEOUT_SECONDS = 120;

/// The layers below the one being edited, most specific first: the model's own Defaults, then the
/// application's defaults for this group, then the application's base. Named rather than sliced off the
/// front of a longer list - a group with no layer of its own would shift the positions and silently drop
/// the model's Defaults.
function below(
  layers: Record<string, ParameterLayer>,
  defaults: Record<string, ParameterLayer>,
  group: string,
): ParameterLayer[] {
  const levels =
    group === "ModelBase"
      ? [defaults.ModelBase]
      : [layers.ModelBase, defaults[group], defaults.ModelBase];
  return levels.filter((l): l is ParameterLayer => l !== undefined);
}

/// The four levels the API walks, most specific first. Mirrors `LlmSettingsResolver.ResolveAsync`.
function walk(
  layers: Record<string, ParameterLayer>,
  defaults: Record<string, ParameterLayer>,
  group: string,
): ParameterLayer[] {
  const own = group === "ModelBase" ? layers.ModelBase : layers[group];
  return [...(own ? [own] : []), ...below(layers, defaults, group)];
}

/// The first layer that MENTIONS the key decides it, whether it names a value or a null. Returns null both
/// when nothing mentions the key and when the deciding layer says null - the callers below cannot tell
/// those apart and do not need to: both mean "no value". Mirrors `LlmParameterLayers.Decide`.
function decide(levels: ParameterLayer[], key: string): ParameterValue {
  for (const level of levels) if (key in level) return level[key];
  return null;
}

function num(levels: ParameterLayer[], key: string): number | undefined {
  const decided = decide(levels, key);
  // A wrong-typed value is not a reason to keep looking: the deciding layer has already decided, and the
  // API's Number()/Integer() return null for it rather than falling through.
  return typeof decided === "number" ? decided : undefined;
}

function text(levels: ParameterLayer[], key: string): string | undefined {
  const decided = decide(levels, key);
  if (typeof decided !== "string" || decided.trim() === "") return undefined;
  return decided;
}

function flag(levels: ParameterLayer[], key: string, fallback: boolean): boolean {
  const decided = decide(levels, key);
  return typeof decided === "boolean" ? decided : fallback;
}

/// What every parameter resolves to from the layers BELOW the one being edited - which is what an
/// inherited row shows under its value, and therefore what "change nothing here" means.
///
/// The layer being edited is excluded, and on the Defaults tab that exclusion is the whole point: a
/// model's own base layer cannot inherit from itself, so there the walk starts at the application
/// defaults. A key a lower layer OMITS resolves to null, which the row renders as "not set" - the same
/// conflation `LlmParameterLayers.Decide` makes, and for the same reason: both mean no value.
export function resolveInherited(
  layers: Record<string, ParameterLayer>,
  defaults: Record<string, ParameterLayer>,
  group: string,
): ParameterLayer {
  const levels = below(layers, defaults, group);
  const out: ParameterLayer = {};
  for (const key of PARAMETERS.map((p) => p.key)) {
    const decided = decide(levels, key);
    if (decided !== null && decided !== undefined) out[key] = decided;
  }
  return out;
}

/// The wire parameters, in the order `LlmRequestBody.Apply` writes them. `reasoning_effort` is deliberately
/// absent - it is gated on `reasoning_enabled` and handled separately below.
const WIRE_NUMBERS = [
  "temperature",
  "top_p",
  "top_k",
  "repeat_penalty",
  "frequency_penalty",
  "presence_penalty",
  "max_tokens",
  "max_completion_tokens",
];

/// Resolves the layers an administrator is editing into the request body the platform would send.
///
/// This exists so the drawer's preview and the server agree. It is a reimplementation rather than a call
/// to the API precisely because it has to update as the admin types, before anything is saved - which
/// means the two can drift, and the tests are what hold them together.
export function buildRequestPreview(
  modelName: string,
  layers: Record<string, ParameterLayer>,
  group: string,
  defaults: Record<string, ParameterLayer>,
): ResolvedRequest {
  const levels = walk(layers, defaults, group);
  const body: Record<string, string | number> = { model: modelName };

  for (const key of WIRE_NUMBERS) {
    const value = num(levels, key);
    if (value !== undefined) body[key] = value;
  }

  const reasoningEnabled = flag(levels, "reasoning_enabled", false);
  const effort = text(levels, "reasoning_effort");
  // Gated on the flag so a non-reasoning endpoint never sees the field at all.
  if (reasoningEnabled && effort !== undefined) body.reasoning_effort = effort;

  return {
    body,
    flags: {
      reasoningEnabled,
      timeoutSeconds: num(levels, "timeout_seconds") ?? DEFAULT_TIMEOUT_SECONDS,
      toolsSupported: flag(levels, "tools_supported", true),
      imagesSupported: flag(levels, "images_supported", false),
    },
  };
}
