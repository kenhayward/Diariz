# Template Block Layout Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the meeting-types template editor a per-block "break after" control (none / line / paragraph), an auto-growing markdown text input, and a drag handle to move blocks within and between sections.

**Architecture:** Add one optional `breakAfter` field to `TemplateBlock` on both the .NET and TS sides (no migration - it's a JSON blob). The server composer (`MeetingTypeMinutesComposer`) chooses the whitespace between two rendered blocks from the preceding block's `breakAfter`, falling back to today's rule when it's null (so old templates render identically). The editor UI (`ManageMeetingTypesModal`) gains the break `<select>`, an auto-growing textarea, and native HTML5 cross-section drag-and-drop, backed by two new pure helpers in `meetingTypeDraft.ts`.

**Tech Stack:** ASP.NET Core / .NET 10 + xUnit (API), React 19 + TypeScript + Vitest + Testing Library (web), react-i18next (locales).

---

## File structure

**Modify:**
- `src/Diariz.Api/Services/MeetingTypeContent.cs` - `TemplateBlock` gains `BreakAfter` + constants; `Validate()` checks it.
- `src/Diariz.Api/Services/MeetingTypeMinutesComposer.cs` - `RenderBlocksAsync` honors `BreakAfter`.
- `apps/web/src/lib/types.ts` - `TemplateBlock.breakAfter`.
- `apps/web/src/lib/meetingTypeDraft.ts` - `newBlock` defaults, `moveBlockCrossSection`, `normalizeBreaks`.
- `apps/web/src/components/ManageMeetingTypesModal.tsx` - break select, `AutoGrowTextarea`, block drag/drop, `normalizeBreaks` on load.
- `apps/web/src/locales/{en,es,fr,de}/workspace.json` - new mt* keys.
- `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts` - version bump + release entry.
- `docs/Data_Schema.md`, `docs/features.md`, `README.md`, `apps/web/src/lib/releases.ts` (CAPABILITIES) - docs.

**Test files (existing, extended):**
- `tests/Diariz.Api.Tests/MeetingTypeContentTests.cs`
- `tests/Diariz.Api.Tests/MeetingTypeMinutesComposerTests.cs`
- `apps/web/src/lib/meetingTypeDraft.test.ts`
- `apps/web/src/components/ManageMeetingTypesModal.test.tsx`

**Conventions:** TDD (failing test first). No em/en dashes in user-facing strings. `breakAfter` serializes camelCase (matches `JsonSerializerDefaults.Web`). Run `.NET` tests from the repo root; run web tests from `apps/web`.

---

## Task 1: C# `TemplateBlock.BreakAfter` + validation

**Files:**
- Modify: `src/Diariz.Api/Services/MeetingTypeContent.cs`
- Test: `tests/Diariz.Api.Tests/MeetingTypeContentTests.cs`

- [ ] **Step 1: Write failing tests**

Append inside the `MeetingTypeContentTests` class in `tests/Diariz.Api.Tests/MeetingTypeContentTests.cs`:

```csharp
[Theory]
[InlineData(TemplateBlock.BreakNone)]
[InlineData(TemplateBlock.BreakLine)]
[InlineData(TemplateBlock.BreakParagraph)]
[InlineData(null)]
public void Validate_accepts_a_known_or_null_break_after(string? breakAfter)
{
    var content = new MeetingTypeContent(
        [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi", BreakAfter: breakAfter)])]);
    Assert.True(content.Validate().Ok);
}

[Fact]
public void Validate_rejects_an_unknown_break_after()
{
    var content = new MeetingTypeContent(
        [new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Boilerplate, Text: "hi", BreakAfter: "double")])]);
    Assert.False(content.Validate().Ok);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingTypeContentTests"`
Expected: FAIL to compile - `TemplateBlock` has no `BreakAfter` / no `BreakNone` constant.

- [ ] **Step 3: Add the field, constants, and validation**

In `src/Diariz.Api/Services/MeetingTypeContent.cs`, change the `TemplateBlock` record (currently `public record TemplateBlock(string Kind, string? Text = null, string? Field = null)`) to:

```csharp
public record TemplateBlock(string Kind, string? Text = null, string? Field = null, string? BreakAfter = null)
{
    public const string Boilerplate = "boilerplate";
    public const string FieldKind = "field";
    public const string Prompt = "prompt";

    // The whitespace emitted after this block, before the next (see MeetingTypeMinutesComposer). Null = legacy rule.
    public const string BreakNone = "none";
    public const string BreakLine = "line";
    public const string BreakParagraph = "paragraph";
}
```

In `MeetingTypeContent.Validate()`, inside the `foreach (var block in section.Blocks ?? [])` loop, immediately after the `switch (block.Kind)` block closes (before the loop's closing brace), add:

```csharp
if (block.BreakAfter is not null &&
    block.BreakAfter is not (TemplateBlock.BreakNone or TemplateBlock.BreakLine or TemplateBlock.BreakParagraph))
    return (false, $"Unknown break value '{block.BreakAfter}'.");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingTypeContentTests"`
Expected: PASS (all, including pre-existing tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/MeetingTypeContent.cs tests/Diariz.Api.Tests/MeetingTypeContentTests.cs
git commit -m "feat: add BreakAfter to template blocks with validation"
```

---

## Task 2: Composer honors `breakAfter`

**Files:**
- Modify: `src/Diariz.Api/Services/MeetingTypeMinutesComposer.cs`
- Test: `tests/Diariz.Api.Tests/MeetingTypeMinutesComposerTests.cs`

- [ ] **Step 1: Write failing tests**

Append inside the `MeetingTypeMinutesComposerTests` class:

```csharp
[Theory]
[InlineData(TemplateBlock.BreakNone, "AB")]
[InlineData(TemplateBlock.BreakLine, "A\nB")]
[InlineData(TemplateBlock.BreakParagraph, "A\n\nB")]
public async Task Break_after_controls_the_gap_between_two_blocks(string breakAfter, string expectedBody)
{
    var content = new MeetingTypeContent(
    [
        new TemplateSection(1, "S",
        [
            new TemplateBlock(TemplateBlock.Boilerplate, Text: "A", BreakAfter: breakAfter),
            new TemplateBlock(TemplateBlock.Boilerplate, Text: "B"),
        ]),
    ]);

    var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

    Assert.Equal($"# S\n\n{expectedBody}", md);
}

[Fact]
public async Task Null_break_after_falls_back_to_legacy_field_glue()
{
    // No BreakAfter set anywhere: a field still glues to the preceding boilerplate, two boilerplates break.
    var content = new MeetingTypeContent(
    [
        new TemplateSection(1, "S",
        [
            new TemplateBlock(TemplateBlock.Boilerplate, Text: "Date: "),
            new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
            new TemplateBlock(TemplateBlock.Boilerplate, Text: "Next line"),
        ]),
    ]);

    var md = await MeetingTypeMinutesComposer.ComposeAsync(content, Field, Prompt);

    Assert.Equal("# S\n\nDate: 2026-07-06\n\nNext line", md);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingTypeMinutesComposerTests"`
Expected: FAIL - the new `Break_after_*` assertions fail (current code ignores `BreakAfter`). The pre-existing tests still pass.

- [ ] **Step 3: Rewrite `RenderBlocksAsync`**

In `src/Diariz.Api/Services/MeetingTypeMinutesComposer.cs`, replace the entire `RenderBlocksAsync` method (and keep the file's `using System.Text;`) with:

```csharp
    /// <summary>Render one section's blocks into a body. Each block is rendered, empties are dropped, and adjacent
    /// blocks are joined by the separator chosen from the preceding block's <c>BreakAfter</c> (a null value falls
    /// back to the legacy rule: glue only before a field).</summary>
    private static async Task<string> RenderBlocksAsync(
        IReadOnlyList<TemplateBlock> blocks,
        Func<string, string?> resolveField,
        Func<TemplateBlock, Task<string>> resolvePrompt)
    {
        var rendered = new List<(TemplateBlock Block, string Text)>();
        foreach (var block in blocks)
        {
            var text = block.Kind switch
            {
                TemplateBlock.Boilerplate => block.Text ?? "",
                TemplateBlock.FieldKind => resolveField(block.Field ?? "") ?? "",
                TemplateBlock.Prompt => (await resolvePrompt(block) ?? "").Trim(),
                _ => "",
            };
            rendered.Add((block, text));
        }

        var kept = rendered.Where(r => r.Text.Trim().Length > 0).ToList();
        if (kept.Count == 0) return "";

        var sb = new StringBuilder(kept[0].Text);
        for (var i = 1; i < kept.Count; i++)
        {
            sb.Append(Separator(kept[i - 1].Block, kept[i].Block));
            sb.Append(kept[i].Text);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The whitespace between a rendered block and the next one. Honors the preceding block's explicit
    /// <c>BreakAfter</c>; a null value falls back to the legacy rule (glue only when the next block is a field).</summary>
    private static string Separator(TemplateBlock prev, TemplateBlock next) =>
        (prev.BreakAfter ?? LegacyBreak(next)) switch
        {
            TemplateBlock.BreakNone => "",
            TemplateBlock.BreakLine => "\n",
            _ => "\n\n", // BreakParagraph (and any unexpected value) => paragraph gap
        };

    private static string LegacyBreak(TemplateBlock next) =>
        next.Kind == TemplateBlock.FieldKind ? TemplateBlock.BreakNone : TemplateBlock.BreakParagraph;
```

Note: the prompt resolver is still called exactly once per prompt block, in document order (inside the first loop), so `Prompt_resolver_is_called_once_per_prompt_block_in_document_order` stays green.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MeetingTypeMinutesComposerTests"`
Expected: PASS (all - new and pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/MeetingTypeMinutesComposer.cs tests/Diariz.Api.Tests/MeetingTypeMinutesComposerTests.cs
git commit -m "feat: composer emits per-block break-after whitespace"
```

---

## Task 3: TS type + `newBlock` defaults

**Files:**
- Modify: `apps/web/src/lib/types.ts:52-56`
- Modify: `apps/web/src/lib/meetingTypeDraft.ts:6-9`
- Test: `apps/web/src/lib/meetingTypeDraft.test.ts`

- [ ] **Step 1: Update the failing tests**

In `apps/web/src/lib/meetingTypeDraft.test.ts`, update the two `addBlock` assertions to expect the new default `breakAfter`:

```ts
  it("adds a field block with a default field", () => {
    const out = addBlock(base, 1, "field");
    expect(out.sections[1].blocks).toEqual([{ kind: "field", field: "date", breakAfter: "none" }]);
  });

  it("adds a prompt block with empty text", () => {
    expect(addBlock(base, 1, "prompt").sections[1].blocks[0]).toEqual({
      kind: "prompt", text: "", breakAfter: "paragraph",
    });
  });
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `apps/web`): `npx vitest run src/lib/meetingTypeDraft.test.ts`
Expected: FAIL - `newBlock` does not yet set `breakAfter`.

- [ ] **Step 3: Add the type field and defaults**

In `apps/web/src/lib/types.ts`, change `TemplateBlock` to include `breakAfter`:

```ts
export interface TemplateBlock {
  kind: TemplateBlockKind;
  text?: string | null;
  field?: string | null;
  /// The break emitted after this block (see MeetingTypeMinutesComposer). Absent = legacy rule.
  breakAfter?: "none" | "line" | "paragraph" | null;
}
```

In `apps/web/src/lib/meetingTypeDraft.ts`, replace `newBlock`:

```ts
export function newBlock(kind: TemplateBlockKind): TemplateBlock {
  if (kind === "field") return { kind, field: "date", breakAfter: "none" };
  return { kind, text: "", breakAfter: "paragraph" };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `apps/web`): `npx vitest run src/lib/meetingTypeDraft.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/meetingTypeDraft.ts apps/web/src/lib/meetingTypeDraft.test.ts
git commit -m "feat: breakAfter on TemplateBlock with per-kind defaults"
```

---

## Task 4: `moveBlockCrossSection`

**Files:**
- Modify: `apps/web/src/lib/meetingTypeDraft.ts`
- Test: `apps/web/src/lib/meetingTypeDraft.test.ts`

- [ ] **Step 1: Write failing tests**

In `apps/web/src/lib/meetingTypeDraft.test.ts`, add `moveBlockCrossSection` to the import from `./meetingTypeDraft`, then add this block:

```ts
describe("moveBlockCrossSection", () => {
  const two: MeetingTypeContent = {
    sections: [
      { level: 1, title: "A", blocks: [{ kind: "boilerplate", text: "a0" }, { kind: "boilerplate", text: "a1" }] },
      { level: 1, title: "B", blocks: [{ kind: "boilerplate", text: "b0" }] },
    ],
  };

  it("moves a block into another section at the given index", () => {
    const out = moveBlockCrossSection(two, { section: 0, index: 0 }, { section: 1, index: 0 });
    expect(out.sections[0].blocks.map((b) => b.text)).toEqual(["a1"]);
    expect(out.sections[1].blocks.map((b) => b.text)).toEqual(["a0", "b0"]);
    expect(two.sections[0].blocks).toHaveLength(2); // input unchanged
  });

  it("appends when the destination index is past the end", () => {
    const out = moveBlockCrossSection(two, { section: 0, index: 1 }, { section: 1, index: 99 });
    expect(out.sections[1].blocks.map((b) => b.text)).toEqual(["b0", "a1"]);
  });

  it("delegates a same-section move to reordering", () => {
    const out = moveBlockCrossSection(two, { section: 0, index: 0 }, { section: 0, index: 1 });
    expect(out.sections[0].blocks.map((b) => b.text)).toEqual(["a1", "a0"]);
  });

  it("is a no-op for an out-of-range source", () => {
    expect(moveBlockCrossSection(two, { section: 5, index: 0 }, { section: 1, index: 0 })).toBe(two);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `apps/web`): `npx vitest run src/lib/meetingTypeDraft.test.ts`
Expected: FAIL - `moveBlockCrossSection` is not exported.

- [ ] **Step 3: Implement `moveBlockCrossSection`**

In `apps/web/src/lib/meetingTypeDraft.ts`, after the existing `moveBlock` function, add:

```ts
export function moveBlockCrossSection(
  content: MeetingTypeContent,
  from: { section: number; index: number },
  to: { section: number; index: number },
): MeetingTypeContent {
  const src = content.sections[from.section];
  if (!src) return content;
  const block = src.blocks[from.index];
  if (!block) return content;
  if (from.section === to.section) return moveBlock(content, from.section, from.index, to.index);

  const sections = content.sections.map((s, i) => {
    if (i === from.section) return { ...s, blocks: s.blocks.filter((_, bi) => bi !== from.index) };
    if (i === to.section) {
      const blocks = [...s.blocks];
      const clamped = Math.max(0, Math.min(to.index, blocks.length));
      blocks.splice(clamped, 0, block);
      return { ...s, blocks };
    }
    return s;
  });
  return { ...content, sections };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `apps/web`): `npx vitest run src/lib/meetingTypeDraft.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/meetingTypeDraft.ts apps/web/src/lib/meetingTypeDraft.test.ts
git commit -m "feat: moveBlockCrossSection for cross-section block moves"
```

---

## Task 5: `normalizeBreaks`

**Files:**
- Modify: `apps/web/src/lib/meetingTypeDraft.ts`
- Test: `apps/web/src/lib/meetingTypeDraft.test.ts`

- [ ] **Step 1: Write failing tests**

In `apps/web/src/lib/meetingTypeDraft.test.ts`, add `normalizeBreaks` to the import, then add:

```ts
describe("normalizeBreaks", () => {
  it("back-fills a missing breakAfter from the legacy rule (glue before a field)", () => {
    const content: MeetingTypeContent = {
      sections: [{
        level: 1, title: "S",
        blocks: [
          { kind: "boilerplate", text: "Date: " }, // followed by a field -> none
          { kind: "field", field: "date" },        // followed by boilerplate -> paragraph
          { kind: "boilerplate", text: "end" },     // last -> paragraph
        ],
      }],
    };
    const out = normalizeBreaks(content);
    expect(out.sections[0].blocks.map((b) => b.breakAfter)).toEqual(["none", "paragraph", "paragraph"]);
  });

  it("keeps an already-set breakAfter", () => {
    const content: MeetingTypeContent = {
      sections: [{ level: 1, title: "S", blocks: [{ kind: "boilerplate", text: "x", breakAfter: "line" }] }],
    };
    expect(normalizeBreaks(content).sections[0].blocks[0].breakAfter).toBe("line");
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `apps/web`): `npx vitest run src/lib/meetingTypeDraft.test.ts`
Expected: FAIL - `normalizeBreaks` is not exported.

- [ ] **Step 3: Implement `normalizeBreaks`**

In `apps/web/src/lib/meetingTypeDraft.ts`, add (near the top-level exports, after `moveBlockCrossSection`):

```ts
/// Back-fill an explicit `breakAfter` on any block that lacks one, using the legacy rule (glue only before a
/// field). Applied when loading a template so pre-feature templates show correct controls and re-render identically.
export function normalizeBreaks(content: MeetingTypeContent): MeetingTypeContent {
  return {
    ...content,
    sections: content.sections.map((s) => ({
      ...s,
      blocks: s.blocks.map((b, i) => ({ ...b, breakAfter: b.breakAfter ?? legacyBreak(s.blocks[i + 1]) })),
    })),
  };
}

function legacyBreak(next: TemplateBlock | undefined): "none" | "paragraph" {
  return next && next.kind === "field" ? "none" : "paragraph";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `apps/web`): `npx vitest run src/lib/meetingTypeDraft.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/meetingTypeDraft.ts apps/web/src/lib/meetingTypeDraft.test.ts
git commit -m "feat: normalizeBreaks back-fills legacy break-after on load"
```

---

## Task 6: Locale keys + break-after select in the UI

**Files:**
- Modify: `apps/web/src/locales/en/workspace.json`, `.../es/...`, `.../fr/...`, `.../de/...`
- Modify: `apps/web/src/components/ManageMeetingTypesModal.tsx` (`BlockRow` + its `ContentEditor` call site)
- Test: `apps/web/src/components/ManageMeetingTypesModal.test.tsx`

- [ ] **Step 1: Add locale keys**

In each `workspace.json`, next to the existing `mtBlockActions` key, add these keys (values per locale):

`en/workspace.json`:
```json
  "mtBreakAfter": "Break after",
  "mtBreakNone": "No break",
  "mtBreakLine": "Line break",
  "mtBreakParagraph": "Paragraph",
  "mtDragBlock": "Drag to move",
  "mtMarkdownHint": "Markdown supported",
```

`es/workspace.json`:
```json
  "mtBreakAfter": "Salto posterior",
  "mtBreakNone": "Sin salto",
  "mtBreakLine": "Salto de linea",
  "mtBreakParagraph": "Parrafo",
  "mtDragBlock": "Arrastrar para mover",
  "mtMarkdownHint": "Compatible con Markdown",
```

`fr/workspace.json`:
```json
  "mtBreakAfter": "Saut apres",
  "mtBreakNone": "Aucun saut",
  "mtBreakLine": "Saut de ligne",
  "mtBreakParagraph": "Paragraphe",
  "mtDragBlock": "Glisser pour deplacer",
  "mtMarkdownHint": "Markdown pris en charge",
```

`de/workspace.json`:
```json
  "mtBreakAfter": "Umbruch danach",
  "mtBreakNone": "Kein Umbruch",
  "mtBreakLine": "Zeilenumbruch",
  "mtBreakParagraph": "Absatz",
  "mtDragBlock": "Zum Verschieben ziehen",
  "mtMarkdownHint": "Markdown wird unterstuetzt",
```

- [ ] **Step 2: Write the failing component test**

In `apps/web/src/components/ManageMeetingTypesModal.test.tsx`, add:

```ts
  it("changes a block's break-after and saves it", async () => {
    const withBlock = mt("mine", "My template", false, true);
    withBlock.content = {
      sections: [{ level: 1, title: "S", blocks: [{ kind: "boilerplate", text: "hi", breakAfter: "paragraph" }] }],
    };
    vi.mocked(api.listMeetingTypes).mockResolvedValue([withBlock]);
    vi.mocked(api.updateMeetingType).mockResolvedValue(withBlock);
    renderModal();

    fireEvent.click(await screen.findByText("My template"));
    fireEvent.change(screen.getByLabelText("Break after"), { target: { value: "line" } });
    fireEvent.click(screen.getByText("Save"));

    await waitFor(() => expect(api.updateMeetingType).toHaveBeenCalled());
    const sent = vi.mocked(api.updateMeetingType).mock.calls[0][1];
    expect(sent.content.sections[0].blocks[0].breakAfter).toBe("line");
  });
```

- [ ] **Step 3: Run the test to verify it fails**

Run (from `apps/web`): `npx vitest run src/components/ManageMeetingTypesModal.test.tsx -t "break-after"`
Expected: FAIL - no element labeled "Break after".

- [ ] **Step 4: Add the break-after select to `BlockRow`**

In `apps/web/src/components/ManageMeetingTypesModal.tsx`, extend `BlockRow`'s props and render. Change the destructured params and the type to add `breakAfter` and `onBreakAfter`:

```tsx
function BlockRow({
  kind, text, field, breakAfter, index, count, t, onText, onField, onBreakAfter, onMove, onRemove,
}: {
  section: number;
  index: number;
  count: number;
  kind: TemplateBlockKind;
  text: string;
  field: string;
  breakAfter: string;
  t: (k: string) => string;
  onText: (v: string) => void;
  onField: (v: string) => void;
  onBreakAfter: (v: string) => void;
  onMove: (to: number) => void;
  onRemove: () => void;
}) {
```

Then, inside the returned row, between the `<div className="min-w-0 flex-1">...</div>` content wrapper and the `<KebabMenu ... />`, insert the select:

```tsx
      <select
        value={breakAfter}
        onChange={(e) => onBreakAfter(e.target.value)}
        aria-label={t("workspace:mtBreakAfter")}
        title={t("workspace:mtBreakAfter")}
        className="mt-1 shrink-0 rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      >
        <option value="none">{t("workspace:mtBreakNone")}</option>
        <option value="line">{t("workspace:mtBreakLine")}</option>
        <option value="paragraph">{t("workspace:mtBreakParagraph")}</option>
      </select>
```

In `ContentEditor`, at the `<BlockRow ... />` call site, add these two props:

```tsx
                  breakAfter={block.breakAfter ?? "paragraph"}
                  onBreakAfter={(breakAfter) => onChange(updateBlock(content, si, bi, { breakAfter: breakAfter as TemplateBlock["breakAfter"] }))}
```

Add `TemplateBlock` to the existing type import at the top of the file:
`import type { MeetingType, MeetingTypeContent, TemplateBlock, TemplateBlockKind } from "../lib/types";`

- [ ] **Step 5: Run the test to verify it passes**

Run (from `apps/web`): `npx vitest run src/components/ManageMeetingTypesModal.test.tsx`
Expected: PASS (all).

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/ManageMeetingTypesModal.tsx apps/web/src/components/ManageMeetingTypesModal.test.tsx apps/web/src/locales
git commit -m "feat: per-block break-after select in the template editor"
```

---

## Task 7: Auto-growing markdown textarea

**Files:**
- Modify: `apps/web/src/components/ManageMeetingTypesModal.tsx`
- Test: `apps/web/src/components/ManageMeetingTypesModal.test.tsx`

- [ ] **Step 1: Write the failing test**

In `apps/web/src/components/ManageMeetingTypesModal.test.tsx`, add:

```ts
  it("shows a markdown hint for text blocks", async () => {
    const withBlock = mt("mine", "My template", false, true);
    withBlock.content = {
      sections: [{ level: 1, title: "S", blocks: [{ kind: "boilerplate", text: "hi", breakAfter: "paragraph" }] }],
    };
    vi.mocked(api.listMeetingTypes).mockResolvedValue([withBlock]);
    renderModal();

    fireEvent.click(await screen.findByText("My template"));
    expect(screen.getByText("Markdown supported")).toBeTruthy();
  });
```

- [ ] **Step 2: Run the test to verify it fails**

Run (from `apps/web`): `npx vitest run src/components/ManageMeetingTypesModal.test.tsx -t "markdown hint"`
Expected: FAIL - no "Markdown supported" text.

- [ ] **Step 3: Add `AutoGrowTextarea` and use it in `BlockRow`**

In `apps/web/src/components/ManageMeetingTypesModal.tsx`, add this component near `BlockRow` (top-level in the file):

```tsx
/// A textarea that grows to fit its content (no inner scrollbar), for the raw-markdown block text.
function AutoGrowTextarea({
  value, onChange, placeholder, ariaLabel,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  ariaLabel: string;
}) {
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }, [value]);
  return (
    <textarea
      ref={ref}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      aria-label={ariaLabel}
      rows={1}
      className="w-full resize-none overflow-hidden rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
    />
  );
}
```

In `BlockRow`, replace the `<textarea ... />` in the non-field branch with `AutoGrowTextarea` plus a hint. The `else` branch becomes:

```tsx
        ) : (
          <div>
            <AutoGrowTextarea
              value={text}
              onChange={onText}
              placeholder={kind === "prompt" ? t("workspace:mtPromptPlaceholder") : t("workspace:mtBoilerplatePlaceholder")}
              ariaLabel={label}
            />
            <span className="mt-0.5 block text-[11px] text-gray-400 dark:text-gray-500">{t("workspace:mtMarkdownHint")}</span>
          </div>
        )}
```

- [ ] **Step 4: Run the test to verify it passes**

Run (from `apps/web`): `npx vitest run src/components/ManageMeetingTypesModal.test.tsx`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/ManageMeetingTypesModal.tsx apps/web/src/components/ManageMeetingTypesModal.test.tsx
git commit -m "feat: auto-growing markdown textarea for template text blocks"
```

---

## Task 8: Cross-section block drag-and-drop + normalize on load

**Files:**
- Modify: `apps/web/src/components/ManageMeetingTypesModal.tsx`

This is native HTML5 DnD (no jsdom test - it is covered by the pure `moveBlockCrossSection`). Manual verification at the end.

- [ ] **Step 1: Import the helpers and normalize on load**

In `apps/web/src/components/ManageMeetingTypesModal.tsx`, add `moveBlockCrossSection` and `normalizeBreaks` to the existing import from `../lib/meetingTypeDraft`:

```tsx
import {
  FIELD_OPTIONS, addSection, removeSection, updateSection, moveSection,
  addBlock, removeBlock, updateBlock, moveBlock, moveBlockCrossSection, normalizeBreaks,
  contentError, emptyContent,
} from "../lib/meetingTypeDraft";
```

In `draftFrom`, wrap the content with `normalizeBreaks`:

```tsx
    icon: t.icon || "document", color: t.color || DEFAULT_COLOR, content: normalizeBreaks(t.content), isPlatform: t.isPlatform,
```

- [ ] **Step 2: Add the block-drag ref and drop targets in `ContentEditor`**

In `ContentEditor`, add a second ref next to `dragSection`:

```tsx
  const dragSection = useRef<number | null>(null);
  const dragBlock = useRef<{ section: number; index: number } | null>(null);
```

On the section's block-list container `<div className="space-y-2 p-2">`, make it an append drop target:

```tsx
            <div
              className="space-y-2 p-2"
              onDragOver={(e) => e.preventDefault()}
              onDrop={(e) => {
                if (dragBlock.current) {
                  e.stopPropagation();
                  onChange(moveBlockCrossSection(content, dragBlock.current, { section: si, index: section.blocks.length }));
                }
                dragBlock.current = null;
              }}
            >
```

At the `<BlockRow ... />` call, add the drag-start and per-row drop props:

```tsx
                  onDragStart={() => { dragBlock.current = { section: si, index: bi }; dragSection.current = null; }}
                  onBlockDrop={() => {
                    if (dragBlock.current) onChange(moveBlockCrossSection(content, dragBlock.current, { section: si, index: bi }));
                    dragBlock.current = null;
                  }}
```

- [ ] **Step 3: Add the drag handle + drop wiring to `BlockRow`**

Extend `BlockRow`'s props type with:

```tsx
  onDragStart: () => void;
  onBlockDrop: () => void;
```

and add them to the destructured params. Change the outer row `<div>` to a drop target and prepend the handle:

```tsx
    <div
      className="flex items-start gap-2 rounded border px-2 py-1.5 dark:border-gray-700"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => { e.preventDefault(); e.stopPropagation(); onBlockDrop(); }}
    >
      <span
        draggable
        onDragStart={onDragStart}
        aria-label={t("workspace:mtDragBlock")}
        title={t("workspace:mtDragBlock")}
        className="mt-1 cursor-grab select-none text-gray-400"
      >
        ⠿
      </span>
      <span className="mt-1 w-16 shrink-0 text-xs font-medium uppercase text-gray-400">{label}</span>
```

(The `<span className="mt-1 w-16 ...">{label}</span>` line already exists - keep it directly after the new handle span.)

- [ ] **Step 4: Typecheck + run the full web test + build**

Run (from `apps/web`):
```bash
npx vitest run
npm run build
```
Expected: all tests PASS; `npm run build` (tsc + vite) succeeds with no type errors.

- [ ] **Step 5: Manual verification (native DnD)**

Start the app (`cd apps/web && npm run dev`, API reachable), open Manage Meeting Types, select an editable template, and confirm:
- dragging a block's ⠿ handle onto another block moves it before that block, including into a different section;
- dropping on a section's empty area appends the block there;
- the section-header ⠿ still reorders whole sections;
- changing a block's Break-after to Line/Paragraph/None and saving changes the generated minutes spacing accordingly (re-generate minutes for a recording using that type).

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/ManageMeetingTypesModal.tsx
git commit -m "feat: drag blocks within and between template sections"
```

---

## Task 9: Version bump + release notes

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`
- Test: `apps/web/src/lib/releases.test.ts` (existing assertion)

- [ ] **Step 1: Bump the version in all four mirrors**

Set the version to `0.109.0` (functional enhancement: Minor +1, Build reset to 0, from `0.108.1`):
- `version.json`: `{ "version": "0.109.0" }`
- `apps/web/package.json`: `"version": "0.109.0"`
- `apps/desktop/package.json`: `"version": "0.109.0"`
- `src/Diariz.Api/Diariz.Api.csproj`: `<Version>0.109.0</Version>`

- [ ] **Step 2: Add the release entry**

In `apps/web/src/lib/releases.ts`, insert a new object at the top of the `RELEASES` array (before the `0.108.1` entry). Set `pr` to the real PR number when the PR is opened (use the next number if known; otherwise fill it in before merge):

```ts
  {
    version: "0.109.0",
    date: "2026-07-08",
    pr: 246,
    headline: "More control over meeting-minutes templates",
    summary:
      "The meeting-type template editor now gives you finer control over how your minutes are laid out. " +
      "Each block (text, field, or model prompt) has a Break-after setting - no break, a line break, or a " +
      "full paragraph - so you decide exactly where content runs together and where it separates. Existing " +
      "templates keep their current spacing. Text blocks are now an auto-growing box that accepts Markdown, " +
      "and you can drag any block by its handle to reorder it within a section or move it into another section.",
    added: [
      "Per-block Break-after control (none / line break / paragraph) for template text, field, and prompt blocks.",
      "Drag blocks by a handle to move them within a section or between sections.",
    ],
    changed: [
      "Template text blocks are now an auto-growing input that supports Markdown.",
    ],
  },
```

- [ ] **Step 3: Run the release assertion + typecheck**

Run (from `apps/web`):
```bash
npx vitest run src/lib/releases.test.ts
npm run build
```
Expected: PASS - `RELEASES[0].version` equals `version.json` (`0.109.0`); build succeeds.

- [ ] **Step 4: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore: bump to 0.109.0 with release notes"
```

---

## Task 10: Docs (schema + features + CAPABILITIES)

**Files:**
- Modify: `docs/Data_Schema.md`
- Modify: `docs/features.md`
- Modify: `README.md`
- Modify: `apps/web/src/lib/releases.ts` (the `CAPABILITIES` table)

- [ ] **Step 1: Update the schema doc**

In `docs/Data_Schema.md`, find where the meeting-type template content JSON (`TemplateBlock`) is described and add the `breakAfter` field: a nullable string (`"none" | "line" | "paragraph"`) controlling the whitespace emitted after the block; null means the legacy rule (a `field` glues to the preceding block, otherwise a paragraph break). Note it needs no migration - it lives inside the existing `MeetingType.ContentJson` blob.

- [ ] **Step 2: Update the feature docs in lockstep**

- `docs/features.md`: extend the meeting-minutes-templates bullet to mention per-block Break-after control (none / line / paragraph), Markdown text blocks, and drag-to-reorder blocks within and across sections.
- `README.md`: update the matching Features-table row (the minutes / templates line) with a concise one-line mention of per-block layout control.
- `apps/web/src/lib/releases.ts` `CAPABILITIES`: the "Summaries & minutes" row already mentions "reusable meeting-type templates" - extend it to note per-block layout control (break, Markdown, drag-to-reorder). Keep it to one line.

Confirm no em/en dashes were introduced in any user-facing string.

- [ ] **Step 3: Verify docs build cleanly (About box renders CAPABILITIES)**

Run (from `apps/web`): `npx vitest run src/lib/releases.test.ts && npm run build`
Expected: PASS (the CAPABILITIES edit is a string change; the build confirms no syntax break).

- [ ] **Step 4: Commit**

```bash
git add docs/Data_Schema.md docs/features.md README.md apps/web/src/lib/releases.ts
git commit -m "docs: template block layout controls in schema, features, capabilities"
```

---

## Final verification

- [ ] **Full backend build + tests** (catches integration/CodeQL compile breaks per CLAUDE.md):

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
```
Expected: build succeeds; all unit tests PASS.

- [ ] **Full web tests + build:**

```bash
cd apps/web && npx vitest run && npm run build
```
Expected: all PASS; build succeeds.

- [ ] **Open the PR** stating the deployment surface: **server redeploy only, no desktop release** (this PR touches `apps/web` and `src/Diariz.Api`, not the desktop shell).
