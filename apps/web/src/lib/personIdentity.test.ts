import { describe, expect, it } from "vitest";
import { personIdentity } from "./personIdentity";
import type { Person } from "./types";

const base: Person = {
  id: "p1", name: "Ken Hayward", title: null, companyName: null, email: null, phone: null,
  isInternal: true, voiceprintOptOut: false, hasVoiceprint: false, sampleCount: 0,
  linkedUserId: null, isSelf: false, canManageBiometrics: false,
  createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
};

describe("personIdentity", () => {
  it("marks the signed-in user's own record", () => {
    expect(personIdentity({ ...base, linkedUserId: "u1", isSelf: true, email: "ken@example.com" }))
      .toEqual({ kind: "self", email: "ken@example.com", i18nKey: "identitySelf" });
  });

  it("names the account for someone else's linked record", () => {
    expect(personIdentity({ ...base, linkedUserId: "u2", email: "other@acme.com" }))
      .toEqual({ kind: "linked", email: "other@acme.com", i18nKey: "identityLinked" });
  });

  it("says plainly when there is no account", () => {
    expect(personIdentity(base)).toEqual({ kind: "none", email: null, i18nKey: "identityNone" });
  });

  it("still reports linked when the account has no email on file", () => {
    // LinkedUserId is the fact that decides; a missing email must not downgrade it to "no account",
    // because that would tell the user a merge is allowed when the server will refuse it.
    expect(personIdentity({ ...base, linkedUserId: "u2", email: null }))
      .toEqual({ kind: "linked", email: null, i18nKey: "identityLinked" });
  });

  it("ignores an email on an unlinked person", () => {
    // An unlinked person may still have a contact email typed in by hand. That is not an account, and
    // showing it as one would make two unmergeable-looking records out of a pair that can be merged.
    expect(personIdentity({ ...base, email: "typed@byhand.com" }))
      .toEqual({ kind: "none", email: null, i18nKey: "identityNone" });
  });

  it("treats a blank email as absent", () => {
    expect(personIdentity({ ...base, linkedUserId: "u2", email: "   " }))
      .toEqual({ kind: "linked", email: null, i18nKey: "identityLinked" });
  });
});
