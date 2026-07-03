/// Pure model for the ephemeral "Tool call: …" indicator shown in the chat while tools run.
///
/// The chat is not interrupted: while one or more tool calls are in flight we show a single gray line
/// listing the active tool names; when they all finish (or the assistant resumes producing text) the line
/// disappears, so completed calls leave no trace and subsequent calls reuse the same line.

export interface ToolCallLineState {
  /// Tool names currently executing, in start order.
  active: string[];
}

export const emptyToolCallLine: ToolCallLineState = { active: [] };

/// A tool call started — add it to the active list.
export function toolStarted(state: ToolCallLineState, name: string): ToolCallLineState {
  return { active: [...state.active, name] };
}

/// A tool call finished — remove one occurrence of it.
export function toolEnded(state: ToolCallLineState, name: string): ToolCallLineState {
  const i = state.active.indexOf(name);
  if (i < 0) return state;
  return { active: [...state.active.slice(0, i), ...state.active.slice(i + 1)] };
}

/// Clears the line (called when the assistant resumes text / the turn ends).
export function clearToolCallLine(): ToolCallLineState {
  return emptyToolCallLine;
}

/// The gray line text, or null when nothing is running. Format: "<prefix> a… b…". The prefix and the
/// per-tool label are supplied by the caller so the indicator can be localized (the tool names come from
/// the server as stable snake_case ids); both default to English/raw for non-UI callers and tests.
export function toolCallLineText(
  state: ToolCallLineState,
  opts?: { prefix?: string; label?: (name: string) => string },
): string | null {
  if (state.active.length === 0) return null;
  const prefix = opts?.prefix ?? "Tool call:";
  const label = opts?.label ?? ((n) => n);
  return `${prefix} ` + state.active.map((n) => `${label(n)}…`).join(" ");
}
