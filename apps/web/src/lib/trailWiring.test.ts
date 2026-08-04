import { describe, it, expect, beforeEach, vi } from "vitest";
import MockAdapter from "axios-mock-adapter";
import { http } from "./api";
import { snapshot, clearTrail } from "./trail";

let mock: MockAdapter;

beforeEach(() => {
  clearTrail();
  mock = new MockAdapter(http);
});

describe("axios feeds the trail", () => {
  it("records a successful call with method, path and status", async () => {
    mock.onGet("/api/recordings").reply(200, []);
    await http.get("/api/recordings");

    const [entry] = snapshot();
    expect(entry.kind).toBe("api");
    expect(entry.label).toBe("GET /api/recordings");
    expect(entry.detail!.status).toBe(200);
  });

  it("records a failed call, and the failure still propagates", async () => {
    mock.onGet("/api/boom").reply(500);
    await expect(http.get("/api/boom")).rejects.toBeTruthy();

    const [entry] = snapshot();
    expect(entry.detail!.status).toBe(500);
  });

  it("strips a query string from the recorded path", async () => {
    mock.onGet(/\/hubs\/transcription/).reply(200);
    await http.get("/hubs/transcription?access_token=A_LIVE_JWT");

    expect(JSON.stringify(snapshot())).not.toContain("A_LIVE_JWT");
  });
});
