import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { handleAuthError, getToken, setToken } from "./api";

const realLocation = window.location;

function stubLocation(pathname: string) {
  const assign = vi.fn();
  Object.defineProperty(window, "location", {
    value: { pathname, assign },
    writable: true,
    configurable: true,
  });
  return assign;
}

describe("handleAuthError", () => {
  beforeEach(() => setToken("a-token"));
  afterEach(() => {
    Object.defineProperty(window, "location", { value: realLocation, configurable: true });
  });

  it("clears the token and redirects to /login on 401", () => {
    const assign = stubLocation("/");
    handleAuthError({ isAxiosError: true, response: { status: 401 } });
    expect(getToken()).toBeNull();
    expect(assign).toHaveBeenCalledWith("/login");
  });

  it("does not redirect when already on /login (avoids a loop) but still clears the token", () => {
    const assign = stubLocation("/login");
    handleAuthError({ isAxiosError: true, response: { status: 401 } });
    expect(getToken()).toBeNull();
    expect(assign).not.toHaveBeenCalled();
  });

  it("leaves the token and does not redirect on non-401 errors", () => {
    const assign = stubLocation("/");
    handleAuthError({ isAxiosError: true, response: { status: 500 } });
    expect(getToken()).toBe("a-token");
    expect(assign).not.toHaveBeenCalled();
  });
});
