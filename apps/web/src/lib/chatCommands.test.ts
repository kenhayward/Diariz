import { describe, it, expect } from "vitest";
import { parseChatCommand, buildToolsOutput, buildHelpOutput } from "./chatCommands";

describe("parseChatCommand", () => {
  it("recognises /tools and /help (case- and space-insensitive)", () => {
    expect(parseChatCommand("/tools")).toBe("tools");
    expect(parseChatCommand("  /TOOLS ")).toBe("tools");
    expect(parseChatCommand("/help")).toBe("help");
    expect(parseChatCommand("/?")).toBe("help");
  });

  it("returns null for normal messages (including text that merely mentions a command)", () => {
    expect(parseChatCommand("what tools do you have?")).toBeNull();
    expect(parseChatCommand("run /tools please")).toBeNull(); // only a bare command counts
    expect(parseChatCommand("")).toBeNull();
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
