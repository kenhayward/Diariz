using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>CRUD for Formulas (a saved prompt + context) plus the action that runs one over a recording.
/// A Personal formula is owned by its creator and always usable by them; a Platform/Diariz formula is
/// shared and its writes (create/edit/delete/enable) are gated by the <see cref="PlatformPermission.ManageFormulas"/>
/// permission. Diariz-seeded formulas (<see cref="Formula.IsBuiltIn"/>) can never be deleted.</summary>
[ApiController]
[Authorize]
[Route("api/formulas")]
public class FormulasController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IUserPermissions _permissions;
    private readonly IFormulaRunner _runner;
    private readonly IJobQueue _queue;
    private readonly IHubContext<TranscriptionHub> _hub;

    public FormulasController(DiarizDbContext db, IUserPermissions permissions, IFormulaRunner runner,
        IJobQueue queue, IHubContext<TranscriptionHub> hub)
    {
        _db = db;
        _permissions = permissions;
        _runner = runner;
        _queue = queue;
        _hub = hub;
    }

    /// <summary>The union of every defined <see cref="FormulaContext"/> flag (None + Transcript..Actions = 63).
    /// Any bit set outside this mask is an invalid context - rejected on create/update so a client can't persist
    /// a value the context builder would never honour.</summary>
    private const int ValidContextMask =
        (int)(FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Attachments
            | FormulaContext.Summary | FormulaContext.Minutes | FormulaContext.Actions);

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> CanManageFormulasAsync() => _permissions.HasAsync(UserId, PlatformPermission.ManageFormulas);

    private static bool IsValidContext(int context) => (context & ~ValidContextMask) == 0;

    /// <summary>The caller's own Personal formulas plus every enabled Platform/Diariz formula.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<FormulaDto>> List()
    {
        var userId = UserId;
        var formulas = await _db.Formulas
            .Where(f => (f.Scope == FormulaScope.Personal && f.OwnerUserId == userId)
                     || (f.Scope != FormulaScope.Personal && f.Enabled)
                     || (f.Scope == FormulaScope.Personal && f.Shared
                         && _db.FormulaSubscriptions.Any(s => s.FormulaId == f.Id && s.UserId == userId)))
            .ToListAsync();
        return formulas.Select(ToDto).ToList();
    }

    /// <summary>Every Platform/Diariz formula, enabled or not (never Personal) - for the Formulas admin
    /// popup, which needs to see and toggle disabled shared formulas too. Gated by <see cref="PlatformPermission.ManageFormulas"/>
    /// via the <c>[Authorize]</c> policy; the explicit check below also makes the 403 unit-testable (a
    /// directly-constructed controller doesn't run the authorization pipeline).</summary>
    [HttpGet("managed")]
    [Authorize(Policy = "ManageFormulas")]
    public async Task<ActionResult<IReadOnlyList<FormulaDto>>> Managed()
    {
        if (!await CanManageFormulasAsync())
            return Forbidden("Only a Formulas Administrator can view managed formulas.");

        var formulas = await _db.Formulas
            .Where(f => f.Scope == FormulaScope.Platform || f.Scope == FormulaScope.Diariz)
            .OrderBy(f => f.Scope)
            .ThenBy(f => f.Name)
            .ToListAsync();
        return Ok(formulas.Select(ToDto).ToList());
    }

    /// <summary>Creates a formula. A Personal formula is always allowed and owned by the caller; a
    /// Platform/Diariz formula requires <see cref="PlatformPermission.ManageFormulas"/>. Clients can never
    /// set <see cref="Formula.IsBuiltIn"/> - only the seeder does.</summary>
    [HttpPost]
    public async Task<ActionResult<FormulaDto>> Create(CreateFormulaRequest req)
    {
        if (!Enum.TryParse<FormulaScope>(req.Scope, out var scope) || !Enum.IsDefined(scope))
            return BadRequest("Unknown scope.");
        if (!IsValidContext(req.Context))
            return BadRequest("Invalid context.");

        if (scope != FormulaScope.Personal && !await CanManageFormulasAsync())
            return Forbidden("Only a Formulas Administrator can create a shared formula.");

        var formula = new Formula
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            OwnerUserId = scope == FormulaScope.Personal ? UserId : null,
            Name = req.Name,
            Description = req.Description,
            ContentJson = (req.Content ?? TemplateContent.Empty).Serialize(),
            Context = (FormulaContext)req.Context,
            Enabled = true,
            IsBuiltIn = false,
            Shared = scope == FormulaScope.Personal && req.Shared,
        };
        _db.Formulas.Add(formula);
        await _db.SaveChangesAsync();
        return Created($"api/formulas/{formula.Id}", ToDto(formula));
    }

    /// <summary>Partial update (null fields left unchanged). A Personal formula needs ownership (a
    /// non-owned Personal formula 404s so its existence isn't leaked); a Platform/Diariz formula needs
    /// <see cref="PlatformPermission.ManageFormulas"/>.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<FormulaDto>> Update(Guid id, UpdateFormulaRequest req)
    {
        var formula = await _db.Formulas.FirstOrDefaultAsync(f => f.Id == id);
        if (formula is null) return NotFound();

        if (formula.Scope == FormulaScope.Personal)
        {
            if (formula.OwnerUserId != UserId) return NotFound();
        }
        else if (!await CanManageFormulasAsync())
        {
            return Forbidden("Only a Formulas Administrator can edit a shared formula.");
        }

        if (req.Context is not null && !IsValidContext(req.Context.Value))
            return BadRequest("Invalid context.");

        if (req.Name is not null) formula.Name = req.Name;
        if (req.Description is not null) formula.Description = req.Description;
        if (req.Content is not null) formula.ContentJson = req.Content.Serialize();
        if (req.Context is not null) formula.Context = (FormulaContext)req.Context.Value;
        if (req.Shared is not null && formula.Scope == FormulaScope.Personal) formula.Shared = req.Shared.Value;
        formula.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(formula));
    }

    /// <summary>Deletes a formula. Personal needs ownership (404 otherwise, same leak-avoidance as
    /// <see cref="Update"/>); Platform/Diariz needs <see cref="PlatformPermission.ManageFormulas"/>; a
    /// Diariz built-in formula can never be deleted (400).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var formula = await _db.Formulas.FirstOrDefaultAsync(f => f.Id == id);
        if (formula is null) return NotFound();

        if (formula.Scope == FormulaScope.Personal)
        {
            if (formula.OwnerUserId != UserId) return NotFound();
        }
        else
        {
            if (!await CanManageFormulasAsync())
                return Forbidden("Only a Formulas Administrator can delete a shared formula.");
            if (formula.IsBuiltIn)
                return BadRequest("Built-in formulas can't be deleted.");
        }

        if (await InUseByAsync(id) is { } usedBy)
            return BadRequest($"This formula generates the minutes for {usedBy}. Point those meeting types at " +
                              "another formula first.");

        _db.Formulas.Remove(formula);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Enables/disables a Platform/Diariz formula (Personal formulas are always available, so
    /// this 400s for them). Requires <see cref="PlatformPermission.ManageFormulas"/>.</summary>
    [HttpPut("{id:guid}/enabled")]
    public async Task<IActionResult> SetEnabled(Guid id, SetFormulaEnabledRequest req)
    {
        var formula = await _db.Formulas.FirstOrDefaultAsync(f => f.Id == id);
        if (formula is null) return NotFound();
        // A non-owned Personal formula 404s BEFORE the "always available" 400, so the 400 hint can't be used
        // to distinguish another user's Personal formula from one that doesn't exist (leak-avoidance, same as
        // Update/Delete).
        if (formula.Scope == FormulaScope.Personal)
        {
            if (formula.OwnerUserId != UserId) return NotFound();
            return BadRequest("Personal formulas are always available.");
        }
        if (!await CanManageFormulasAsync())
            return Forbidden("Only a Formulas Administrator can enable or disable a shared formula.");

        // Disabling is as destructive as deleting for a template that depends on it - a disabled formula can't
        // be run, so its meeting types would quietly stop producing minutes. Same guard, same message.
        if (!req.Enabled && await InUseByAsync(id) is { } usedBy)
            return BadRequest($"This formula generates the minutes for {usedBy}. Point those meeting types at " +
                              "another formula first.");

        formula.Enabled = req.Enabled;
        formula.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The meeting types that use this formula as their PRIMARY (i.e. it generates their minutes), as a
    /// readable list - or null when none do. Additional-formula links are not checked: those cascade away
    /// harmlessly, and a template without one still produces minutes.</summary>
    private async Task<string?> InUseByAsync(Guid formulaId)
    {
        var titles = await _db.MeetingTypes
            .Where(m => m.PrimaryFormulaId == formulaId)
            .OrderBy(m => m.Title)
            .Select(m => m.Title)
            .ToListAsync();

        return titles.Count == 0 ? null : string.Join(", ", titles);
    }

    /// <summary>Formulas shared by OTHER users, for the discovery browser. Any authed user; excludes the
    /// caller's own. Includes the owner's display (name falls back to email) + avatar and whether the caller
    /// has already added it.</summary>
    [HttpGet("shared")]
    public async Task<ActionResult<IReadOnlyList<SharedFormulaDto>>> Shared()
    {
        var userId = UserId;
        var shared = await _db.Formulas
            .Where(f => f.Scope == FormulaScope.Personal && f.Shared && f.OwnerUserId != userId)
            .OrderBy(f => f.Name)
            .ToListAsync();

        var ownerIds = shared.Where(f => f.OwnerUserId != null).Select(f => f.OwnerUserId!.Value).Distinct().ToList();
        var owners = (await _db.Users.Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Email, u.PictureUrl }).ToListAsync())
            .ToDictionary(u => u.Id, u => (u.FullName, u.Email, u.PictureUrl));

        var sharedIds = shared.Select(f => f.Id).ToList();
        var mine = (await _db.FormulaSubscriptions
                .Where(s => s.UserId == userId && sharedIds.Contains(s.FormulaId))
                .Select(s => s.FormulaId).ToListAsync())
            .ToHashSet();

        return shared.Select(f =>
        {
            string? name = null, pic = null;
            if (f.OwnerUserId is Guid oid && owners.TryGetValue(oid, out var o))
            {
                name = string.IsNullOrWhiteSpace(o.FullName) ? o.Email : o.FullName;
                pic = o.PictureUrl;
            }
            return new SharedFormulaDto(ToDto(f), name, pic, mine.Contains(f.Id));
        }).ToList();
    }

    /// <summary>Add a shared Personal formula (owned by someone else) to the caller's collection. Idempotent.
    /// 404 for a missing / non-shared / non-Personal / own formula (leak-avoidance).</summary>
    [HttpPost("{id:guid}/subscribe")]
    public async Task<IActionResult> Subscribe(Guid id)
    {
        var userId = UserId;
        var f = await _db.Formulas.FirstOrDefaultAsync(x => x.Id == id);
        if (f is null || f.Scope != FormulaScope.Personal || !f.Shared || f.OwnerUserId == userId)
            return NotFound();

        if (!await _db.FormulaSubscriptions.AnyAsync(s => s.FormulaId == id && s.UserId == userId))
        {
            _db.FormulaSubscriptions.Add(new FormulaSubscription
            {
                Id = Guid.NewGuid(), FormulaId = id, UserId = userId, CreatedAt = DateTimeOffset.UtcNow,
            });
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // A concurrent first-subscribe won the race and the unique (FormulaId, UserId) index rejected
                // this one - the caller is already subscribed, so treat it as success (idempotent).
            }
        }
        return NoContent();
    }

    /// <summary>Remove the caller's link to a shared formula. Idempotent.</summary>
    [HttpDelete("{id:guid}/subscribe")]
    public async Task<IActionResult> Unsubscribe(Guid id)
    {
        var userId = UserId;
        var sub = await _db.FormulaSubscriptions.FirstOrDefaultAsync(s => s.FormulaId == id && s.UserId == userId);
        if (sub is not null)
        {
            _db.FormulaSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }

    /// <summary>Kicks off an async formula run over a recording: validates run-access (the same guards the
    /// synchronous tool path uses, via <see cref="IFormulaRunner.ValidateRecordingRunAsync"/>), creates a
    /// pending <c>Generating</c> result row, enqueues a background job (<see cref="FormulaRunJob"/>), and
    /// returns <c>202 Accepted</c> with the pending result. The <c>FormulaRunWorker</c> flips the row to
    /// Ready/Failed and notifies the browser over SignalR. Lives on an absolute route under
    /// <c>api/recordings</c> so the URL reads naturally while the rest of this controller's CRUD stays under
    /// <c>api/formulas</c>.</summary>
    [HttpPost("~/api/recordings/{recordingId:guid}/formulas/{formulaId:guid}/run")]
    public async Task<ActionResult<FormulaResultDto>> Run(Guid recordingId, Guid formulaId, CancellationToken ct)
    {
        Formula formula;
        try
        {
            formula = await _runner.ValidateRecordingRunAsync(UserId, recordingId, formulaId, ct);
        }
        catch (FormulaNotFoundException)
        {
            return NotFound();
        }
        catch (FormulaAccessException ex)
        {
            return Forbidden(ex.Message);
        }
        catch (FormulaNotConfiguredException)
        {
            return BadRequest("Formulas need an AI endpoint. Set one in Settings.");
        }

        // An explicit run replaces this recording's existing result for the formula (never appends a duplicate),
        // and does overwrite a hand-edited one - the user asked for it.
        var result = (await FormulaResultUpsert.ForRecordingAsync(_db, recordingId, formula, UserId, automatic: false, ct))!;
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueFormulaRunAsync(new FormulaRunJob(recordingId, null, result.Id, formula.Id, UserId), ct);
        await _hub.NotifyFormulaStatusAsync(UserId, recordingId, null, result.Id, FormulaRunStatus.Generating.ToString());

        var origins = await FormulaResultOrigins.ResolveAsync(_db, new[] { result }, ct);
        return Accepted(ToResultDto(result, origins[result.Id]));
    }

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);

    private static FormulaDto ToDto(Formula f) => new(
        f.Id, f.Scope.ToString(), f.OwnerUserId, f.Name, f.Description, TemplateContent.Parse(f.ContentJson),
        (int)f.Context, f.Enabled, f.IsBuiltIn, f.Shared);

    private static FormulaResultDto ToResultDto(FormulaResult r, FormulaResultOriginDto origin) => new(
        r.Id, r.RecordingId, r.Name, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt, origin,
        r.Status.ToString(), r.Error);
}
