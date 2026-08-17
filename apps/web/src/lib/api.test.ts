import { describe, it, expect, beforeEach, afterEach } from "vitest";
import axios from "axios";
import MockAdapter from "axios-mock-adapter";
import { http, api } from "./api";

// ASP.NET Core's [FromQuery] array binder only recognises the repeated-key form
// ("userIds=a&userIds=b"), not axios's default bracketed form ("userIds[]=a&userIds[]=b" - see
// apps/web/src/lib/api.ts's "LLM usage log" section comment for the full story). A regression that
// dropped `paramsSerializer: { indexes: null }` from one of these calls would silently unfilter every
// multi-select on the usage viewer - including the destructive bulk delete - without any component
// test catching it, since a component test asserts on rendered rows, not on the wire format of the
// request that produced them. These tests read the ACTUAL serialized request axios sent (via
// `axios.getUri`, the same helper the real HTTP adapter uses internally to build the URL), not just
// the `params` object passed in - a mistake in `paramsSerializer` would not show up if only `params`
// were inspected.

describe("api client: array query params serialize as repeated keys, not bracketed", () => {
  let mock: MockAdapter;

  beforeEach(() => {
    mock = new MockAdapter(http);
  });

  afterEach(() => {
    mock.restore();
  });

  const userIds = ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"];

  it("getLlmUsage sends a multi-value array filter as repeated keys", async () => {
    mock.onGet("/api/admin/llm-usage").reply(200, {
      rows: [],
      page: 1,
      pageSize: 50,
      total: 0,
      totals: {
        calls: 0,
        operations: 0,
        durationMs: 0,
        promptTokens: null,
        completionTokens: null,
        reasoningTokens: null,
        totalTokens: null,
        tokenMeasuredCalls: 0,
        failedCalls: 0,
        tokensPerSecond: null,
      },
    });

    await api.getLlmUsage({ userIds });

    const url = axios.getUri(mock.history.get[0]);
    expect(url).toContain(`userIds=${userIds[0]}&userIds=${userIds[1]}`);
    expect(url).not.toContain("%5B%5D"); // the encoded bracketed form ASP.NET Core does not bind
  });

  it("deleteLlmUsage sends a multi-value array filter as repeated keys", async () => {
    mock.onDelete("/api/admin/llm-usage").reply(200, { deleted: 0 });

    await api.deleteLlmUsage({ userIds });

    const url = axios.getUri(mock.history.delete[0]);
    expect(url).toContain(`userIds=${userIds[0]}&userIds=${userIds[1]}`);
    expect(url).not.toContain("%5B%5D");
  });

  it("getLlmUsageSummary sends a multi-value array filter as repeated keys", async () => {
    mock.onGet("/api/admin/llm-usage/summary").reply(200, {
      groups: [],
      totals: {
        calls: 0,
        operations: 0,
        durationMs: 0,
        promptTokens: null,
        completionTokens: null,
        reasoningTokens: null,
        totalTokens: null,
        tokenMeasuredCalls: 0,
        promptTokensMeasured: 0,
        completionTokensMeasured: 0,
        reasoningTokensMeasured: 0,
        totalTokensMeasured: 0,
        failedCalls: 0,
        tokensPerSecond: null,
      },
    });

    await api.getLlmUsageSummary({ userIds, groupBy: ["kind"] });

    const url = axios.getUri(mock.history.get[0]);
    expect(url).toContain(`userIds=${userIds[0]}&userIds=${userIds[1]}`);
    expect(url).not.toContain("%5B%5D");
  });
});
