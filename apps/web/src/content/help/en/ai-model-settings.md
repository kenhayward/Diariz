---
title: AI and model settings
summary: Summaries, minutes, actions, tags, chat, and formulas all need an OpenAI-compatible endpoint. Set your own in Settings, or use the server default an administrator configured.
group: settings
order: 10
---

Transcription and speaker separation always work. Everything that needs a language model does not,
until an endpoint is configured.

Features that need one: summaries, automatic naming, meeting minutes, action-item extraction, tags,
chat, translation, and formulas.

## Configuring it

A Platform Administrator sets the platform-wide one at **Settings**, **Model Settings**. Your own
override, if you want one, is at **Preferences**, **Assistant**, on the **Model** card - click
**Change...**. Either way you give Diariz:

- **An API base URL.** Any OpenAI-compatible `/chat/completions` endpoint. This includes hosted
  providers and local runtimes such as LM Studio or Ollama.
- **A model name.** This must match exactly what the endpoint calls the model.
- **An API key**, if the endpoint needs one.

Your key is **encrypted at rest**, and it is write-only: once saved, Diariz will tell you a key is set
but will never show it back to you.

## Your settings versus the server's

Each field falls back independently. Diariz uses your value if you set one, and the server default
otherwise. That means you can point only the model name at something different while still using the
server's endpoint and key.

If neither you nor the server has an endpoint configured, the AI features return a clear error rather
than failing silently.

## The context window

Diariz sends your meeting content to the model in one request, and every model has a limit on how much
it can hold at once - its context window, measured in tokens. One setting tells Diariz what that limit
is, and everything else follows from it: summaries, minutes, action items, tags, folder roll-ups,
formula runs and chat all size what they send against the same number.

A Platform Administrator sets it server-wide; you can override it for yourself on the **Assistant**
tab in Preferences. Diariz spends about 60% of the window on meeting content and leaves the rest for
the instructions and the model's reply, which counts against the same window.

Set it to what your model genuinely supports. Too high and your endpoint will reject or silently
truncate requests. Too low and long meetings get trimmed - and a folder roll-up will stop part-way
through, summarising only the meetings that fit and leaving the rest out.

## Embeddings

A separate embeddings endpoint powers semantic search. Configuring it turns panel search, chat, and the
chat tools into hybrid keyword-plus-meaning search. Without it, search stays keyword-only. The model
name must match the endpoint's exact identifier, and the base URL usually ends in `/v1`.

## Administrator settings

A Platform Administrator can also set a **global AI timeout**, choose whether minutes generate with one
call per section (better structure) or a single call (fewer tokens), and switch API access, Claude/MCP,
and Automations on or off independently.
