import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { useEffect } from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { SelectionProvider, useSelection } from "../lib/selection";

vi.mock("../lib/api", () => ({
  api: {
    listRecordings: vi.fn(),
    getUserSettings: vi.fn(),
    chatStream: vi.fn(),
    uploadChatAttachment: vi.fn(),
    listChatConversations: vi.fn(),
    getChatConversation: vi.fn(),
    createChatConversation: vi.fn(),
    updateChatConversation: vi.fn(),
    deleteChatConversation: vi.fn(),
    listFormulas: vi.fn(),
    runFormula: vi.fn(),
    getFormulaResultText: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import ChatPanel from "./ChatPanel";

const rec = (id: string, title: string, status = "Transcribed") => ({
  id, title, name: null, source: "Microphone", durationMs: 1000, status,
  createdAt: "2026-01-01T00:00:00Z", sectionId: null, sectionName: null,
});

/// Seeds the shared selection (as the list's Select mode would) before ChatPanel renders.
function Seed({ ids }: { ids: string[] }) {
  const sel = useSelection();
  useEffect(() => {
    sel.set(ids);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return null;
}

function renderPanel(route = "/recordings/rec-1", seedSelected: string[] = []) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SelectionProvider>
        <MemoryRouter initialEntries={[route]}>
          {seedSelected.length > 0 && <Seed ids={seedSelected} />}
          <ChatPanel />
        </MemoryRouter>
      </SelectionProvider>
    </QueryClientProvider>,
  );
}

const mock = (fn: unknown) => fn as ReturnType<typeof vi.fn>;

describe("ChatPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.listRecordings).mockResolvedValue([rec("rec-1", "Standup"), rec("rec-2", "Retro", "Summarized")]);
    mock(api.getUserSettings).mockResolvedValue({
      apiBase: null, model: "gpt-oss", hasApiKey: false, defaultApiBase: null, defaultModel: "gpt-oss",
      serverHasApiKey: false, contextWindow: null, defaultContextWindow: 131072,
    });
    mock(api.chatStream).mockImplementation(async (_body: any, h: any) => {
      h.onMeta?.({ model: "gpt-oss", contextUsed: 10, contextTotal: 100 });
      h.onToken("Hello ");
      h.onToken("world");
      return { model: "gpt-oss", contextUsed: 12, contextTotal: 100 };
    });
    mock(api.createChatConversation).mockResolvedValue({ id: "conv-1", title: "Standup Recap" });
    mock(api.deleteChatConversation).mockResolvedValue(undefined);
    mock(api.listChatConversations).mockResolvedValue([]);
  });

  async function ask(text: string) {
    const box = screen.getByLabelText("Chat message");
    // Wrap in act so the streaming promise chain (onMeta/onToken/finally) settles inside act.
    await act(async () => {
      fireEvent.focus(box); // focusing snapshots the inferred context (as a real user does before typing)
      fireEvent.change(box, { target: { value: text } });
      fireEvent.click(screen.getByRole("button", { name: /^send$/i }));
    });
  }

  it("shows the context dial with the configured total before any message is sent", async () => {
    renderPanel("/recordings/rec-1");
    // 0 used out of the 131,072 server default, shown inline from the start.
    expect(await screen.findByText(/0 \/ 131,072 \(0%\)/)).toBeTruthy();
  });

  it("streams a reply, defaults context to the open recording, and shows the dial", async () => {
    renderPanel("/recordings/rec-1");
    await ask("Who spoke?");

    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());
    expect(api.chatStream).toHaveBeenCalledWith(
      expect.objectContaining({ recordingIds: ["rec-1"], messages: [{ role: "user", content: "Who spoke?" }] }),
      expect.anything(),
    );
    // The context dial appears once usage is known.
    await waitFor(() => expect(screen.getByLabelText(/Context \d+% used/)).toBeTruthy());
  });

  it("infers the shared selection (2+ ticked) as the context, no menu choice needed", async () => {
    renderPanel("/recordings/rec-1", ["rec-1", "rec-2"]);

    await ask("Compare them");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ recordingIds: ["rec-1", "rec-2"] }),
        expect.anything(),
      ),
    );
    // The pill reflects the inferred multi-selection.
    expect(screen.getByRole("button", { name: /selected transcripts/i })).toBeTruthy();
  });

  it("infers the open folder as the context (sends a section id)", async () => {
    renderPanel("/sections/sec-1");

    await ask("Summarise this folder");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ sectionId: "sec-1", recordingIds: [] }),
        expect.anything(),
      ),
    );
    expect(screen.getByRole("button", { name: /current folder/i })).toBeTruthy();
  });

  it("sends no transcript context when None is chosen", async () => {
    renderPanel("/recordings/rec-1");
    fireEvent.click(await screen.findByRole("button", { name: /context:/i }));
    fireEvent.click(await screen.findByRole("menuitemradio", { name: /none/i }));

    await ask("General question");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ recordingIds: [] }),
        expect.anything(),
      ),
    );
  });

  it("refreshes the saved-conversations list after saving, so a new one appears in an open dropdown", async () => {
    mock(api.listChatConversations).mockResolvedValue([]); // initially empty
    renderPanel("/recordings/rec-1");
    await ask("Summarise");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    // Open the dropdown (empty) and leave it open.
    await act(async () => fireEvent.click(screen.getByRole("button", { name: /saved conversations/i })));
    expect(screen.getByText(/no saved conversations/i)).toBeTruthy();

    // After saving, the server now lists the conversation.
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-1", title: "Standup Recap", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    await act(async () => fireEvent.click(screen.getByRole("button", { name: /save conversation/i })));
    await waitFor(() => expect(api.createChatConversation).toHaveBeenCalled());

    // The still-open dropdown reflects the save without re-toggling.
    await waitFor(() => expect(screen.getByRole("button", { name: "Standup Recap" })).toBeTruthy());
  });

  it("closes the saved-conversations dropdown on an outside click", async () => {
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-1", title: "Old Chat", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    renderPanel("/recordings/rec-1");

    await act(async () => fireEvent.click(await screen.findByRole("button", { name: /saved conversations/i })));
    expect(await screen.findByRole("button", { name: "Old Chat" })).toBeTruthy();

    fireEvent.mouseDown(document.body); // click outside the dropdown
    await waitFor(() => expect(screen.queryByRole("button", { name: "Old Chat" })).toBeNull());
  });

  it("saves the conversation then enables and performs delete", async () => {
    renderPanel("/recordings/rec-1");
    await ask("Summarise");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /save conversation/i })));
    await waitFor(() => expect(api.createChatConversation).toHaveBeenCalled());
    expect(screen.getByText("Saved")).toBeTruthy();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /delete conversation/i })));
    await waitFor(() => expect(api.deleteChatConversation).toHaveBeenCalledWith("conv-1"));
  });

  it("saves a folder chat with its section id", async () => {
    renderPanel("/sections/sec-1");
    await ask("Summarise this folder");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /save conversation/i })));

    await waitFor(() =>
      expect(api.createChatConversation).toHaveBeenCalledWith(
        expect.objectContaining({ context: expect.objectContaining({ sectionId: "sec-1", recordingIds: [] }) }),
      ),
    );
  });

  it("reopening a saved folder chat restores its folder context", async () => {
    mock(api.listChatConversations).mockResolvedValue([
      { id: "conv-f", title: "Folder Chat", updatedAt: "2026-01-01T00:00:00Z" },
    ]);
    mock(api.getChatConversation).mockResolvedValue({
      id: "conv-f", title: "Folder Chat", updatedAt: "2026-01-01T00:00:00Z",
      messages: [{ role: "user", content: "hi" }, { role: "assistant", content: "hey" }],
      context: { recordingIds: [], sectionId: "sec-9", attachmentName: null, attachmentText: null },
    });
    renderPanel("/recordings/rec-1"); // start somewhere else

    await act(async () => fireEvent.click(await screen.findByRole("button", { name: /saved conversations/i })));
    await act(async () => fireEvent.click(await screen.findByRole("button", { name: "Folder Chat" })));

    expect(await screen.findByRole("button", { name: /current folder/i })).toBeTruthy();
    await ask("And the action items?");
    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ sectionId: "sec-9", recordingIds: [] }),
        expect.anything(),
      ),
    );
  });

  it("clears the conversation thread with the Clear button", async () => {
    renderPanel("/recordings/rec-1");
    await ask("Tell me something");
    await waitFor(() => expect(screen.getByText("world", { exact: false })).toBeTruthy());

    await act(async () => fireEvent.click(screen.getByRole("button", { name: /clear conversation/i })));

    expect(screen.queryByText("world", { exact: false })).toBeNull();
  });

  it("attaches a file and includes its text in the request", async () => {
    mock(api.uploadChatAttachment).mockResolvedValue({ name: "spec.pdf", text: "blue widget", chars: 11 });
    const { container } = renderPanel("/recordings/rec-1");

    const fileInput = container.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(["%PDF"], "spec.pdf", { type: "application/pdf" });
    await act(async () => fireEvent.change(fileInput, { target: { files: [file] } }));

    await waitFor(() => expect(screen.getByText("spec.pdf")).toBeTruthy());
    await ask("Does it match?");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ attachmentName: "spec.pdf", attachmentText: "blue widget" }),
        expect.anything(),
      ),
    );
  });

  describe("/formula command", () => {
    const formula = {
      id: "f1", scope: "Personal", ownerUserId: "u1", name: "Follow-up email",
      description: null, prompt: "Draft a follow-up.", context: 1, enabled: true, isBuiltIn: false,
    };

    beforeEach(() => {
      mock(api.listFormulas).mockResolvedValue([formula]);
      mock(api.runFormula).mockResolvedValue({
        id: "res-1", recordingId: "rec-1", name: "Follow-up email",
        createdByUserId: "u1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
      });
      mock(api.getFormulaResultText).mockResolvedValue("**Thanks for joining.**");
    });

    it("runs the matching formula on the open recording and shows the markdown result", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      await waitFor(() => expect(api.runFormula).toHaveBeenCalledWith("rec-1", "f1"));
      expect(api.getFormulaResultText).toHaveBeenCalledWith("rec-1", "res-1");
      await waitFor(() => expect(screen.getByText(/Thanks for joining/)).toBeTruthy());
    });

    it("does not send the command text to the model", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula Follow-up email");

      await waitFor(() => expect(api.runFormula).toHaveBeenCalled());
      expect(api.chatStream).not.toHaveBeenCalled();
    });

    it("shows a message and skips the lookup when no recording is open", async () => {
      renderPanel("/sections/sec-1");
      await ask("/formula Follow-up email");

      expect(await screen.findByText(/open a recording first/i)).toBeTruthy();
      expect(api.listFormulas).not.toHaveBeenCalled();
    });

    it("shows a not-found message when no formula matches the name", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula Nonexistent");

      expect(await screen.findByText(/no formula named/i)).toBeTruthy();
      expect(api.runFormula).not.toHaveBeenCalled();
    });

    it("does not run bare /formula with no name (sends it as a normal message instead)", async () => {
      renderPanel("/recordings/rec-1");
      await ask("/formula");

      await waitFor(() => expect(api.chatStream).toHaveBeenCalled());
      expect(api.listFormulas).not.toHaveBeenCalled();
    });
  });
});
