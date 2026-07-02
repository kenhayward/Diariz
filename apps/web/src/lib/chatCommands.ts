/// Client-side chat slash commands. These are handled entirely in the browser and are NEVER sent to the
/// model — so typing a command (e.g. "/tools") always does the same thing deterministically and can't be
/// misread by the LLM as a request to run a tool.

export type ChatCommand = "tools" | "help" | "clear" | "context" | "save" | "load" | "copy" | "retry";

const COMMANDS: ChatCommand[] = ["tools", "help", "clear", "context", "save", "load", "copy", "retry"];

/// Parse a chat input into a slash command, or null when it's a normal message. Only a *bare* command counts
/// ("/tools", not "run /tools please") so ordinary messages that mention a slash still go to the model.
export function parseChatCommand(input: string): ChatCommand | null {
  const s = input.trim().toLowerCase();
  if (s === "/help" || s === "/?") return "help";
  if (!s.startsWith("/")) return null;
  const name = s.slice(1);
  return (COMMANDS as string[]).includes(name) ? (name as ChatCommand) : null;
}

/// One command in the autocomplete/help list.
export interface CommandInfo {
  cmd: ChatCommand;
  command: string; // "/tools"
  description: string;
}

/// Commands whose name starts with the typed input — used to drive the autocomplete popup. Returns [] unless
/// the input starts with "/". Typing just "/" lists them all.
export function matchCommands(input: string, commands: CommandInfo[]): CommandInfo[] {
  const s = input.trim().toLowerCase();
  if (!s.startsWith("/")) return [];
  return commands.filter((c) => c.command.toLowerCase().startsWith(s));
}

export interface ToolsLabels {
  heading: string;
  disabled: string;
  none: string;
}

/// Markdown for "/tools": the enabled chat tools (title + description), or a note when tools are off / none on.
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

/// Markdown for "/help": the available slash commands.
export function buildHelpOutput(commands: { command: string; description: string }[], heading: string): string {
  return `**${heading}**\n\n` + commands.map((c) => `- \`${c.command}\` — ${c.description}`).join("\n");
}

/// A simple "**heading**\n\n- item\n- item" Markdown block (used by "/context").
export function bulletList(heading: string, items: string[]): string {
  return `**${heading}**\n\n` + items.map((i) => `- ${i}`).join("\n");
}
