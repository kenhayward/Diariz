import type { Person } from "./types";

/// Which account a person IS, reduced to one renderable fact.
///
/// Two people with the same name are indistinguishable in a platform-wide directory, and the merge dialog
/// refuses a linked/linked pair without saying which accounts it means. This is the single answer all three
/// surfaces render (the directory list, the duplicates banner, the merge dialog), so they cannot drift into
/// describing the same person differently.
///
/// `kind` is decided by `linkedUserId` alone, never by the email, and that matters in both directions:
///
/// - A **linked** person whose account has no email on file must still read as linked. "No Diariz account"
///   there would promise a merge the server will reject with a 400.
/// - An **unlinked** person may still carry a contact email someone typed in by hand. That is not an
///   account, and rendering it as one would make an unmergeable-looking pair out of one you can merge.
///
/// For a linked person `Person.Email` *is* the account's email - `PeopleDirectory` keeps them in sync - so
/// nothing new is fetched or exposed here.
export type PersonIdentity = {
  kind: "self" | "linked" | "none";
  email: string | null;
  i18nKey: "identitySelf" | "identityLinked" | "identityNone";
};

export function personIdentity(p: Person): PersonIdentity {
  if (p.linkedUserId == null) return { kind: "none", email: null, i18nKey: "identityNone" };
  const email = p.email && p.email.trim() !== "" ? p.email : null;
  return p.isSelf
    ? { kind: "self", email, i18nKey: "identitySelf" }
    : { kind: "linked", email, i18nKey: "identityLinked" };
}
