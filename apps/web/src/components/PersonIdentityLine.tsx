import { useTranslation } from "react-i18next";
import { personIdentity } from "../lib/personIdentity";
import type { Person } from "../lib/types";

/// Renders which account a person is, as one line.
///
/// A component rather than a helper each caller copies: the directory list, the duplicates banner and the
/// merge dialog all answer the same question, and three copies of the wording would drift the first time
/// one of them was edited. The decision itself lives in `lib/personIdentity` and is unit-tested there.
///
/// An account with no email on file still reads as *having* an account, just without naming it. Falling
/// through to "no Diariz account" would promise a merge the server refuses with a 400.
export default function PersonIdentityLine({
  person,
  className,
}: {
  person: Person;
  className?: string;
}) {
  const { t } = useTranslation("people");
  const identity = personIdentity(person);

  const text =
    identity.email == null
      ? t(
          identity.kind === "self"
            ? "identitySelfNoEmail"
            : identity.kind === "linked"
              ? "identityLinkedNoEmail"
              : "identityNone",
        )
      : t(identity.i18nKey, { email: identity.email });

  return <span className={className}>{text}</span>;
}
