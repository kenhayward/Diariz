using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Enrolled voiceprints (per-user). Create a profile from a recording's speaker, list them for
/// reassignment, and erase one (GDPR) — which also reverts any auto-applied labels.</summary>
[ApiController]
[Authorize]
[Route("api/speaker-profiles")]
public class SpeakerProfilesController : ControllerBase
{
    private readonly DiarizDbContext _db;

    public SpeakerProfilesController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IReadOnlyList<SpeakerProfileDto>> List() =>
        await _db.SpeakerProfiles
            .Where(p => p.UserId == UserId)
            .OrderBy(p => p.Name)
            .Select(p => new SpeakerProfileDto(p.Id, p.Name, p.SampleCount))
            .ToListAsync();

    /// <summary>Create a voiceprint from a recording's diarized speaker (its embedding becomes the centroid).</summary>
    [HttpPost]
    public async Task<ActionResult<SpeakerProfileDto>> Create(CreateSpeakerProfileRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A name is required.");

        // Ownership: the recording (and thus its speaker) must belong to the caller.
        var owned = await _db.Recordings.AnyAsync(r => r.Id == req.RecordingId && r.UserId == UserId);
        if (!owned) return NotFound();

        var speaker = await _db.Speakers
            .FirstOrDefaultAsync(s => s.RecordingId == req.RecordingId && s.Label == req.Label);
        if (speaker is null) return NotFound();
        if (speaker.Embedding is null)
            return BadRequest("This speaker has no voice embedding yet (re-transcribe to compute one).");

        var profile = new SpeakerProfile
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = name,
            Embedding = speaker.Embedding,
            SampleCount = 1,
        };
        _db.SpeakerProfiles.Add(profile);
        _db.ProfileContributions.Add(new ProfileContribution
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            SpeakerId = speaker.Id,
            RecordingId = req.RecordingId,
            Embedding = speaker.Embedding,
        });

        // Assign the source speaker to the new profile (manual, not auto).
        speaker.ProfileId = profile.Id;
        speaker.DisplayName = name;
        speaker.IdentifiedAuto = false;

        await _db.SaveChangesAsync();
        return new SpeakerProfileDto(profile.Id, profile.Name, profile.SampleCount);
    }

    /// <summary>GDPR erase: delete the voiceprint + training data, unlink it from recordings, and revert
    /// auto-applied names to the anonymous label (manual names are kept).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == id && p.UserId == UserId);
        if (profile is null) return NotFound();

        var linked = await _db.Speakers.Where(s => s.ProfileId == id).ToListAsync();
        foreach (var s in linked)
        {
            s.ProfileId = null;
            if (s.IdentifiedAuto)
            {
                s.DisplayName = s.Label; // revert the auto label
                s.IdentifiedAuto = false;
            }
        }

        _db.SpeakerProfiles.Remove(profile); // cascades ProfileContributions
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
