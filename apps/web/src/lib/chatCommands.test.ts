import { describe, it, expect } from "vitest";
import { parseChatCommand, buildToolsOutput, buildHelpOutput, bulletList, matchCommands, type CommandInfo } from "./chatCommands";

describe("parseChatCommand", () => {
  it("recognises every command (case- and space-insensitive)", () => {
    expect(parseChatCommand("/tools")).toBe("tools");
    expect(parseChatCommand("  /TOOLS ")).toBe("tools");
    expect(parseChatCommand("/help")).toBe("help");
    expect(parseChatCommand("/?")).toBe("help");
    expect(parseChatCommand("/clear")).toBe("clear");
    expect(parseChatCommand("/context")).toBe("context");
    expect(parseChatCommand("/save")).toBe("save");
    expect(parseChatCommand("/load")).toBe("load");
    expect(parseChatCommand("/copy")).toBe("copy");
    expect(parseChatCommand("/retry")).toBe("retry");
  });

  it("returns null for normal messages, unknown slashes, and text that merely mentions a command", () => {
    expect(parseChatCommand("what tools do you have?")).toBeNull();
    expect(parseChatCommand("run /tools please")).toBeNull(); // only a bare command counts
    expect(parseChatCommand("/nope")).toBeNull();
    expect(parseChatCommand("")).toBeNull();
  });
});

describe("matchCommands", () => {
  const cmds: CommandInfo[] = [
    { cmd: "clear", command: "/clear", description: "" },
    { cmd: "context", command: "/context", description: "" },
    { cmd: "copy", command: "/copy", description: "" },
    { cmd: "tools", command: "/tools", description: "" },
  ];

  it("lists all commands for a bare slash", () => {
    expect(matchCommands("/", cmds)).toHaveLength(4);
  });

  it("filters by the typed prefix", () => {
    expect(matchCommands("/co", cmds).map((c) => c.command)).toEqual(["/context", "/copy"]);
    expect(matchCommands("/tool", cmds).map((c) => c.command)).toEqual(["/tools"]);
  });

  it("returns nothing when the input isn't a slash command", () => {
    expect(matchCommands("hello", cmds)).toEqual([]);
    expect(matchCommands("", cmds)).toEqual([]);
  });
});

describe("bulletList", () => {
  it("renders a heading and bullet items", () => {
    const out = bulletList("Chat context", ["Scope: Current", "Model: gpt"]);
    expect(out).toContain("**Chat context**");
    expect(out).toContain("- Scope: Current");
    expect(out).toContain("- Model: gpt");
  });
});

describe("buildToolsOutput", () => {
  const labels = { heading: "Available tools", disabled: "Tools are off.", none: "None enabled." };
  const tools = [
    { title: "Search transcripts", description: "Search everything.", enabled: true },
    { title: "Send email", description: "Email you.", enabled: false },
  ];

  it("lists only the enabled tools", () => {
    const out = buildToolsOutput(tools, true, labels);
    expect(out).toContain("Available tools");
    expect(out).toContain("Search transcripts");
    expect(out).not.toContain("Send email"); // disabled tool omitted
  });

  it("reports when the master switch is off", () => {
    expect(buildToolsOutput(tools, false, labels)).toBe("Tools are off.");
  });

  it("reports when tools are on but none are enabled", () => {
    expect(buildToolsOutput([{ title: "X", description: "y", enabled: false }], true, labels)).toBe("None enabled.");
  });
});

describe("buildHelpOutput", () => {
  it("lists the commands", () => {
    const out = buildHelpOutput([{ command: "/tools", description: "List tools." }], "Commands");
    expect(out).toContain("Commands");
    expect(out).toContain("`/tools`");
    expect(out).toContain("List tools.");
  });
});
