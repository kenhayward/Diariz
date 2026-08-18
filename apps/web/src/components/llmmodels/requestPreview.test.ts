import { describe, it, expect } from "vitest";
import { buildRequestPreview, resolveInherited } from "./requestPreview";

/// The drawer's "Request sent" panel claims to be the request body. It therefore has to agree with the
/// API's `LlmRequestBody.Apply` exactly - a preview that lists a parameter the platform never sends sends
/// the administrator hunting through their server logs for something that was never there.
///
/// The layer walk it performs is the same four levels the server does, most specific first:
///   layers[group] -> layers.ModelBase -> defaults[group] -> defaults.ModelBase
/// and the first layer that MENTIONS a key decides it, whether it names a value or a null.
describe("buildRequestPreview", () => {
  const none = { ModelBase: {} };

  it("never puts the behaviour flags in the body", () => {
    // timeout_seconds, tools_supported, images_supported and reasoning_enabled govern the client, not the
    // request. Writing the layer into the body wholesale is the obvious implementation and it is wrong.
    const { body } = buildRequestPreview(
      "m",
      { ModelBase: { timeout_seconds: 600, tools_supported: false, images_supported: true, reasoning_enabled: true } },
      "ModelBase",
      none,
    );

    expect(Object.keys(body)).toEqual(["model"]);
  });

  it("reports the behaviour flags separately, with the platform's own fallbacks", () => {
    const { flags } = buildRequestPreview("m", none, "ModelBase", none);

    expect(flags).toEqual({
      reasoningEnabled: false,
      timeoutSeconds: 120,
      toolsSupported: true,
      imagesSupported: false,
    });
  });

  it("inherits a value the group layer does not set from the model's own Defaults", () => {
    const { body } = buildRequestPreview(
      "m",
      { ModelBase: { temperature: 0.5 }, Summaries: {} },
      "Summaries",
      none,
    );

    expect(body.temperature).toBe(0.5);
  });

  it("falls through to the app defaults when the model sets nothing", () => {
    // Without this the preview reads `{ "model": "..." }` on a fresh model while the real request carries
    // temperature 0.3 - the whole reason the defaults endpoint exists.
    const { body } = buildRequestPreview("m", none, "Summaries", { ModelBase: { temperature: 0.3 } });

    expect(body.temperature).toBe(0.3);
  });

  it("prefers the group's app default over the base app default", () => {
    // Translation genuinely runs cooler than everything else, and that lives in LlmDefaultsOptions.
    const { body } = buildRequestPreview("m", none, "Translation", {
      ModelBase: { temperature: 0.3 },
      Translation: { temperature: 0.1 },
    });

    expect(body.temperature).toBe(0.1);
  });

  it("drops a parameter omitted here even though a lower layer sets it", () => {
    // The load-bearing distinction: null is "stop looking and send nothing", not "keep looking".
    const { body } = buildRequestPreview(
      "m",
      { ModelBase: { top_k: 40, temperature: 0.5 }, Summaries: { top_k: null } },
      "Summaries",
      { ModelBase: { top_k: 20 } },
    );

    expect("top_k" in body).toBe(false);
    // ...and the walk really did run: an absence assertion alone would pass against an empty body.
    expect(body.temperature).toBe(0.5);
  });

  it("sends reasoning_effort only when reasoning resolves on", () => {
    const layers = { ModelBase: { reasoning_effort: "high" } };

    expect(buildRequestPreview("m", layers, "ModelBase", none).body.reasoning_effort).toBeUndefined();
    expect(
      buildRequestPreview("m", { ModelBase: { ...layers.ModelBase, reasoning_enabled: true } }, "ModelBase", none)
        .body.reasoning_effort,
    ).toBe("high");
  });

  it("treats a blank reasoning effort as nothing to send", () => {
    // Reachable by clearing the text box. The API's Text() reads whitespace as null rather than sending "".
    const { body } = buildRequestPreview(
      "m",
      { ModelBase: { reasoning_effort: "  ", reasoning_enabled: true, temperature: 0.5 } },
      "ModelBase",
      none,
    );

    expect("reasoning_effort" in body).toBe(false);
    expect(body.temperature).toBe(0.5);
  });

  it("names the model first, as the request does", () => {
    const { body } = buildRequestPreview("qwen3", { ModelBase: { temperature: 0.2 } }, "ModelBase", none);

    expect(Object.keys(body)[0]).toBe("model");
    expect(body.model).toBe("qwen3");
  });
});

/// What a row shows under its value when it is left inherited. Same walk as the preview, minus the layer
/// being edited - so it answers "what would happen if I changed nothing here".
describe("resolveInherited", () => {
  const none = { ModelBase: {} };

  it("credits the model's own Defaults before the application's", () => {
    const inherited = resolveInherited(
      { ModelBase: { temperature: 0.9 }, Summaries: { temperature: 0.2 } },
      { ModelBase: { temperature: 0.3 } },
      "Summaries",
    );

    // Not 0.2 - that is the layer being edited, and a row cannot inherit from itself.
    expect(inherited.temperature).toBe(0.9);
  });

  it("gives the Defaults tab the application defaults, never its own layer", () => {
    // On ModelBase the layer being edited IS the model's defaults, so including it would show every
    // override as its own inherited value and no row would ever look overridden.
    const inherited = resolveInherited({ ModelBase: { temperature: 0.9 } }, { ModelBase: { temperature: 0.3 } }, "ModelBase");

    expect(inherited.temperature).toBe(0.3);
  });

  it("prefers a group's application default over the base one", () => {
    const inherited = resolveInherited(none, { ModelBase: { temperature: 0.3 }, Translation: { temperature: 0.1 } }, "Translation");

    expect(inherited.temperature).toBe(0.1);
  });

  it("still credits the model's Defaults when the group has no layer of its own at all", () => {
    // A group with no overrides can reach here as a missing key rather than an empty object. Excluding
    // "the layer being edited" by POSITION rather than by name silently drops the model's own Defaults
    // when that happens, and the row then credits the application for a value the model set.
    const inherited = resolveInherited({ ModelBase: { temperature: 0.9 } }, { ModelBase: { temperature: 0.3 } }, "Summaries");

    expect(inherited.temperature).toBe(0.9);
  });

  it("reads a lower layer's omission as nothing set", () => {
    // The row would otherwise claim to inherit a value that is deliberately never sent.
    const inherited = resolveInherited(
      { ModelBase: { top_k: null, temperature: 0.5 }, Summaries: {} },
      { ModelBase: { top_k: 40 } },
      "Summaries",
    );

    expect(inherited.top_k ?? undefined).toBeUndefined();
    // ...and the walk really ran: an absence assertion alone passes against an empty result.
    expect(inherited.temperature).toBe(0.5);
  });
});
