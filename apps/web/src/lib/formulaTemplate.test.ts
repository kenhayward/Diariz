import { describe, it, expect } from "vitest";
import { barePrompt, fromPrompt, isStructured, templateOutline } from "./formulaTemplate";
import type { TemplateContent } from "./types";

describe("templateOutline", () => {
  // The shared-formulas browser lets you read a formula before adding it. A bare prompt shows verbatim; a
  // structured one has no single prompt to show, so it shows its shape instead of nothing.
  it("renders headings and prompt instructions so a structured formula can be read before adding it", () => {
    const content: TemplateContent = {
      sections: [
        {
          level: 1,
          title: "Decisions",
          blocks: [
            { kind: "prompt", text: "List the decisions." },
            { kind: "field", field: "date" },
          ],
        },
      ],
    };
    expect(templateOutline(content)).toBe("# Decisions\n\nList the decisions.\n{{date}}");
  });

  it("omits the heading of a headless section", () => {
    const content: TemplateContent = {
      sections: [{ level: 0, title: "", blocks: [{ kind: "boilerplate", text: "Hello." }] }],
    };
    expect(templateOutline(content)).toBe("Hello.");
  });

  it("is empty for missing content", () => {
    expect(templateOutline(null)).toBe("");
  });
});

describe("fromPrompt", () => {
  it("wraps a prompt in one headless section, so it composes to just that prompt's output", () => {
    expect(fromPrompt("Draft a follow-up.")).toEqual({
      sections: [{ level: 0, title: "", blocks: [{ kind: "prompt", text: "Draft a follow-up." }] }],
    });
  });
});

describe("barePrompt", () => {
  it("round-trips a prompt", () => {
    expect(barePrompt(fromPrompt("Draft a follow-up."))).toBe("Draft a follow-up.");
  });

  it("is null once the template has a heading", () => {
    const content: TemplateContent = {
      sections: [{ level: 1, title: "Summary", blocks: [{ kind: "prompt", text: "Summarise." }] }],
    };
    expect(barePrompt(content)).toBeNull();
  });

  it("is null once the template has more than one block", () => {
    const content: TemplateContent = {
      sections: [
        { level: 0, title: "", blocks: [{ kind: "prompt", text: "Summarise." }, { kind: "field", field: "date" }] },
      ],
    };
    expect(barePrompt(content)).toBeNull();
  });

  it("is null for a single non-prompt block", () => {
    const content: TemplateContent = {
      sections: [{ level: 0, title: "", blocks: [{ kind: "boilerplate", text: "hi" }] }],
    };
    expect(barePrompt(content)).toBeNull();
  });

  it("is null for more than one section", () => {
    const content: TemplateContent = {
      sections: [
        { level: 0, title: "", blocks: [{ kind: "prompt", text: "A" }] },
        { level: 0, title: "", blocks: [{ kind: "prompt", text: "B" }] },
      ],
    };
    expect(barePrompt(content)).toBeNull();
  });

  it("is null for empty or missing content", () => {
    expect(barePrompt({ sections: [] })).toBeNull();
    expect(barePrompt(null)).toBeNull();
    expect(barePrompt(undefined)).toBeNull();
  });
});

describe("isStructured", () => {
  // The distinction the editor needs: a bare prompt can be edited as a textarea; a structured template can't be
  // flattened back to one without destroying it, so the editor must not try.
  it("is false for a bare prompt", () => {
    expect(isStructured(fromPrompt("Summarise."))).toBe(false);
  });

  it("is false for empty content (a brand-new formula)", () => {
    expect(isStructured({ sections: [] })).toBe(false);
    expect(isStructured(null)).toBe(false);
  });

  it("is true once the template carries real structure", () => {
    const content: TemplateContent = {
      sections: [{ level: 1, title: "Summary", blocks: [{ kind: "prompt", text: "Summarise." }] }],
    };
    expect(isStructured(content)).toBe(true);
  });
});
