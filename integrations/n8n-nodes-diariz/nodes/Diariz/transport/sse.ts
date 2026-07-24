export interface SseReference {
  name: string;
  href: string;
}

export interface SseResult {
  answer: string;
  references: SseReference[];
  model?: string;
}

interface SseFrame {
  type?: string;
  value?: string;
  name?: string;
  href?: string;
  message?: string;
  model?: string;
}

/// Consumes Diariz's chat stream (POST /api/chat/stream, text/event-stream) and returns the finished answer,
/// so a workflow author never sees the streaming transport. Frames are `data: {json}\n\n` with a `type` of
/// token / meta / tool_start / tool_end / ref / done / error - see ChatController.WriteEventAsync.
export async function accumulateSse(stream: AsyncIterable<Buffer | string>): Promise<SseResult> {
  let buffer = "";
  let answer = "";
  let model: string | undefined;
  const references: SseReference[] = [];

  const handle = (frame: SseFrame): boolean => {
    switch (frame.type) {
      case "token":
        answer += frame.value ?? "";
        return false;
      case "ref":
        if (frame.name && frame.href) references.push({ name: frame.name, href: frame.href });
        return false;
      case "done":
        model = frame.model ?? model;
        return true;
      case "error":
        throw new Error(frame.message ?? "The chat stream reported an error.");
      default:
        // meta, tool_start, tool_end and anything added later are progress noise for a workflow.
        if (frame.model) model = frame.model;
        return false;
    }
  };

  for await (const chunk of stream) {
    buffer += typeof chunk === "string" ? chunk : chunk.toString("utf8");

    let split = buffer.indexOf("\n\n");
    while (split !== -1) {
      const raw = buffer.slice(0, split);
      buffer = buffer.slice(split + 2);

      const payload = raw.startsWith("data:") ? raw.slice(5).trim() : raw.trim();
      if (payload.length > 0) {
        let frame: SseFrame | undefined;
        try {
          frame = JSON.parse(payload) as SseFrame;
        } catch {
          frame = undefined; // a malformed frame is not worth failing a finished answer over
        }
        if (frame && handle(frame)) return { answer, references, model };
      }

      split = buffer.indexOf("\n\n");
    }
  }

  return { answer, references, model };
}
