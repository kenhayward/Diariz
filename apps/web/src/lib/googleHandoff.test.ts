import { describe, it, expect } from "vitest";
import { parseHandoffToken } from "./googleHandoff";

describe("parseHandoffToken", () => {
  it("extracts the token from among other cookies", () => {
    expect(parseHandoffToken("foo=1; diariz_auth=eyJ.abc.def; bar=2")).toBe("eyJ.abc.def");
  });

  it("reads it when it's the only/first cookie", () => {
    expect(parseHandoffToken("diariz_auth=eyJ.abc.def")).toBe("eyJ.abc.def");
  });

  it("returns null when absent or empty", () => {
    expect(parseHandoffToken("foo=1; bar=2")).toBeNull();
    expect(parseHandoffToken("")).toBeNull();
  });

  it("url-decodes the value", () => {
    expect(parseHandoffToken("diariz_auth=a%2Bb")).toBe("a+b");
  });
});
