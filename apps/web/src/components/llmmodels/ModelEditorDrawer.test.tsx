import { render, screen, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { LlmModel } from "../../lib/types";

// vi.mock's factory is hoisted above top-level const declarations, so the mock is created via vi.hoisted.
const { api } = vi.hoisted(() => ({
  api: {
    listModels: vi.fn(), updateModel: vi.fn(), createModel: vi.fn(), deleteModel: vi.fn(),
    testModel: vi.fn(), getTestRecording: vi.fn(), setTestRecording: vi.fn(), listRecordings: vi.fn(),
  },
}));
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import ModelEditorDrawer from "./ModelEditorDrawer";

const MODELS: LlmModel[] = [
  {
    id: "a", name: "gpt-oss-20b", displayName: null, apiBase: "http://a/v1", hasApiKey: true, chatEnabled: false, contextLength: 8192,
    parameters: { ModelBase: '{"temperature":0.5}' },
  },
  {
    id: "b", name: "qwen3-27b", displayName: null, apiBase: "http://b/v1", hasApiKey: false, chatEnabled: false, contextLength: 32768,
    parameters: { ModelBase: '{"temperature":0.9,"top_k":40}', Translation: '{"temperature":0.1}' },
  },
];

const OK_RESULT = {
  ok: true, httpStatus: 200, ttftMs: 310, durationMs: 1420,
  promptTokens: 1240, completionTokens: 44, reasoningTokens: 128, totalTokens: 1412,
  finishReason: "stop", response: "A short reply.", requestBodyJson: '{"model":"qwen3-27b"}',
  errorKind: null, message: null, offendingParameter: null, parsedKind: null, parsedJson: null,
};

/// The application defaults, as the page hands them over - the bottom of the layer stack.
const DEFAULTS = { ModelBase: '{"temperature":0.3,"timeout_seconds":120}' };

function open(model: LlmModel | null = MODELS[0], props: Record<string, unknown> = {}) {
  // Inside a router, as it always is in the app: the result card deep-links into the usage log.
  return render(
    <MemoryRouter>
      <ModelEditorDrawer
        model={model}
        allModels={MODELS}
        defaults={DEFAULTS}
        isDefaultModel={false}
        onClose={vi.fn()}
        onSaved={vi.fn()}
        onDeleted={vi.fn()}
        {...props}
      />
    </MemoryRouter>,
  );
}

describe("ModelEditorDrawer", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // Nothing remembered and nothing to pick, unless a test says otherwise. The drawer calls
    // getTestRecording on mount, so an unmocked one would reject in every test.
    api.getTestRecording.mockResolvedValue({ recordingId: null, title: null });
    api.listRecordings.mockResolvedValue([]);
    api.setTestRecording.mockResolvedValue(undefined);
  });

  it("offers one tab per parameter group, starting on Defaults", () => {
    open();

    const tabs = screen.getAllByRole("tab").map((t) => t.textContent);
    expect(tabs).toHaveLength(7);
    expect(tabs[0]).toMatch(/Defaults/);
    expect(screen.getByRole("tab", { selected: true }).textContent).toMatch(/Defaults/);
  });

  it("counts the overrides on each tab, and says nothing when there are none", () => {
    open(MODELS[1]);

    // ModelBase carries temperature and top_k; Translation carries temperature; the rest carry nothing.
    expect(screen.getByRole("tab", { name: /Defaults/ }).textContent).toMatch(/2/);
    expect(screen.getByRole("tab", { name: /Translation/ }).textContent).toMatch(/1/);
    expect(screen.getByRole("tab", { name: /Tags/ }).textContent).not.toMatch(/\d/);
  });

  it("scopes the parameter list to the open tab", () => {
    open(MODELS[1]);

    expect((screen.getByTestId("param-ModelBase-temperature") as HTMLInputElement).value).toBe("0.9");

    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    expect((screen.getByTestId("param-Translation-temperature") as HTMLInputElement).value).toBe("0.1");
  });

  it("shows what a group inherits from the model's own Defaults rather than leaving it blank", () => {
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Summaries/ }));

    // top_k is set on the model's Defaults and nowhere else, so Summaries inherits 40.
    expect(screen.getByText(/from Defaults · 40/)).toBeTruthy();
  });

  it("shows the application default on the Defaults tab, which has no model layer below it", () => {
    open(MODELS[0]);

    // temperature is overridden on this tab, so the row that proves the point is one the model leaves
    // alone: the timeout comes from the application defaults and nowhere else.
    expect(screen.getByText(/app default · 120/)).toBeTruthy();
  });

  it("previews the request body for the open tab, and follows what is typed", () => {
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Summaries/ }));

    const preview = () => screen.getByTestId("request-preview").textContent ?? "";
    expect(preview()).toMatch(/"temperature": 0\.9/);

    fireEvent.change(screen.getByTestId("param-Summaries-temperature"), { target: { value: "0.2" } });

    expect(preview()).toMatch(/"temperature": 0\.2/);
  });

  it("keeps the behaviour flags out of the previewed body", () => {
    // timeout_seconds governs the client, not the request. A preview that lists it sends the admin
    // hunting through their server logs for a field no endpoint ever received.
    open(MODELS[1]);

    expect(screen.getByTestId("request-preview").textContent).not.toMatch(/timeout_seconds/);
  });

  it("saves only the groups that carry an override", async () => {
    // An empty layer would create a row that decides nothing while looking, in the database, exactly like
    // a deliberate set of overrides.
    api.updateModel.mockResolvedValue(MODELS[1]);
    open(MODELS[1]);

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(Object.keys(api.updateModel.mock.calls[0][1].parameters).sort()).toEqual(["ModelBase", "Translation"]);
  });

  it("drops a group from the payload once its last override is returned to inherit", async () => {
    api.updateModel.mockResolvedValue(MODELS[1]);
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    fireEvent.click(screen.getAllByRole("button", { name: /back to inherited/i })[0]);
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(Object.keys(api.updateModel.mock.calls[0][1].parameters)).toEqual(["ModelBase"]);
  });

  it("sends no apiKey field when the key was left untouched", async () => {
    // The drawer is never given the stored key, so it must omit it rather than send an empty string -
    // empty means "clear the key" to the API.
    api.updateModel.mockResolvedValue(MODELS[0]);
    open(MODELS[0]);

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].apiKey).toBeUndefined();
  });

  it("saves a display name from the connection panel", async () => {
    api.updateModel.mockResolvedValue(MODELS[0]);
    open(MODELS[0]);

    // The connection fields sit behind a toggle for a saved model - they are set once and rarely touched.
    fireEvent.click(screen.getByRole("button", { name: /connection/i }));
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: "QWEN 3.8" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].displayName).toBe("QWEN 3.8");
  });

  it("sends a blank display name as null, so the slug shows through", async () => {
    // "" and null would be two spellings of the same state; the server stores one, so the client sends one.
    api.updateModel.mockResolvedValue(MODELS[0]);
    open({ ...MODELS[0], displayName: "Old Label" });

    fireEvent.click(screen.getByRole("button", { name: /connection/i }));
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].displayName).toBeNull();
  });

  it("counts a display-name edit as an unsaved change", () => {
    // `dirty` does not gate Save - it drives the status line and the discard prompt on close. A field
    // missing from that check makes the drawer report "no unsaved changes" while holding an edit, and
    // then throw it away on close without asking.
    open(MODELS[0]);
    expect(screen.getByText(/override\(s\) on/).textContent).not.toContain("unsaved");

    fireEvent.click(screen.getByRole("button", { name: /connection/i }));
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: "QWEN 3.8" } });

    expect(screen.getByText(/override\(s\) on/).textContent).toContain("unsaved");
  });

  it("copies another model's parameters into the open drawer without saving", () => {
    open(MODELS[0]);

    fireEvent.change(screen.getByLabelText(/copy parameters from/i), { target: { value: "b" } });

    expect((screen.getByTestId("param-ModelBase-temperature") as HTMLInputElement).value).toBe("0.9");
    expect(api.updateModel).not.toHaveBeenCalled();
  });

  it("copies a group override the target model does not have", () => {
    open(MODELS[0]);

    fireEvent.change(screen.getByLabelText(/copy parameters from/i), { target: { value: "b" } });
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    expect((screen.getByTestId("param-Translation-temperature") as HTMLInputElement).value).toBe("0.1");
  });

  it("never copies name, endpoint or key", () => {
    open(MODELS[0]);
    fireEvent.change(screen.getByLabelText(/copy parameters from/i), { target: { value: "b" } });

    fireEvent.click(screen.getByRole("button", { name: /connection/i }));

    // These are what make an entry distinct; copying them would produce a duplicate pointing elsewhere.
    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("gpt-oss-20b");
    expect((screen.getByLabelText(/endpoint/i) as HTMLInputElement).value).toBe("http://a/v1");
  });

  it("does not offer the model being edited as a copy source", () => {
    open(MODELS[0]);
    const options = Array.from(
      (screen.getByLabelText(/copy parameters from/i) as HTMLSelectElement).options,
    ).map((o) => o.value);

    expect(options).not.toContain("a");
    expect(options).toContain("b");
  });

  it("returns every override on the open tab to inherit at once", () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    open(MODELS[1]);

    fireEvent.click(screen.getByRole("button", { name: /reset all to inherit/i }));
    confirm.mockRestore();

    expect(screen.getByRole("tab", { name: /Defaults/ }).textContent).not.toMatch(/\d/);
    // ...and only that tab.
    expect(screen.getByRole("tab", { name: /Translation/ }).textContent).toMatch(/1/);
  });

  it("does not carry a half-typed value across to another tab", () => {
    // The rows are one component per parameter; without a per-group identity, switching tabs reuses the
    // same input and whatever was mid-edit would appear to belong to the call type just opened.
    open(MODELS[1]);
    fireEvent.change(screen.getByTestId("param-ModelBase-temperature"), { target: { value: "0." } });

    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    expect((screen.getByTestId("param-Translation-temperature") as HTMLInputElement).value).toBe("0.1");
  });

  it("warns before discarding unsaved overrides", () => {
    const onClose = vi.fn();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    open(MODELS[0], { onClose });

    fireEvent.change(screen.getByTestId("param-ModelBase-temperature"), { target: { value: "0.8" } });
    fireEvent.click(screen.getByRole("button", { name: /close/i }));

    expect(confirm).toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    confirm.mockRestore();
  });

  it("closes without a prompt when nothing was changed", () => {
    const onClose = vi.fn();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    open(MODELS[0], { onClose });

    fireEvent.click(screen.getByRole("button", { name: /close/i }));

    expect(confirm).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
    confirm.mockRestore();
  });

  it("runs the test against the layers on screen, not the ones on the server", () => {
    // Testing before saving is the whole reason the endpoint takes parameters at all.
    api.testModel.mockResolvedValue(OK_RESULT);
    open(MODELS[1]);
    // Translation, not a content group: those run against a real recording, which this test neither has nor
    // is about. Translation is still a non-base group, so group-over-base layering is exercised the same.
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));
    fireEvent.change(screen.getByTestId("param-Translation-temperature"), { target: { value: "0.2" } });

    fireEvent.click(screen.getByRole("button", { name: /run test/i }));

    // The whole layer set goes, exactly as Save sends it - the server walks the group it was given. What
    // matters is that Translation carries the UNSAVED 0.2 rather than the stored model's parameters.
    expect(api.testModel).toHaveBeenCalledWith("b", {
      group: "Translation",
      parameters: {
        ModelBase: '{"temperature":0.9,"top_k":40}',
        Translation: '{"temperature":0.2}',
      },
    });
  });

  it("keeps each tab's result so switching back does not lose it", async () => {
    // The results are not comparable across tabs - different parameters - so one shared slot would make
    // the rail show a number that belongs to a call type the admin is no longer looking at.
    api.testModel.mockResolvedValue(OK_RESULT);
    open(MODELS[1]);

    fireEvent.click(screen.getByRole("button", { name: /run test/i }));
    await screen.findByText(/0\.31 s/);

    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));
    expect(screen.queryByText(/0\.31 s/)).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: /Defaults/ }));
    expect(screen.getByText(/0\.31 s/)).toBeTruthy();
  });

  it("applies a one-click fix to the open tab only", async () => {
    api.testModel.mockResolvedValue({
      ...OK_RESULT, ok: false, httpStatus: 400, errorKind: "Http400",
      message: "top_k is not supported", offendingParameter: "top_k", ttftMs: null,
    });
    open(MODELS[1]);
    // Translation, not a content group: those need a recording to run at all, and this test is about where
    // a one-click fix lands, not about which call type ran.
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    fireEvent.click(screen.getByRole("button", { name: /run test/i }));
    const fix = await screen.findByRole("button", { name: /omit top k here/i });
    fireEvent.click(fix);

    // Omitted here, and the model's own Defaults - where top_k is genuinely set - left alone.
    expect(screen.getByTestId("param-Translation-top_k").textContent).toMatch(/omitted/i);
    fireEvent.click(screen.getByRole("tab", { name: /Defaults/ }));
    expect((screen.getByTestId("param-ModelBase-top_k") as HTMLInputElement).value).toBe("40");
  });

  it("saves an omitted parameter as null rather than dropping the key", async () => {
    // The distinction the whole editor is built on: absent means inherit, null means do not send.
    api.testModel.mockResolvedValue({
      ...OK_RESULT, ok: false, errorKind: "Http400", offendingParameter: "top_k", ttftMs: null,
    });
    api.updateModel.mockResolvedValue(MODELS[1]);
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    fireEvent.click(screen.getByRole("button", { name: /run test/i }));
    fireEvent.click(await screen.findByRole("button", { name: /omit top k here/i }));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(JSON.parse(api.updateModel.mock.calls[0][1].parameters.Translation)).toEqual({
      temperature: 0.1, top_k: null,
    });
  });

  it("will not test a model that does not exist yet", () => {
    // The endpoint is keyed by id and takes the endpoint and key from the stored row, so there is nothing
    // to test against until the model is saved. Saying why beats a button that fails.
    open(null);

    expect(screen.queryByRole("button", { name: /run test/i })).toBeNull();
    expect(screen.getByText(/save the model before running a test/i)).toBeTruthy();
  });

  it("asks for a recording on a content tab and will not run without one", async () => {
    open();

    await userEvent.click(screen.getByRole("tab", { name: /Tags/ }));

    const run = screen.getByRole("button", { name: /run test/i });
    expect(run.hasAttribute("disabled")).toBe(true);
    await userEvent.click(run);
    expect(api.testModel).not.toHaveBeenCalled();
  });

  it("keeps the built-in sample on a tab that has no real prompt", async () => {
    open();

    await userEvent.click(screen.getByRole("tab", { name: /Chat/ }));

    expect(screen.getByText(/built-in sample transcript/i)).toBeDefined();
    expect(screen.getByRole("button", { name: /run test/i }).hasAttribute("disabled")).toBe(false);
  });

  it("sends the remembered recording with the test call", async () => {
    api.getTestRecording.mockResolvedValue({ recordingId: "r1", title: "Quarterly planning" });
    api.testModel.mockResolvedValue(OK_RESULT);
    open();

    await userEvent.click(screen.getByRole("tab", { name: /Tags/ }));
    await screen.findByText("Quarterly planning");
    await userEvent.click(screen.getByRole("button", { name: /run test/i }));

    expect(api.testModel).toHaveBeenCalledWith("a", {
      group: "Tags",
      parameters: { ModelBase: '{"temperature":0.5}' },
      recordingId: "r1",
    });
  });

  it("remembers a newly chosen recording immediately", async () => {
    // Persisted on selection, not on run: an admin who picks a recording and then closes the drawer should
    // not have to pick it again.
    api.listRecordings.mockResolvedValue([
      {
        id: "r2", title: "2026-08-20 sync", name: "Budget review", source: "Microphone", durationMs: 60000,
        status: "Transcribed", createdAt: "2026-08-20T09:00:00Z", sectionId: null, sectionName: null,
        hasActions: false, hasAudio: true, calendarEventId: null,
      },
    ]);
    open();

    await userEvent.click(screen.getByRole("tab", { name: /Tags/ }));
    await userEvent.click(screen.getByRole("button", { name: /choose a recording/i }));
    await userEvent.click(await screen.findByText("Budget review"));

    expect(api.setTestRecording).toHaveBeenCalledWith("r2");
  });

  it("does not send a recording for a sample-transcript group", async () => {
    api.getTestRecording.mockResolvedValue({ recordingId: "r1", title: "Quarterly planning" });
    api.testModel.mockResolvedValue(OK_RESULT);
    open();

    await userEvent.click(screen.getByRole("tab", { name: /Chat/ }));
    await userEvent.click(screen.getByRole("button", { name: /run test/i }));

    expect(api.testModel).toHaveBeenCalledWith("a", {
      group: "Chat",
      parameters: { ModelBase: '{"temperature":0.5}' },
    });
  });
});
