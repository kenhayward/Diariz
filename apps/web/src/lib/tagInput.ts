/// What a tag may look like, client-side. Mirrors `TagText.Normalize` in
/// `src/Diariz.Api/Services/TagText.cs` - change both together, since the server normalises again and a
/// drift between the two would show up as a chip that renames itself after a refetch.
///
/// A tag never contains whitespace: internal whitespace becomes a hyphen so a pasted phrase lands as one
/// token. Case is kept as typed; every comparison is case-insensitive.

/// Longest tag the API stores (the `RecordingTags.Tag` column).
const MAX_LENGTH = 64;

/// Cleans a raw tag, or returns null when there is no usable text (blank, or hyphens only).
export function normalizeTag(raw: string): string | null {
  const joined = raw.trim().replace(/\s+/g, "-").replace(/^-+|-+$/g, "");
  if (joined.length === 0) return null;
  if (joined.length <= MAX_LENGTH) return joined;

  // Trim hyphens AGAIN after the slice, or this function is not idempotent: the cut can land right after a
  // hyphen (or after whitespace that just became one), leaving a trailing hyphen that normalising the result
  // would strip. Adopting an over-long suggestion would then insert a second row on the server and the chip
  // would visibly re-spell itself after the refetch. Cannot empty the string: `joined` is longer than
  // MAX_LENGTH and its first character is already known not to be a hyphen. `TagText.Normalize` does the
  // same; see SHARED_FIXTURE in `tagInput.test.ts` for the exact cases, mirrored in the C# test.
  return joined.slice(0, MAX_LENGTH).replace(/^-+|-+$/g, "");
}

/// Adds `raw` to `list`, case-insensitively de-duplicated. `added` is the tag that went in, or null when
/// the input was unusable or already present - the caller uses it to decide whether to call the API, so a
/// duplicate never becomes a request. Never mutates `list`.
export function addTag(list: string[], raw: string): { tags: string[]; added: string | null } {
  const tag = normalizeTag(raw);
  if (tag === null) return { tags: list, added: null };

  const lower = tag.toLowerCase();
  if (list.some((t) => t.toLowerCase() === lower)) return { tags: list, added: null };

  return { tags: [...list, tag], added: tag };
}
