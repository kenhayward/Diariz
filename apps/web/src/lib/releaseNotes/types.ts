/// Shapes for the release history. Kept in their own module so `current.ts` and `archive.ts` can
/// both use them without importing the barrel, which would make the lazy archive import circular.

export interface Release {
  version: string;
  date: string; // ISO yyyy-mm-dd
  pr?: number;
  headline: string;
  summary: string; // markdown; PR-level detail
  added?: string[];
  changed?: string[];
  fixed?: string[];
}

/// A named, curated span of the release history - the default view of the release-notes page.
///
/// `from`/`to` are inclusive version bounds over the archive, and the ranges are contiguous: every
/// archived release belongs to exactly one epoch. An epoch stores no date span or release count,
/// because both are derivable from the releases in its range and a stored copy would be a second
/// derivation of the same fact - agreeing with the first only by luck.
export interface Epoch {
  /// Stable URL slug. Hand-authored rather than derived from `title`, so retitling an epoch does
  /// not break a bookmark.
  id: string;
  title: string;
  from: string;
  to: string;
  summary: string; // markdown
  highlights?: string[];
}
