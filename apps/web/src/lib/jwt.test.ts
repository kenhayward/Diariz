import { describe, it, expect } from "vitest";
import { decodeJwtPayload, emailFromToken, fullNameFromToken, pictureFromToken } from "./jwt";

// Build an unsigned JWT (header.payload.signature) for the given payload — display-only decode.
function fakeJwt(payload: Record<string, unknown>): string {
  const b64 = (o: unknown) => btoa(JSON.stringify(o)).replace(/=+$/, "");
  return `${b64({ alg: "none" })}.${b64(payload)}.sig`;
}

describe("jwt", () => {
  it("decodes the payload of a well-formed token", () => {
    const token = fakeJwt({ email: "x@y.com", sub: "123" });
    expect(decodeJwtPayload(token)).toMatchObject({ email: "x@y.com", sub: "123" });
  });

  it("reads the email claim", () => {
    expect(emailFromToken(fakeJwt({ email: "ken@diariz.io" }))).toBe("ken@diariz.io");
  });

  it("returns null for malformed or empty tokens", () => {
    expect(decodeJwtPayload("not-a-jwt")).toBeNull();
    expect(decodeJwtPayload(null)).toBeNull();
    expect(emailFromToken(null)).toBeNull();
  });

  it("reads the full name claim", () => {
    expect(fullNameFromToken(fakeJwt({ name: "Ada Lovelace" }))).toBe("Ada Lovelace");
    expect(fullNameFromToken(fakeJwt({ name: "" }))).toBeNull();
  });

  it("reads the picture claim (present only when set)", () => {
    expect(pictureFromToken(fakeJwt({ picture: "https://pic/1.png" }))).toBe("https://pic/1.png");
    expect(pictureFromToken(fakeJwt({ picture: "" }))).toBeNull();
    expect(pictureFromToken(fakeJwt({}))).toBeNull();
  });

});
