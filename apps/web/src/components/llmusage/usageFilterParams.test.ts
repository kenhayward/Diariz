import { describe, it, expect } from "vitest";
import { usageFilterFromParams, usageFilterToParams } from "./usageFilterParams";

const NOW = new Date("2026-08-18T12:00:00.000Z");

function parse(query: string) {
  return usageFilterFromParams(new URLSearchParams(query), NOW);
}

/// Deep-linking into the usage log. The link the model editor offers ("Open in usage log") is only useful
/// if it lands on the calls it names, and the page has to survive whatever ends up in the address bar -
/// hand-edited, stale after a rename, or truncated by a chat client.
describe("usageFilterFromParams", () => {
  it("is the ordinary default when the URL asks for nothing", () => {
    expect(parse("")).toEqual({
      preset: "7d",
      from: "2026-08-11T12:00:00.000Z",
      to: null,
      userIds: [],
      kinds: [],
      models: [],
      outcome: "all",
    });
  });

  it("takes the call kinds the URL names", () => {
    expect(parse("kinds=AdminTest,Tags").kinds).toEqual(["AdminTest", "Tags"]);
  });

  it("drops a kind that is not a real call kind", () => {
    // The API binds `kinds` as the enum and rejects a name it does not know, so passing one straight
    // through would turn a stale link into an error page rather than a slightly wrong filter.
    expect(parse("kinds=AdminTest,Nonsense").kinds).toEqual(["AdminTest"]);
  });

  it("keeps model names exactly as given", () => {
    // A model name is whatever the endpoint calls it: slashes, @ and : are all normal.
    expect(parse("models=" + encodeURIComponent("qwen3.8-27b@q4_k_xl")).models).toEqual([
      "qwen3.8-27b@q4_k_xl",
    ]);
  });

  it("accepts only the three outcomes the API knows", () => {
    expect(parse("outcome=failed").outcome).toBe("failed");
    expect(parse("outcome=sideways").outcome).toBe("all");
  });

  it("switches to a custom range when the URL names one", () => {
    const filter = parse("from=2026-01-01T00:00:00.000Z&to=2026-02-01T00:00:00.000Z");

    expect(filter.preset).toBe("custom");
    expect(filter.from).toBe("2026-01-01T00:00:00.000Z");
    expect(filter.to).toBe("2026-02-01T00:00:00.000Z");
  });

  it("ignores a from that is not a date rather than showing nothing", () => {
    // An unparseable date reaching the API as `from` would filter the whole table away, which reads as
    // "there is no usage" - a far worse answer than "here is the usual week".
    const filter = parse("from=yesterday");

    expect(filter.preset).toBe("7d");
    expect(filter.from).toBe("2026-08-11T12:00:00.000Z");
  });

  it("ignores parameters it does not know", () => {
    // ...and still returns a usable filter rather than an empty one.
    expect(parse("sort=cost&page=4")).toEqual(parse(""));
  });
});

describe("usageFilterToParams", () => {
  it("builds a link that parses back to the same filter", () => {
    // The two are only worth having as a pair: a link the page cannot read is a link to nowhere.
    const query = usageFilterToParams({ kinds: ["AdminTest"], models: ["qwen3.8-27b@q4_k_xl"] });

    const round = parse(query);
    expect(round.kinds).toEqual(["AdminTest"]);
    expect(round.models).toEqual(["qwen3.8-27b@q4_k_xl"]);
  });

  it("encodes a model name that would otherwise break the query string", () => {
    const query = usageFilterToParams({ models: ["openai/gpt-oss-20b&x=1"] });

    expect(parse(query).models).toEqual(["openai/gpt-oss-20b&x=1"]);
  });

  it("omits what it was not given", () => {
    const query = usageFilterToParams({ kinds: ["AdminTest"] });

    expect(query).toMatch(/kinds=AdminTest/);
    expect(query).not.toMatch(/models/);
  });
});
