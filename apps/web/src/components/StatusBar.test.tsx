import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { StatusMessage } from "../lib/status";

// Controllable hook mocks.
let statusValue: StatusMessage | null = null;
let uploadValue: { busy: boolean; items: { status: string }[] } = { busy: false, items: [] };

vi.mock("../lib/status", () => ({ useStatus: () => ({ status: statusValue, setStatus: vi.fn() }) }));
vi.mock("../lib/uploadContext", () => ({ useUpload: () => uploadValue }));
vi.mock("../lib/api", () => ({
  api: {
    listRecordings: vi.fn(),
    getUserStorage: vi.fn(),
  },
}));

import { api } from "../lib/api";
import StatusBar from "./StatusBar";

function renderBar() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><StatusBar /></QueryClientProvider>);
}

describe("StatusBar", () => {
  beforeEach(() => {
    statusValue = null;
    uploadValue = { busy: false, items: [] };
    (api.getUserStorage as ReturnType<typeof vi.fn>).mockResolvedValue({
      usedBytes: 1024, quotaBytes: 5 * 1024 ** 3, totalTranscriptionMs: 3_661_000,
    });
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(
      Array.from({ length: 1234 }, () => ({ status: "Transcribed" })),
    );
  });

  it("shows storage, transcription usage, and the thousands-separated transcript count", async () => {
    renderBar();
    expect(await screen.findByText(/1,234 transcripts/)).toBeTruthy();
    expect(screen.getByText(/Storage 1 KB \/ 5 GB/)).toBeTruthy();
    expect(screen.getByText(/Transcription 1:01:01/)).toBeTruthy(); // 3,661,000 ms
  });

  it("shows an explicitly-pushed status over the derived one, in its tone colour", async () => {
    statusValue = { text: "Extracting actions…", tone: "progress" };
    renderBar();
    const msg = await screen.findByText("Extracting actions…");
    expect(msg.className).toContain("amber");
  });

  it("derives a pipeline message from the recordings list when nothing is pushed", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { status: "Transcribed" }, { status: "Transcribing" },
    ]);
    renderBar();
    expect(await screen.findByText("Transcribing…")).toBeTruthy();
  });

  it("shows an upload message while a batch is in flight", async () => {
    uploadValue = { busy: true, items: [{ status: "uploading" }, { status: "queued" }] };
    renderBar();
    expect(await screen.findByText(/Uploading 2/)).toBeTruthy();
  });
});
