import { describe, it, expect, beforeEach } from "vitest";
import { getToken, setToken } from "./api";

describe("token storage", () => {
  beforeEach(() => localStorage.clear());

  it("returns null when no token is stored", () => {
    expect(getToken()).toBeNull();
  });

  it("round-trips a token through localStorage", () => {
    setToken("jwt-abc");
    expect(getToken()).toBe("jwt-abc");
  });

  it("clears the token when set to null", () => {
    setToken("jwt-abc");
    setToken(null);
    expect(getToken()).toBeNull();
  });
});
