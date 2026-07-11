import { Fragment } from "react";

// http(s) URLs. The last char excludes trailing sentence punctuation and closers so "(see https://x)." or a URL
// at the end of a line doesn't swallow the delimiter. Global, for String.split with a capturing group.
const URL_RE = /(https?:\/\/[^\s<]+[^\s<.,;:!?)\]}'"])/g;
const IS_URL = /^https?:\/\//;

/// Render plain text with any embedded http(s) URLs as clickable links (new tab, safe rel). Safe by
/// construction: text is rendered as React children (escaped) and only matched http(s) URLs become anchors -
/// no HTML injection. Used for calendar invite descriptions/locations so meeting join links are clickable.
export default function LinkifiedText({ text }: { text: string }) {
  // split() with a capturing group interleaves the non-URL text and the captured URLs.
  const parts = text.split(URL_RE);
  return (
    <>
      {parts.map((part, i) =>
        IS_URL.test(part) ? (
          <a
            key={i}
            href={part}
            target="_blank"
            rel="noopener noreferrer"
            className="text-indigo-600 underline hover:text-indigo-500 dark:text-indigo-400"
          >
            {part}
          </a>
        ) : (
          <Fragment key={i}>{part}</Fragment>
        ),
      )}
    </>
  );
}
