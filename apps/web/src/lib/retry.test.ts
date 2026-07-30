import { describe, it, expect, vi } from "vitest";
import { AxiosError, AxiosHeaders } from "axios";
import { isGatewayError, retryOnGatewayError, UPLOAD_RETRY_DELAYS_MS } from "./retry";

function axiosStatus(status: number): AxiosError {
  const err = new AxiosError("boom");
  err.response = { status, data: null, statusText: "", headers: new AxiosHeaders(), config: { headers: new AxiosHeaders() } };
  return err;
}

describe("isGatewayError", () => {
  it("is true for the statuses a proxy returns when it cannot reach the API", () => {
    expect(isGatewayError(axiosStatus(502))).toBe(true);
    expect(isGatewayError(axiosStatus(503))).toBe(true);
    expect(isGatewayError(axiosStatus(504))).toBe(true);
  });

  it("is false for anything the API itself answered", () => {
    // Replaying these would either fail identically or, worse, duplicate work the server already did.
    for (const status of [400, 401, 403, 404, 409, 413, 500]) {
      expect(isGatewayError(axiosStatus(status))).toBe(false);
    }
  });

  it("is false for a bare network error", () => {
    // Deliberate: a request that never got a response MAY have reached the API and been processed. A
    // duplicated recording is worse than an error message, so ambiguity is not retried.
    expect(isGatewayError(new AxiosError("Network Error"))).toBe(false);
    expect(isGatewayError(new Error("nope"))).toBe(false);
  });
});

describe("retryOnGatewayError", () => {
  it("returns the first success without waiting", async () => {
    const sleep = vi.fn();
    const fn = vi.fn().mockResolvedValue("ok");
    await expect(retryOnGatewayError(fn, { sleep })).resolves.toBe("ok");
    expect(fn).toHaveBeenCalledTimes(1);
    expect(sleep).not.toHaveBeenCalled();
  });

  it("retries a gateway error and succeeds on a later attempt", async () => {
    const sleep = vi.fn().mockResolvedValue(undefined);
    const fn = vi
      .fn()
      .mockRejectedValueOnce(axiosStatus(502))
      .mockRejectedValueOnce(axiosStatus(503))
      .mockResolvedValue("ok");
    await expect(retryOnGatewayError(fn, { sleep })).resolves.toBe("ok");
    expect(fn).toHaveBeenCalledTimes(3);
    expect(sleep).toHaveBeenNthCalledWith(1, UPLOAD_RETRY_DELAYS_MS[0]);
    expect(sleep).toHaveBeenNthCalledWith(2, UPLOAD_RETRY_DELAYS_MS[1]);
  });

  it("gives up after the configured delays and rethrows the last error", async () => {
    const sleep = vi.fn().mockResolvedValue(undefined);
    const err = axiosStatus(502);
    const fn = vi.fn().mockRejectedValue(err);
    await expect(retryOnGatewayError(fn, { sleep })).rejects.toBe(err);
    // One initial attempt plus one per delay - never more, so a long outage still surfaces to the user
    // rather than hanging on a spinner.
    expect(fn).toHaveBeenCalledTimes(UPLOAD_RETRY_DELAYS_MS.length + 1);
  });

  it("does not retry an error the API itself returned", async () => {
    const sleep = vi.fn();
    const err = axiosStatus(401);
    const fn = vi.fn().mockRejectedValue(err);
    await expect(retryOnGatewayError(fn, { sleep })).rejects.toBe(err);
    expect(fn).toHaveBeenCalledTimes(1);
    expect(sleep).not.toHaveBeenCalled();
  });
});
