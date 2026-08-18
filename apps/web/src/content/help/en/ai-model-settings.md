---
title: AI and model settings
summary: Summaries, minutes, actions, tags, chat, and formulas all need an OpenAI-compatible endpoint. A Platform Administrator configures the models for everyone on the AI models page, where a grid decides which model runs which kind of call.
group: settings
order: 10
---

Transcription and speaker separation always work. Everything that needs a language model does not,
until a model is configured.

Features that need one: summaries, automatic naming, meeting minutes, action-item extraction, tags,
chat, translation, and formulas.

Models are set up **once, for the whole platform**, by a Platform Administrator. There is no per-account
model setting - if the AI features are not working, or are answering differently from how you expect,
that is a question for your administrator rather than something to fix in your own Preferences.

## Configuring models

A Platform Administrator opens **Settings**, **AI**, then **Manage AI models**, which opens over the
settings window rather than in a new tab - so the installed app and the desktop app stay signed in. The
address `/admin/llm-models` still works if you have it bookmarked. Each model there carries:

- **A name.** Sent to the endpoint exactly as written, so it must match what that endpoint calls the
  model.
- **An endpoint.** Any OpenAI-compatible `/chat/completions` URL. This includes hosted providers and
  local runtimes such as LM Studio or Ollama.
- **An API key**, if the endpoint needs one. It is encrypted at rest and write-only: once saved, Diariz
  says a key is set but never shows it back.
- **A context window** - see below.

A server that has an endpoint set in its environment but no models configured keeps working exactly as
it did. The page offers to import that endpoint as your first model when you are ready.

## Parameters

Every model has a full set of parameters: temperature, top P, top K, repeat penalty, frequency and
presence penalties, max tokens, max completion tokens, reasoning effort, the request timeout, and
whether the model supports tool calling or image input.

Each one is in **one of three states**, and two of them are easy to confuse:

- **Inherited** - this level says nothing, so whatever is configured below it decides. This is what a
  row does if you leave it alone, and it names what it inherits and from where.
- **Omitted** - the parameter is left out of the request **entirely**. This is not the same as
  inheriting: it actively suppresses a value a lower level would have supplied. Use it for an endpoint
  that rejects a parameter it does not recognise.
- **Set** - use this value. Typing in the row's value box is what sets it; there is no button to press
  first.

**Reasoning effort is free text**, not a fixed list, because models disagree about what they accept:
gpt-oss takes `low`, `medium` and `high`, qwen3 also takes `xhigh`, and the next model will take
something else again. Type whatever yours accepts.

## Different settings per job

Parameters can differ by what the model is being asked to do. Alongside a model's own defaults, each of
**tag extraction**, **action extraction**, **summaries**, **minutes and formulas**, **translation** and
**chat** has its own optional overrides.

They resolve most-specific-first: the job's override, then the model's defaults, then the application
defaults. So you can leave everything on the model's defaults and drop the temperature for translation
alone, without repeating the rest.

Each call type is a tab in the model's drawer, carrying a count of how many parameters it overrides, and
a panel on the right shows the exact request body that tab would send.

To reuse a set of parameters, use **Copy parameters from** in the drawer. It copies parameters only -
never the name, endpoint or key - and nothing is saved until you press Save, so you can review what
arrived first.

## Different models per job

Each job can also run on a **different model** entirely. Point tag extraction at something small and
fast while summaries go to a larger model.

The AI models page is a grid: your models down the side, the call types across the top, and one selection
per column. Click a cell to move that call type to that model. The last row, **No model**, is how a call
type goes back to following the default - and how the default itself goes back to the endpoint configured
in the server environment.

Following the default is not the same as being pointed at whichever model is currently the default: a
call type in the **No model** row moves with the default if you change it, while one pointed at a model
stays where you put it.

Each row has a **Test** button that checks the model is reachable - the endpoint, the key and the model
name - and **Test all** in the footer runs them one at a time.

A model that is still in use cannot be deleted. Diariz refuses and names the jobs still pointing at it,
so you know exactly what to move first. **Delete model** lives at the foot of the model's own drawer.

## The context window

Diariz sends your meeting content to the model in one request, and every model has a limit on how much
it can hold at once - its context window, measured in tokens. A model's setting tells Diariz what that
limit is, and everything else follows from it: summaries, minutes, action items, tags, folder roll-ups,
formula runs and chat all size what they send against the same number. The dial in chat reports against
the window of whichever model is serving chat.

Diariz spends about 60% of the window on meeting content and leaves the rest for the instructions and
the model's reply, which counts against the same window.

Set it to what the model genuinely supports. Too high and the endpoint will reject or silently truncate
requests. Too low and long meetings get trimmed - and a folder roll-up will stop part-way through,
summarising only the meetings that fit and leaving the rest out.

## Response timeout

The timeout is one of the parameters above, in seconds, and can be set per model and per job.

Raise it if a large local model is getting cut off partway through a reply. That is what it is for.

It is not a cap on how long a whole reply may take. It is an idle allowance, measured between each
piece of a reply as it streams in - as long as the model keeps producing output, however slowly, the
reply keeps going. Only a gap with nothing at all coming through trips it.

Very large values have a limit outside Diariz: if a reverse proxy sits in front of the server, its own
read timeout still applies (the bundled one allows an hour), so a request can be cut off there no matter
what you set here - raise the proxy's read timeout to match if you need longer.

One consequence: the allowance also has to cover the time before the model produces its first piece of
output at all, including a cold model that has to load before it can answer. If the timeout is shorter
than the model's worst-case load time, a cold start looks exactly like a stuck reply and gets cut off -
so set it above that worst case, not just above how long a typical reply takes once the model is warm.

## Embeddings

A separate embeddings endpoint powers semantic search, and is configured in the server environment
rather than on the models page. Setting it turns panel search, chat, and the chat tools into hybrid
keyword-plus-meaning search. Without it, search stays keyword-only. The model name must match the
endpoint's exact identifier, and the base URL usually ends in `/v1`.

When no dedicated embeddings endpoint is set, embeddings reuse whichever model the platform is
otherwise using, including its key and timeout.

## Other administrator settings

A Platform Administrator can also choose whether minutes generate with one call per section (better
structure) or a single call (fewer tokens), and switch API access, Claude/MCP, and Automations on or off
independently.

## LLM usage log

The AI settings also carry an **LLM usage log**: a master switch, a retention window in days (0 keeps
rows forever), and a toggle asking streaming replies for a token count. Every outbound call the platform
makes to a model - summaries, minutes, chat, formulas, and the rest - is recorded here: who it was for,
which model answered, how long it took, and how many tokens it used. The prompt and reply themselves are
never stored, only counts and sizes.

A **View usage log** link opens the log at `/admin/llm-usage`, Platform Administrator only. It has three
views - **Operations** (one row per user-facing action, with how many model calls it took), **Calls**
(every individual call), and **Summary** (rolled up by user, model, or call type) - with a filter bar for
date range, user, call type, model, and outcome, and a totals row that always reflects the whole filter,
not just what's on screen. Deleting rows there deletes them for good, and the confirmation tells you
exactly how many rows are about to go before you commit to it.
