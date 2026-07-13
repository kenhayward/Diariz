import { describe, it, expect } from "vitest";
import {
  parseChatCommand, parseRunFormula, buildToolsOutput, buildHelpOutput, bulletList, matchCommands,
  conversationToMarkdown, type CommandInfo,
} from "./chatCommands";

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
    expect(parseChatCommand("/attach")).toBe("attach");
  });

  it("returns null for normal messages, unknown slashes, and text that merely mentions a command", () => {
    expect(parseChatCommand("what tools do you have?")).toBeNull();
    expect(parseChatCommand("run /tools please")).toBeNull(); // only a bare command counts
    expect(parseChatCommand("/nope")).toBeNull();
    expect(parseChatCommand("")).toBeNull();
  });
});

describe("parseRunFormula", () => {
  it("extracts the trimmed formula name after /formula", () => {
    expect(parseRunFormula("/formula Follow-up email")).toBe("Follow-up email");
    expect(parseRunFormula("  /formula   Action items  ")).toBe("Action items");
  });

  it("is case-insensitive on the command itself", () => {
    expect(parseRunFormula("/FORMULA Recap")).toBe("Recap");
    expect(parseRunFormula("/Formula recap")).toBe("recap");
  });

  it("returns null for a bare /formula with no name", () => {
    expect(parseRunFormula("/formula")).toBeNull();
    expect(parseRunFormula("/formula ")).toBeNull();
  });

  it("returns null for lookalike commands and normal text", () => {
    expect(parseRunFormula("/formulas x")).toBeNull();
    expect(parseRunFormula("run /formula Recap please")).toBeNull();
    expect(parseRunFormula("just a normal message")).toBeNull();
    expect(parseRunFormula("")).toBeNull();
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
  const labels = {
    heading: "Available tools", disabled: "Tools are off.", none: "None enabled.",
    colName: "Tool", colDescription: "What it does",
  };
  const tools = [
    { title: "Search transcripts", description: "Search everything.", enabled: true },
    { title: "Send email", description: "Email you.", enabled: false },
  ];

  it("lists only the enabled tools as a two-column table", () => {
    const out = buildToolsOutput(tools, true, labels);
    expect(out).toContain("Available tools");
    expect(out).toContain("| Tool | What it does |"); // table header
    expect(out).toContain("| --- | --- |"); // table separator row
    expect(out).toContain("| Search transcripts | Search everything. |"); // enabled tool row
    expect(out).not.toContain("Send email"); // disabled tool omitted
  });

  it("reports when the master switch is off", () => {
    expect(buildToolsOutput(tools, false, labels)).toBe("Tools are off.");
  });

  it("reports when tools are on but none are enabled", () => {
    expect(buildToolsOutput([{ title: "X", description: "y", enabled: false }], true, labels)).toBe("None enabled.");
  });

  it("escapes backslashes, pipes, and newlines so the table row stays intact", () => {
    const out = buildToolsOutput(
      [{ title: "a\\b|c", description: "one\ntwo", enabled: true }],
      true,
      labels,
    );
    // backslash escaped first (\\), then pipe (\|); newline collapsed to a space.
    expect(out).toContain("| a\\\\b\\|c | one two |");
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

describe("conversationToMarkdown", () => {
  const labels = { title: "Chat conversation", youLabel: "You", assistantLabel: "Assistant" };

  it("renders each turn as a labelled section under a title", () => {
    const out = conversationToMarkdown(
      [
        { role: "user", content: "Hello there" },
        { role: "assistant", content: "Hi! How can I help?" },
      ],
      labels,
    );
    expect(out).toContain("# Chat conversation");
    expect(out).toContain("## You\n\nHello there");
    expect(out).toContain("## Assistant\n\nHi! How can I help?");
  });

  it("skips blank turns", () => {
    const out = conversationToMarkdown(
      [
        { role: "user", content: "Question" },
        { role: "assistant", content: "   " },
      ],
      labels,
    );
    expect(out).toContain("## You\n\nQuestion");
    expect(out).not.toContain("## Assistant");
  });
});
