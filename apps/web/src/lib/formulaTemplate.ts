import type { TemplateContent } from "./types";

/// A formula's body is a template (the same structure a meeting-minutes template uses). A formula that is
/// *just a prompt* - which every formula was before templates - is stored as one headless (level-0) section
/// holding one prompt block. That composes to exactly that prompt's output, with no heading around it.
///
/// Mirrors `TemplateContent.FromPrompt` / `BarePrompt` on the server; keep the two in step.

export function fromPrompt(prompt: string): TemplateContent {
  return { sections: [{ level: 0, title: "", blocks: [{ kind: "prompt", text: prompt }] }] };
}

/// The prompt text when the template is *nothing but* a prompt; null when it carries any structure - a heading,
/// a field, boilerplate, or a second prompt.
export function barePrompt(content: TemplateContent | null | undefined): string | null {
  const sections = content?.sections ?? [];
  if (sections.length !== 1) return null;

  const [section] = sections;
  if (section.level > 0) return null;
  if (section.blocks.length !== 1) return null;

  const [block] = section.blocks;
  return block.kind === "prompt" ? block.text ?? "" : null;
}

/// Whether the template has real structure - i.e. it cannot be edited as a single prompt without destroying it.
/// Empty content (a brand-new formula) is not structured: it is simply a prompt not yet written.
export function isStructured(content: TemplateContent | null | undefined): boolean {
  const sections = content?.sections ?? [];
  return sections.length > 0 && barePrompt(content) === null;
}

/// A readable rendering of a template, for previewing a formula you don't own (the shared-formulas browser lets
/// you read one before adding it). A structured template has no single prompt to show, so show its shape: the
/// headings, the literal text, the fields it substitutes, and the instructions it gives the model.
export function templateOutline(content: TemplateContent | null | undefined): string {
  return (content?.sections ?? [])
    .map((section) => {
      const body = section.blocks
        .map((b) => {
          if (b.kind === "field") return `{{${b.field ?? ""}}}`;
          if (b.kind === "hr") return "---";
          return b.text ?? "";
        })
        .filter((line) => line.length > 0)
        .join("\n");

      if (section.level <= 0) return body;
      return `${"#".repeat(section.level)} ${section.title}\n\n${body}`;
    })
    .filter((s) => s.trim().length > 0)
    .join("\n\n");
}
