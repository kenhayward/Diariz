import { describe, it, expect } from "vitest";
import { apiErrorMessage } from "./api";

// Builds an object that axios.isAxiosError() treats as an axios error.
function axiosError(data: unknown, status = 400) {
  return { isAxiosError: true, response: { data, status } };
}

describe("apiErrorMessage", () => {
  it("reports a network failure when there is no response", () => {
    expect(apiErrorMessage({ isAxiosError: true, response: undefined })).toBe(
      "Cannot reach the server."
    );
  });

  it("returns a plain string body as-is", () => {
    expect(apiErrorMessage(axiosError("Invalid email or password"))).toBe(
      "Invalid email or password"
    );
  });

  it("joins an array body (Identity error descriptions)", () => {
    expect(apiErrorMessage(axiosError(["Passwords must be 8+ chars.", "Need a digit."]))).toBe(
      "Passwords must be 8+ chars. Need a digit."
    );
  });

  it("flattens a ProblemDetails errors object", () => {
    const body = { errors: { Email: ["Required"], Password: ["Too short", "No digit"] } };
    expect(apiErrorMessage(axiosError(body))).toBe("Required Too short No digit");
  });

  it("falls back to the ProblemDetails title", () => {
    expect(apiErrorMessage(axiosError({ title: "Bad Request" }))).toBe("Bad Request");
  });

  it("falls back to the status code when the body is unhelpful", () => {
    expect(apiErrorMessage(axiosError({}, 503))).toBe("Request failed (503).");
  });

  it("uses the message of a non-axios Error", () => {
    expect(apiErrorMessage(new Error("boom"))).toBe("boom");
  });

  it("uses the provided fallback for unknown non-Error values", () => {
    expect(apiErrorMessage(42, "Custom fallback.")).toBe("Custom fallback.");
  });

  it("uses the default fallback when none is provided", () => {
    expect(apiErrorMessage(undefined)).toBe("Something went wrong.");
  });
});
