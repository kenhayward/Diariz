import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { useState } from "react";
import TemplateContentEditor from "./TemplateContentEditor";
import type { TemplateContent } from "../lib/types";

/// The editor is controlled, so drive it through a tiny host that keeps the draft - the same way both the formula
/// editor and (historically) the meeting-type editor use it.
function Host({ initial, onChange }: { initial: TemplateContent; onChange: (c: TemplateContent) => void }) {
  const [content, setContent] = useState(initial);
  return (
    <TemplateContentEditor
      content={content}
      onChange={(c) => {
        setContent(c);
        onChange(c);
      }}
      t={(k: string) => k.replace(/^workspace:/, "")}
    />
  );
}

const oneBlock: TemplateContent = {
  sections: [{ level: 1, title: "S", blocks: [{ kind: "boilerplate", text: "hi", breakAfter: "paragraph" }] }],
};

describe("TemplateContentEditor", () => {
  it("changes a block's break-after", () => {
    const onChange = vi.fn();
    render(<Host initial={oneBlock} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText("mtBreakAfter"), { target: { value: "line" } });

    const sent = onChange.mock.calls.at(-1)![0] as TemplateContent;
    expect(sent.sections[0].blocks[0].breakAfter).toBe("line");
  });

  it("adds a section", () => {
    const onChange = vi.fn();
    render(<Host initial={{ sections: [] }} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /mtAddSection/ }));

    expect((onChange.mock.calls.at(-1)![0] as TemplateContent).sections).toHaveLength(1);
  });

  // Level 0 = a headless section (the body alone). Every formula that is just a prompt is one, so the editor has
  // to be able to show and keep that shape - if it could not, those formulas would be uneditable.
  it("can represent a headless (level-0) section", () => {
    const headless: TemplateContent = {
      sections: [{ level: 0, title: "", blocks: [{ kind: "prompt", text: "Summarise.", breakAfter: "paragraph" }] }],
    };
    render(<Host initial={headless} onChange={vi.fn()} />);

    const level = screen.getByLabelText("mtHeadingLevel") as HTMLSelectElement;
    expect(level.value).toBe("0");
    expect(screen.getByDisplayValue("Summarise.")).toBeTruthy();
  });

  it("switches a headed section to headless", () => {
    const onChange = vi.fn();
    render(<Host initial={oneBlock} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText("mtHeadingLevel"), { target: { value: "0" } });

    expect((onChange.mock.calls.at(-1)![0] as TemplateContent).sections[0].level).toBe(0);
  });
});
