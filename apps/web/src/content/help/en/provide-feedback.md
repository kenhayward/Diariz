---
title: Provide Feedback
summary: Use Provide Feedback in the account menu to report something that looks or behaves wrong, even without an error. A short technical trail is attached automatically. Only a platform administrator can read it, so do not paste meeting content.
group: settings
order: 30
---

Some problems never raise an error - a button that should be disabled but is not, a value that looks wrong,
a screen that does not match what you expected. **Provide Feedback**, in the account menu, is for exactly
that: a place to describe what you saw, whether or not anything actually broke.

## What to use it for

Describe what happened, what you expected instead, and roughly what you were doing at the time. There is no
right length - a sentence is fine if that is all it takes.

This is not a support chat. Nobody replies to you inside the app, and there is no history of your own past
submissions to look back on. Think of it as a note left for whoever maintains Diariz, not a conversation.

## What is attached automatically

A short technical trail of the app's own recent activity goes with your report - the last couple of API
calls and page changes leading up to it. This is what usually turns "something looked wrong" into something
a maintainer can actually track down, since it shows what the app just did without you having to remember or
explain it.

The trail is scrubbed in the browser before it is ever sent: things like access tokens and other sensitive
values are removed at the point they are recorded, not afterward. It never includes your typed feedback text
itself, only a record of app activity such as which screen you were on and which requests the app made.

Screenshots are not part of this yet. That is a planned addition, not something you can attach today.

## Who can read it

Submitted feedback is readable and deletable only by a **platform administrator** - not by you, and not by
any other regular user, even for your own submissions once you have sent them. There is no per-user feedback
list anywhere in the app.

Some platforms also route new feedback into an outside system, such as a ticket tracker, through an
automation a platform administrator sets up. Whether or not that is configured, the same rule applies: only a
platform administrator, or a system they have deliberately connected, ever sees what you wrote.

## Do not paste meeting content

The description you type is stored as plain text, exactly as you write it, alongside the technical trail.
Because a platform administrator can read it, treat it the same way you would treat an email to your IT
department: fine for describing a problem, not the place to paste transcript text, quotes from a meeting, or
anything else you would not want a platform administrator to see. If a meeting itself is the problem, describe
what looked wrong and where, rather than pasting the content in question.
