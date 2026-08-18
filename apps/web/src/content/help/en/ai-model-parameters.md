---
title: Editing a model's parameters
summary: The model dialog sets a model's connection details and every sampling parameter. Each parameter is Inherit, Off, or a value, and Off is not the same as Inherit. Parameters can differ per call type and be copied between models.
group: advanced
order: 20
---

This is the dialog behind **Add model** and **Edit** on the AI models page. Everything here is
platform-wide: it decides how Diariz calls that model for every user.

## Connection

The four fields at the top are what it takes to reach the model.

- **Name** is sent to the endpoint exactly as written, as the `model` in each request. It has to match what
  that server calls the model, character for character. It is also the name you will see in the usage log.
- **Endpoint** is any OpenAI-compatible `/chat/completions` base URL, usually ending in `/v1`.
- **API key** is optional - a local endpoint normally needs none. It is encrypted at rest and write-only:
  once saved, the dialog shows that a key is stored but never shows the key again. Leaving the box blank on
  a later edit keeps the stored key; clearing it removes the key entirely.
- **Context length** is the model's context window in tokens. Diariz sizes every prompt against it, and the
  chat dial reports against it. Set it to what the model genuinely supports: too high and the endpoint
  rejects or silently truncates requests, too low and long meetings get trimmed.

Name and endpoint are what make an entry distinct, so **Copy from** never touches them.

## The three states

Every parameter is in one of three states, and the middle one is the one people get wrong.

| State | What it does |
|---|---|
| **Inherit** | This level says nothing. Whatever is configured below it decides. |
| **Off** | The parameter is left out of the request entirely. |
| **Set** | Use this value. |

**Off is not Inherit.** Inherit passes the question down; Off answers it with "send nothing", which
actively suppresses a value a lower level would have supplied. Reach for Off when an endpoint rejects a
parameter it does not recognise. If you only mean "I have no opinion", that is Inherit.

The control shows what Inherit currently resolves to, so you can see what you would be overriding before
you override it.

## Defaults and per-call-type overrides

The first panel, **Defaults**, is the model's own baseline. The panels after it - Tags, Actions, Summaries,
Minutes and formulas, Translation, Chat - are optional overrides for one kind of work.

They resolve most specific first:

1. the call type's own override
2. the model's Defaults panel
3. the application defaults that ship with Diariz

So you can leave everything on Defaults and drop the temperature for Translation alone, without repeating
the rest. A panel showing "no overrides" is not empty by accident - it means that call type simply uses
the model's defaults.

## Copying between models

**Copy from** loads another model's parameters into the open dialog for review. It copies parameters only,
never the name, endpoint or key, and nothing is saved until you press Save - so you can look at what
arrived and change your mind.

## Reasoning

**Send reasoning effort** decides whether `reasoning_effort` is sent at all. **Reasoning effort** is free
text rather than a list, because models disagree about what they accept: gpt-oss takes `low`, `medium` and
`high`, while Qwen3.8 takes `low`, `medium` and `xhigh`. Type whatever yours accepts.

Not every server honours the field. Some route it through their own on/off reasoning switch and ignore
levels they do not recognise, falling back to a default that may be the most expensive setting rather than
the one you asked for. If reasoning effort seems to make no difference, check your server's log for a
warning about an unsupported reasoning setting, and set the level in the server's own model configuration
instead.

## Token caps and a trap worth knowing

**Max tokens** and **Max completion tokens** cap the reply. On a reasoning model they cap the reasoning
too, and the reasoning is spent first.

That produces a specific and confusing failure: the call succeeds, the tokens are billed, and the answer
comes back **empty**, because the entire allowance went on thinking before any answer was written. It does
not look like an error anywhere - no exception, no failure status, just a blank result.

The usage log flags exactly this. A row that stopped this way carries a **Cut off** badge next to its
outcome, which stays "OK" because the call really did succeed. If you see blank summaries or empty chat
replies, that badge is the first place to look.

The safe default is to leave both token caps on Inherit, which sends no cap at all and lets the model
finish. Set one only when you have measured what that model's reasoning actually costs, and leave enough
room for an answer on top.

## Timeout

The request deadline in seconds. It is an idle allowance, not a cap on the whole reply: as long as output
keeps arriving the call continues, and only a silent gap trips it. Raise it for a large local model that
pauses while loading, and remember that a reverse proxy in front of Diariz has its own read timeout that
applies regardless of this setting.

## Tool calling and images

**Supports tool calling** tells Diariz whether this model can use chat tools. Turn it off for a model that
cannot, and tools are left out of the request rather than sent and ignored.

**Supports image input** is recorded for later use and is not read by anything yet.
