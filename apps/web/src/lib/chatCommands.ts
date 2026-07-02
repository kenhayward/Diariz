/// Client-side chat slash commands. These are handled entirely in the browser and are NEVER sent to the
/// model — so typing "/tools" always shows the tool list deterministically and can't trigger a spurious
/// tool call (as it did when the input was passed through to the LLM).

export type ChatCommand = "tools" | "help";

/// Parse a chat input into a slash command, or null when it's a normal message.
export function parseChatCommand(input: string): ChatCommand | null {
  const s = input.trim().toLowerCase();
  if (s === "/tools") return "tools";
  if (s === "/help" || s === "/?") return "help";
  return null;
}

export interface ToolsLabels {
  heading: string;
  disabled: string;
  none: string;
}

/// Markdown for the "/tools" command: the enabled chat tools (title + description), or a note when tools are
/// off or none are enabled.
export function buildToolsOutput(
  tools: { title: string; description: string; enabled: boolean }[],
  masterEnabled: boolean,
  labels: ToolsLabels,
): string {
  if (!masterEnabled) return labels.disabled;
  const on = tools.filter((t) => t.enabled);
  if (on.length === 0) return labels.none;
  return `**${labels.heading}**\n\n` + on.map((t) => `- **${t.title}** — ${t.description}`).join("\n");
}

/// Markdown for the "/help" command: the available slash commands.
export function buildHelpOutput(commands: { command: string; description: string }[], heading: string): string {
  return `**${heading}**\n\n` + commands.map((c) => `- \`${c.command}\` — ${c.description}`).join("\n");
}
