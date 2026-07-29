/// Which `api.*` client methods call an endpoint that requires a **platform permission**, and which
/// surfaces are allowed to use them.
///
/// This exists because of a real regression. The recording page prefetched the whole people directory to
/// fill the speaker-assignment picker; when listing the directory started requiring `ManagePeople`, every
/// user without it opened a recording to an empty picker and could not name a speaker at all. Nothing
/// caught it: the component tests mock `../lib/api` wholesale, so no test ever crosses a real permission
/// boundary, and the failure is a 403 that looks like "no results" rather than an error.
///
/// `gatedApi.test.ts` enforces this list by scanning source. It is **default-deny**: a module may only
/// reference a gated method if it is named in {@link PERMISSION_GATED_SURFACES}. A new page that reaches
/// for one fails the build until someone decides, deliberately, that the page is admin-only.

/// Client methods whose endpoint 403s without the named permission. Keep in step with the controllers -
/// the value is documentation, the key is what the test matches on.
export const GATED_API_METHODS: Readonly<Record<string, string>> = {
  // PeopleController - the directory is platform-wide, so browsing it exposes every external contact the
  // organisation has recorded. Searching to label a speaker is deliberately NOT here.
  listPeople: "managePeople",
  findPersonDuplicates: "managePeople",
  deletePerson: "managePeople",
  mergePeople: "managePeople",
  deleteAllVoiceprints: "managePlatform",
};

/// Modules permitted to call a gated method, because they are only reachable by someone who holds the
/// permission. Adding to this list is a decision that the surface is administrative - not a way to silence
/// the test.
export const PERMISSION_GATED_SURFACES: readonly string[] = [
  // The directory modal and its editor are reachable only with ManagePeople - the modal checks the same
  // permission the endpoints enforce, and renders an explanation instead when it is absent.
  "components/PeopleModal.tsx",
  "components/PersonEditor.tsx",
];

/// True when `source` references `api.<method>` for a gated method. Deliberately crude: a substring match
/// over the module text, matching how helpContent.test.ts scans for HelpButton topics. False positives are
/// cheap to resolve (name the surface, or do not call it); a false negative would defeat the point.
export function gatedMethodsUsedIn(source: string): string[] {
  return Object.keys(GATED_API_METHODS).filter((method) => source.includes(`api.${method}`));
}
