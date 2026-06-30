/// Fallback for chat answers: if the assistant mentions a recording by name but didn't keep the markdown
/// link the tool provided, turn the first plain mention of that name into a link. The recording references
/// come from the server (the `ref` stream events), so this works even when the model paraphrased the link
/// away.

export interface RecordingRef {
  name: string;
  href: string;
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/// Inserts a markdown link for the first plain occurrence of each ref's name that isn't already linked.
/// Longer names are handled first so a name that contains a shorter one wins.
export function linkifyRecordings(markdown: string, refs: ReadonlyArray<RecordingRef>): string {
  let out = markdown;
  const ordered = [...refs].filter((r) => r.name.trim().length >= 3).sort((a, b) => b.name.length - a.name.length);

  for (const ref of ordered) {
    // If this recording is already linked anywhere (the model kept the link), leave the text alone.
    if (out.includes(`](${ref.href}`)) continue;
    // Match the name when it's not glued to a word char and not already inside markdown link brackets.
    const re = new RegExp(`(?<![\\w[])${escapeRegExp(ref.name)}(?![\\w\\]])`);
    if (re.test(out)) {
      out = out.replace(re, `[${ref.name}](${ref.href})`);
    }
  }
  return out;
}
