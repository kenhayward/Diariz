using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class WebhookPublisherTests
{
    /// <summary>A PERSONAL subscription (the default scope). Do not reach for this when the behaviour under
    /// test only exists for platform subscriptions - use <see cref="PlatformSub"/>, or the test asserts against
    /// a scope/event-type combination production can never produce.</summary>
    private static WebhookSubscription Sub(Guid owner, string events, bool active = true) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = owner, Name = "s", Url = "https://x/y",
        SecretEncrypted = "c", EventTypes = events, IsActive = active,
    };

    /// <summary>A PLATFORM subscription. <paramref name="signalFilter"/> defaults to a real signal because
    /// that is what production looks like: <c>PlatformWebhooksController.Validate</c> refuses to create a
    /// platform subscription with an empty filter.</summary>
    private static WebhookSubscription PlatformSub(string events, string? signalFilter = "post-to-slack") => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform, Name = "plat",
        Url = "https://x/y", SecretEncrypted = "c", EventTypes = events, SignalFilter = signalFilter,
        IsActive = true,
    };

    /// <summary>The contacts gate. An automation posts to an arbitrary URL, so attendees' email addresses
    /// and phone numbers only go to a subscription whose owner deliberately ticked the box - everything
    /// else gets the contact-free body from the same event.</summary>
    [Fact]
    public async Task Contacts_reach_only_the_subscription_that_opted_in()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var optedIn = Sub(owner, "recording.summarized");
        optedIn.IncludeAttendeeContacts = true;
        db.Webhooks.Add(optedIn);
        db.Webhooks.Add(Sub(owner, "recording.summarized")); // default: off
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingSummarized, owner,
            new { recordingId = Guid.NewGuid(), attendees = new[] { new { name = "Ada" } } },
            dataWithContacts: new { recordingId = Guid.NewGuid(), attendees = new[] { new { name = "Ada", email = "ada@example.com" } } });

        var bodies = await db.WebhookDeliveries.ToDictionaryAsync(d => d.SubscriptionId, d => d.PayloadJson);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("ada@example.com", bodies[optedIn.Id]);
        Assert.DoesNotContain("ada@example.com", bodies.Single(b => b.Key != optedIn.Id).Value);
    }

    /// <summary>Both deliveries of one event must keep the same event id, or a subscriber deduplicating on
    /// it would treat the pair as two different meetings.</summary>
    [Fact]
    public async Task Contacts_body_shares_the_event_id_with_the_plain_one()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var optedIn = Sub(owner, "recording.summarized");
        optedIn.IncludeAttendeeContacts = true;
        db.Webhooks.Add(optedIn);
        db.Webhooks.Add(Sub(owner, "recording.summarized"));
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingSummarized, owner,
            new { recordingId = Guid.NewGuid() }, dataWithContacts: new { recordingId = Guid.NewGuid() });

        Assert.Single((await db.WebhookDeliveries.ToListAsync()).Select(d => d.EventId).Distinct());
    }

    /// <summary>Without a contact-bearing body, the opt-in changes nothing - it must not fall back to some
    /// other shape.</summary>
    [Fact]
    public async Task Opting_in_without_a_contacts_body_still_gets_the_plain_one()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var optedIn = Sub(owner, "formula_result.completed");
        optedIn.IncludeAttendeeContacts = true;
        db.Webhooks.Add(optedIn);
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.FormulaResultCompleted, owner, new { marker = "plain" });

        Assert.Contains("plain", (await db.WebhookDeliveries.SingleAsync()).PayloadJson);
    }

    [Fact]
    public async Task Publishes_one_delivery_per_matching_active_subscription()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.Webhooks.Add(Sub(owner, "recording.transcribed,formula_result.completed"));   // matches
        db.Webhooks.Add(Sub(owner, "recording.created"));                                 // wrong type
        db.Webhooks.Add(Sub(owner, "recording.transcribed", active: false));             // inactive
        db.Webhooks.Add(Sub(other, "recording.transcribed"));                            // wrong owner
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingTranscribed, owner,
            new { recordingId = Guid.NewGuid(), status = "Transcribed" });

        var deliveries = await db.WebhookDeliveries.ToListAsync();
        Assert.Single(deliveries);
        Assert.Equal(WebhookEventTypes.RecordingTranscribed, deliveries[0].EventType);
        Assert.Equal(WebhookDeliveryStatus.Pending, deliveries[0].Status);
        Assert.Contains("\"type\":\"recording.transcribed\"", deliveries[0].PayloadJson);
        Assert.False(string.IsNullOrEmpty(deliveries[0].EventId));
    }

    [Fact]
    public async Task No_subscribers_inserts_nothing()
    {
        var db = TestDb.Create();
        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingCreated, Guid.NewGuid(), new { });
        Assert.Empty(await db.WebhookDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Platform_sub_matches_by_signal_and_gets_inline_output_for_formula_events()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        // A personal sub owned by someone else, and a platform sub routed on "post-to-slack".
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Personal,
            Name = "p", Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed", IsActive = true });
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform,
            Name = "plat", Url = "https://x/z", SecretEncrypted = "c", EventTypes = "formula_result.completed",
            SignalFilter = "post-to-slack", IsActive = true });
        // An empty-filter platform sub must NOT match.
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform,
            Name = "plat-empty", Url = "https://x/w", SecretEncrypted = "c", EventTypes = "formula_result.completed",
            SignalFilter = null, IsActive = true });
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync("formula_result.completed", owner,
            data: new { formulaResultId = Guid.NewGuid(), status = "Ready" },
            signals: new[] { "post-to-slack" },
            platformData: new { formulaResultId = Guid.NewGuid(), status = "Ready", output = "the output", formulaName = "F" });

        var deliveries = await db.WebhookDeliveries.Include(d => d.Subscription).ToListAsync();
        // Only the signal-matched platform sub fires (owner has no personal sub; other personal belongs to someone else).
        Assert.Single(deliveries);
        Assert.Equal(WebhookScope.Platform, deliveries[0].Subscription!.Scope);
        Assert.Contains("\"output\":\"the output\"", deliveries[0].PayloadJson); // inline output embedded
    }

    [Fact]
    public async Task Personal_delivery_stays_thin_even_when_platform_data_is_supplied()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = owner, Scope = WebhookScope.Personal,
            Name = "mine", Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed", IsActive = true });
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync("formula_result.completed", owner,
            data: new { formulaResultId = Guid.NewGuid(), status = "Ready" },
            signals: new[] { "post-to-slack" },
            platformData: new { output = "secret output" });

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.DoesNotContain("output", d.PayloadJson); // personal never embeds output
    }

    /// <summary>A personal subscription receives events about its OWNER's own data. Feedback is readable only
    /// by a Platform Administrator, so a personal subscription on this type would leak other users' words -
    /// it must never even appear as a choice in the personal subscribe list. Half of a pair with
    /// <see cref="FeedbackSubmitted_IsPlatformSubscribable"/> below: the type must be selectable by exactly
    /// one of the two lists, never both and never neither.</summary>
    [Fact]
    public void FeedbackSubmitted_IsNotPersonallySubscribable()
    {
        Assert.DoesNotContain(WebhookEventTypes.FeedbackSubmitted, WebhookEventTypes.Subscribable);
    }

    /// <summary>The other half of the pair: a PLATFORM subscription must be able to choose
    /// <c>feedback.submitted</c>, or the event is published into a void - no subscription of either kind could
    /// ever be created for it, and <see cref="WebhookSubscription.IncludeFeedbackText"/> would be unreachable.</summary>
    [Fact]
    public void FeedbackSubmitted_IsPlatformSubscribable()
    {
        Assert.Contains(WebhookEventTypes.FeedbackSubmitted, WebhookEventTypes.PlatformSubscribable);
    }

    /// <summary>The platform list must be a superset of the personal one, or a type a personal subscription
    /// may choose could become unavailable to a platform subscription by accident.</summary>
    [Fact]
    public void PlatformSubscribable_IsNeverNarrowerThanSubscribable()
    {
        foreach (var type in WebhookEventTypes.Subscribable)
            Assert.Contains(type, WebhookEventTypes.PlatformSubscribable);
    }

    /// <summary>The bug this pair of tests was written for: <c>feedback.submitted</c> carries no Workflow
    /// Signals (nothing in the product produces one for it), and the platform branch used to require the
    /// subscription's filter to intersect the event's signals. Since a platform subscription is REQUIRED to
    /// have a non-empty filter, that made the type subscribable but permanently undeliverable - published
    /// exactly as <c>FeedbackController</c> publishes it, this enqueued zero deliveries.</summary>
    [Fact]
    public async Task FeedbackSubmitted_reaches_a_platform_sub_whose_signals_cannot_match()
    {
        var db = TestDb.Create();
        var sub = PlatformSub(WebhookEventTypes.FeedbackSubmitted, signalFilter: "post-to-slack");
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        // Published with no signals at all - exactly how FeedbackController calls it.
        await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(),
            data: new { id = Guid.NewGuid(), hasScreenshot = false });

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(sub.Id, d.SubscriptionId);
    }

    /// <summary>The exemption is by EVENT TYPE, not by filter shape, so a filterless platform subscription
    /// gets it too. (The management API refuses to create one, but a row that predates or bypasses that
    /// validation must not behave differently from the one beside it.)</summary>
    [Fact]
    public async Task FeedbackSubmitted_reaches_a_platform_sub_with_an_empty_signal_filter()
    {
        var db = TestDb.Create();
        db.Webhooks.Add(PlatformSub(WebhookEventTypes.FeedbackSubmitted, signalFilter: null));
        await db.SaveChangesAsync();

        await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(), data: new { id = Guid.NewGuid() });

        Assert.Single(await db.WebhookDeliveries.ToListAsync());
    }

    /// <summary>The regression guard for the exemption's blast radius. Same shape as the feedback test above -
    /// platform subscription with a real filter, event published carrying no signals - but a signal-routed
    /// event type. It must still deliver nothing: the exemption is for <c>feedback.submitted</c> alone, and
    /// widening the gate itself would start firing every existing type at platform subscriptions that do not
    /// receive it today.</summary>
    [Fact]
    public async Task Signal_routed_events_still_skip_a_platform_sub_when_the_event_carries_no_signals()
    {
        var db = TestDb.Create();
        db.Webhooks.Add(PlatformSub(WebhookEventTypes.FormulaResultCompleted, signalFilter: "post-to-slack"));
        db.Webhooks.Add(PlatformSub(WebhookEventTypes.RecordingSummarized, signalFilter: null));
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.FormulaResultCompleted, Guid.NewGuid(), new { marker = "x" });
        await pub.PublishAsync(WebhookEventTypes.RecordingSummarized, Guid.NewGuid(), new { marker = "x" });

        Assert.Empty(await db.WebhookDeliveries.ToListAsync());
    }

    /// <summary>A platform subscription that did not ask for <c>feedback.submitted</c> must not start
    /// receiving it just because the type skips the signal gate - the event-type match still applies.</summary>
    [Fact]
    public async Task FeedbackSubmitted_skips_a_platform_sub_that_did_not_subscribe_to_it()
    {
        var db = TestDb.Create();
        db.Webhooks.Add(PlatformSub(WebhookEventTypes.RecordingSummarized));
        db.Webhooks.Add(PlatformSub(WebhookEventTypes.FeedbackSubmitted, signalFilter: null));
        db.Webhooks.Add(PlatformSub(WebhookEventTypes.FeedbackSubmitted).Tap(s => s.IsActive = false));
        await db.SaveChangesAsync();

        await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(), data: new { id = Guid.NewGuid() });

        Assert.Single(await db.WebhookDeliveries.ToListAsync());
    }

    /// <summary>The feedback-text gate, mirroring the contacts gate above: the thin body (ids/route/release)
    /// goes to every matching subscription, but the submitter's free-text description only goes to one that
    /// has deliberately ticked <see cref="WebhookSubscription.IncludeFeedbackText"/>. Built on PLATFORM
    /// subscriptions - the earlier version of this pair used the personal <see cref="Sub"/> helper, so it
    /// asserted against a combination production can never produce and would have passed even while
    /// <c>feedback.submitted</c> was undeliverable.</summary>
    [Fact]
    public async Task Publish_OmitsFeedbackText_WhenTheSubscriptionHasNotOptedIn()
    {
        var db = TestDb.Create();
        var sub = PlatformSub(WebhookEventTypes.FeedbackSubmitted);
        sub.IncludeFeedbackText = false;
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(),
            data: new { id = Guid.NewGuid(), hasScreenshot = false },
            dataWithFeedbackText: new { id = Guid.NewGuid(), description = "SECRET_FEEDBACK_TEXT" });

        var body = (await db.WebhookDeliveries.SingleAsync()).PayloadJson;
        Assert.DoesNotContain("SECRET_FEEDBACK_TEXT", body);
    }

    [Fact]
    public async Task Publish_IncludesFeedbackText_WhenTheSubscriptionHasOptedIn()
    {
        var db = TestDb.Create();
        var sub = PlatformSub(WebhookEventTypes.FeedbackSubmitted);
        sub.IncludeFeedbackText = true;
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        await new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance).PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, Guid.NewGuid(),
            data: new { id = Guid.NewGuid(), hasScreenshot = false },
            dataWithFeedbackText: new { id = Guid.NewGuid(), description = "SECRET_FEEDBACK_TEXT" });

        Assert.Contains("SECRET_FEEDBACK_TEXT", (await db.WebhookDeliveries.SingleAsync()).PayloadJson);
    }
}

file static class SubscriptionTweak
{
    /// <summary>Inline mutation for a subscription built by a factory helper.</summary>
    public static WebhookSubscription Tap(this WebhookSubscription s, Action<WebhookSubscription> tweak)
    { tweak(s); return s; }
}
