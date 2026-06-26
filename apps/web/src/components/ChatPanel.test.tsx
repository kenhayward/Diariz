import { render, screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

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
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import ChatPanel from "./ChatPanel";

const rec = (id: string, title: string, status = "Transcribed") => ({
  id, title, name: null, source: "Microphone", durationMs: 1000, status,
  createdAt: "2026-01-01T00:00:00Z", sectionId: null, sectionName: null,
});

function renderPanel(route = "/recordings/rec-1") {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[route]}>
        <ChatPanel />
      </MemoryRouter>
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

  it("lets the user select multiple transcripts as context", async () => {
    renderPanel("/recordings/rec-1");
    fireEvent.click(await screen.findByRole("button", { name: /context:/i }));
    const menu = await screen.findByRole("menu");
    fireEvent.click(within(menu).getByText("Retro"));

    await ask("Compare them");

    await waitFor(() =>
      expect(api.chatStream).toHaveBeenCalledWith(
        expect.objectContaining({ recordingIds: expect.arrayContaining(["rec-1", "rec-2"]) }),
        expect.anything(),
      ),
    );
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
});
