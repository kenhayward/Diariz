using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Localization;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>
/// Creates the next <see cref="Transcription"/> version for a recording and queues the worker job.
/// <para>
/// Extracted from <c>RecordingsController</c> because a live capture is transcribed from the
/// <b>worker merge callback</b> rather than from a user request: the audio does not exist until the
/// chunks have been concatenated. Two copies of the version-numbering and language-resolution rules
/// would drift, and the language rule in particular is easy to get subtly wrong.
/// </para>
/// </summary>
public static class TranscriptionEnqueue
{
    /// <summary>Adds the transcription row, flips the recording to <see cref="RecordingStatus.Queued"/>,
    /// enqueues the job and notifies the owner. Does <b>not</b> call <c>SaveChangesAsync</c> - the caller
    /// owns the transaction boundary.</summary>
    public static async Task<Transcription> AddAsync(
        DiarizDbContext db, IJobQueue queue, IHubContext<TranscriptionHub> hub,
        Recording rec, string defaultModel, string? model = null, CancellationToken ct = default)
    {
        var nextVersion = await db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .Select(t => (int?)t.Version).MaxAsync(ct) ?? 0;

        var transcription = new Transcription
        {
            Id = Guid.NewGuid(),
            RecordingId = rec.Id,
            Model = model ?? defaultModel,
            Version = nextVersion + 1,
        };
        db.Transcriptions.Add(transcription);

        // The spoken language: this recording's own pin, else the owner's default, else auto-detect. The
        // default is read per job rather than copied onto the recording, so changing the preference applies
        // to everything that has not overridden it. Mapped to a Whisper code here - the worker hands it
        // straight to the model, which does not know the platform's regional tags ("pt-BR").
        var chosenLanguage = rec.TranscriptionLanguage
            ?? (await db.UserSettings.FindAsync([rec.UserId], ct))?.TranscriptionLanguage;

        rec.Status = RecordingStatus.Queued;
        await queue.EnqueueAsync(
            new TranscriptionJob(rec.Id, transcription.Id, rec.BlobKey, transcription.Model,
                rec.MinSpeakers, rec.MaxSpeakers, SupportedLanguages.ToWhisperCode(chosenLanguage)), ct);
        await hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
        return transcription;
    }
}
