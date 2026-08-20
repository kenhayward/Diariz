using System.Text.Json;
using System.Text.Json.Nodes;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>The models an administrator configures and points call groups at - the write side of what
/// <see cref="LlmSettingsResolver"/> reads.
///
/// <c>[ManagePlatform]</c>, not the weaker <c>ReadAdminSettings</c> that Administrators also hold: these
/// rows carry endpoint credentials and decide which model every user's calls go to.</summary>
[ApiController]
[Route("api/admin/llm-models")]
[Authorize(Policy = "ManagePlatform")]
public class LlmModelsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IApiKeyProtector _protector;
    private readonly SummarizationOptions _env;
    private readonly LlmDefaultsOptions _defaults;
    private readonly ILlmTestProbe _probe;
    private readonly ILlmModelDiscoveryClient _discovery;
    private readonly ILlmTestPromptFactory _prompts;

    public LlmModelsController(
        DiarizDbContext db, IApiKeyProtector protector, IOptions<SummarizationOptions> env,
        IOptions<LlmDefaultsOptions> defaults, ILlmTestProbe probe, ILlmModelDiscoveryClient discovery,
        ILlmTestPromptFactory prompts)
    {
        _db = db;
        _protector = protector;
        _env = env.Value;
        _defaults = defaults.Value;
        _probe = probe;
        _discovery = discovery;
        _prompts = prompts;
    }

    [HttpGet]
    public async Task<ActionResult<List<LlmModelDto>>> List()
    {
        var models = await _db.LlmModels
            .Include(m => m.Parameters)
            .OrderBy(m => m.Name)
            .AsNoTracking()
            .ToListAsync();

        return models.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<LlmModelDto>> Create(LlmModelUpsert req)
    {
        if (Validate(req) is { } bad) return bad;
        if (await _db.LlmModels.AnyAsync(m => m.Name == req.Name.Trim()))
            return Conflict($"A model named '{req.Name.Trim()}' already exists.");

        var model = new LlmModel
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            DisplayName = Trim(req.DisplayName),
            ApiBase = req.ApiBase.Trim(),
            ApiKeyEncrypted = _protector.Protect(req.ApiKey),
            ContextLength = req.ContextLength,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.LlmModels.Add(model);
        ReplaceParameters(model, req.Parameters);
        await _db.SaveChangesAsync();

        return ToDto(model);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LlmModelDto>> Update(Guid id, LlmModelUpsert req)
    {
        if (Validate(req) is { } bad) return bad;

        var model = await _db.LlmModels.Include(m => m.Parameters).FirstOrDefaultAsync(m => m.Id == id);
        if (model is null) return NotFound();

        if (await _db.LlmModels.AnyAsync(m => m.Name == req.Name.Trim() && m.Id != id))
            return Conflict($"A model named '{req.Name.Trim()}' already exists.");

        model.Name = req.Name.Trim();
        model.DisplayName = Trim(req.DisplayName);
        model.ApiBase = req.ApiBase.Trim();
        model.ContextLength = req.ContextLength;
        model.UpdatedAt = DateTimeOffset.UtcNow;

        // Three distinct meanings, and the middle one is why this is not a plain assignment: null keeps the
        // stored key (the UI is never given it, so it cannot send it back), empty clears it for an endpoint
        // that needs none, and a value replaces it.
        if (req.ApiKey is not null)
            model.ApiKeyEncrypted = _protector.Protect(req.ApiKey);

        ReplaceParameters(model, req.Parameters);
        await _db.SaveChangesAsync();

        return ToDto(model);
    }

    /// <summary>Removes a model, refusing while anything still points at it.
    ///
    /// The checks below are the guard, NOT a nicety on top of the database. Both FKs are DELETE RESTRICT in
    /// Postgres, but neither reliably stops this path: for an assignment EF throws at <c>Remove</c> before
    /// any statement is sent, and for the platform default - whose FK is nullable - EF quietly issues
    /// <c>SET DefaultLlmModelId = NULL</c> ahead of the DELETE, so the constraint never fires and the model
    /// really is deleted. Verified against real Postgres in <c>LlmModelSchemaTests</c>.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var model = await _db.LlmModels.FirstOrDefaultAsync(m => m.Id == id);
        if (model is null) return NotFound();

        var groups = await _db.LlmCallAssignments
            .Where(a => a.LlmModelId == id)
            .Select(a => a.Group)
            .ToListAsync();

        if (groups.Count > 0)
            return Conflict(
                $"'{model.Name}' still serves {string.Join(", ", groups.OrderBy(g => g).Select(g => g.ToString()))}. " +
                "Point those call types at another model first.");

        if (await _db.PlatformSettings.AnyAsync(p => p.DefaultLlmModelId == id))
            return Conflict($"'{model.Name}' is the default model. Choose a different default first.");

        _db.LlmModels.Remove(model);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The bottom of the layer stack - the application defaults every unset parameter resolves
    /// through, keyed by group exactly as a model's own parameter rows are.
    ///
    /// The editor needs these for two things it cannot do without them: telling an administrator what a
    /// parameter inherits ("from Defaults - 0.3" rather than a blank), and previewing the request body as
    /// it is typed. They live in configuration, so there is no other way for a browser to learn them.
    ///
    /// A group whose defaults configure nothing is OMITTED rather than sent as <c>{}</c>: the client walks
    /// these the same way <see cref="LlmSettingsResolver"/> does, where a layer that mentions no key decides
    /// nothing and an empty object is indistinguishable from one that does.</summary>
    [HttpGet("defaults")]
    public async Task<ActionResult<Dictionary<string, string>>> Defaults()
    {
        var layers = new Dictionary<string, string>();

        // The administrator's platform timeout is folded into the base layer rather than sent separately:
        // the client walks these to show what a parameter inherits and to preview the request body, and it
        // must arrive at the number the SERVER will use. Reporting the shipped default here while the call
        // used 600 would make the panel state a timeout no call ever has.
        var platform = await _db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
        if (LlmPlatformLayers.BaseWithPlatformTimeout(_defaults, platform) is { } baseLayer)
            layers[nameof(LlmCallGroup.ModelBase)] = baseLayer;

        foreach (var group in Enum.GetValues<LlmCallGroup>())
            if (group != LlmCallGroup.ModelBase && _defaults.LayerFor(group) is { } layer)
                layers[group.ToString()] = layer;

        return layers;
    }

    /// <summary>Runs one sample call against this model with the parameters the administrator is editing,
    /// and reports what came back: how long the model took to say anything, what it cost, what it said,
    /// and - when it failed - which parameter the endpoint blamed.
    ///
    /// The parameters come from the request because the point is to try a change BEFORE saving it. The
    /// endpoint, key and model name come from the stored row and only from there.
    ///
    /// Scoped as <see cref="LlmCallKind.AdminTest"/> so the call appears in the usage log like any other:
    /// a call that spent tokens and left no trace would be the one an administrator could not account
    /// for.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<LlmTestOutcome>> Test(Guid id, LlmModelTestRequest req)
    {
        if (!Enum.TryParse<LlmCallGroup>(req.Group, out var group))
            return BadRequest($"Unknown call group '{req.Group}'.");

        if (ValidateParameters(req.Parameters) is { } bad) return bad;

        var model = await _db.LlmModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (model is null) return NotFound();

        // The same order LlmSettingsResolver walks, with the request's unsaved layers standing in for the
        // model's stored rows. The layers below them come from the SHARED helper, so the panel cannot
        // report a timeout (or anything else) the real call would not use.
        var platform = await _db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
        var layers = new List<string?>
        {
            group == LlmCallGroup.ModelBase ? null : Layer(req.Parameters, group),
            Layer(req.Parameters, LlmCallGroup.ModelBase),
        };
        layers.AddRange(LlmPlatformLayers.Below(
            _defaults, group == LlmCallGroup.ModelBase ? null : group, platform));

        var config = new LlmRequestConfig(
            ApiBase: model.ApiBase,
            ApiKey: _protector.Unprotect(model.ApiKeyEncrypted) ?? "",
            Model: model.Name,
            Parameters: LlmParameterLayers.Resolve(layers))
        {
            ContextCharBudget = LlmContextBudget.CharsFor(model.ContextLength),
        };

        // Read from the users table rather than a claim, the same way every other call site attributes its
        // scope: the email is a denormalised snapshot on LlmCalls, and a blank one puts every test call in
        // a single nameless bucket in the usage log's by-user grouping.
        var userId = CallerId;
        var userEmail = userId is null
            ? null
            : await _db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();

        // Three groups run the REAL prompt against one of the caller's own recordings; the rest run the
        // built-in sample. Which is which is the factory's to decide, so the two cannot drift.
        var messages = LlmTestSample.Messages;
        var maxResponseChars = LlmTestSample.MaxResponseChars;
        LlmTestPrompt? recordingPrompt = null;
        Recording? recording = null;

        if (_prompts.NeedsRecording(group))
        {
            if (req.RecordingId is not { } recordingId)
                return BadRequest($"A recording is required to test the {group} call.");
            if (userId is not { } callerId) return Unauthorized();

            recordingPrompt = await _prompts.BuildAsync(
                group, recordingId, callerId, config.ContextCharBudget, HttpContext.RequestAborted);
            if (recordingPrompt is null)
                return NotFound("That recording does not exist, is not yours, or has no transcript yet.");

            recording = await _db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordingId);
            messages = recordingPrompt.Messages;
            maxResponseChars = LlmTestPromptFactory.RecordingMaxResponseChars;
        }

        using var scope = LlmCallScope.Push(
            LlmCallKind.AdminTest, userId, userEmail,
            recording?.Id, recording is null ? null : recording.Name ?? recording.Title);

        var outcome = await _probe.RunAsync(config, messages, maxResponseChars, HttpContext.RequestAborted);

        // Parsed through the pipeline's own parser, so an unusable reply shows up here exactly as it would
        // show up as a recording with no tags. A failed call has no reply to parse.
        if (recordingPrompt is null || !outcome.Ok || outcome.Response is null) return outcome;

        return outcome with
        {
            ParsedKind = recordingPrompt.ParsedKind,
            Parsed = ParseForDisplay(recordingPrompt.ParsedKind, outcome.Response, recording),
        };
    }

    /// <summary>The reply as the pipeline would have understood it. Not a validation step - all three
    /// parsers are total, and an empty list is the meaningful answer "this model gave us nothing".
    ///
    /// <para>Returns the object itself and lets the response serializer name its properties. Serializing it
    /// here instead produced PascalCase inside a camelCase envelope, which the browser could not read.</para></summary>
    private static object? ParseForDisplay(string parsedKind, string response, Recording? recording) =>
        parsedKind switch
        {
            "Tags" => TagsPrompt.ParseContent(response),
            "Actions" => ActionsPrompt.ParseContent(response),
            "Summary" => SummarizationPrompt.ParseContent(
                response, needName: string.IsNullOrWhiteSpace(recording?.Name)),
            _ => null,
        };

    private static string? Layer(Dictionary<string, string> parameters, LlmCallGroup group) =>
        parameters.TryGetValue(group.ToString(), out var json) ? json : null;

    [HttpGet("assignments")]
    public async Task<ActionResult<LlmAssignmentsDto>> GetAssignments()
    {
        var settings = await _db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);

        var assignments = await _db.LlmCallAssignments.AsNoTracking().ToListAsync();

        return new LlmAssignmentsDto(
            settings?.DefaultLlmModelId,
            assignments.ToDictionary(a => a.Group.ToString(), a => a.LlmModelId));
    }

    [HttpPut("assignments")]
    public async Task<IActionResult> UpdateAssignments(LlmAssignmentsDto req)
    {
        var parsed = new List<(LlmCallGroup Group, Guid ModelId)>();
        foreach (var (name, modelId) in req.Assignments)
        {
            if (!Enum.TryParse<LlmCallGroup>(name, out var group))
                return BadRequest($"Unknown call group '{name}'.");

            // ModelBase names a parameter scope, not a call type: nothing is ever dispatched to it, so an
            // assignment there would be silently inert.
            if (group == LlmCallGroup.ModelBase)
                return BadRequest("ModelBase is a parameter scope, not a call type, and cannot be assigned.");

            if (!await _db.LlmModels.AnyAsync(m => m.Id == modelId))
                return BadRequest($"No model with id {modelId}.");

            parsed.Add((group, modelId));
        }

        if (req.DefaultModelId is { } defaultId && !await _db.LlmModels.AnyAsync(m => m.Id == defaultId))
            return BadRequest($"No model with id {defaultId}.");

        _db.LlmCallAssignments.RemoveRange(await _db.LlmCallAssignments.ToListAsync());
        await _db.SaveChangesAsync();

        foreach (var (group, modelId) in parsed)
            _db.LlmCallAssignments.Add(new LlmCallAssignment { Group = group, LlmModelId = modelId });

        var settings = await _db.PlatformSettings.FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
        if (settings is null)
        {
            settings = new PlatformSettings { Id = PlatformSettings.SingletonId };
            _db.PlatformSettings.Add(settings);
        }
        settings.DefaultLlmModelId = req.DefaultModelId;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The calling administrator. Their OWN recordings are the only ones a test may run against,
    /// so this is an authorisation input, not just attribution.</summary>
    private Guid? CallerId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { } uid
            && Guid.TryParse(uid, out var parsed)
            ? parsed
            : null;

    /// <summary>The recording this administrator tests models against, shared by every call group that runs
    /// against real content.</summary>
    [HttpGet("test-recording")]
    public async Task<ActionResult<LlmTestRecordingDto>> GetTestRecording()
    {
        if (CallerId is not { } userId) return new LlmTestRecordingDto(null, null);

        var chosen = await _db.UserSettings
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.LlmTestRecordingId)
            .FirstOrDefaultAsync();
        if (chosen is not { } recordingId) return new LlmTestRecordingDto(null, null);

        // Resolved on read, and scoped to the owner again: a recording deleted (or a stored id that never
        // belonged to them) reports as "nothing chosen" rather than a dangling label the picker cannot show.
        var title = await _db.Recordings
            .AsNoTracking()
            .Where(r => r.Id == recordingId && r.UserId == userId)
            .Select(r => r.Name ?? r.Title)
            .FirstOrDefaultAsync();

        return title is null
            ? new LlmTestRecordingDto(null, null)
            : new LlmTestRecordingDto(recordingId, title);
    }

    [HttpPut("test-recording")]
    public async Task<IActionResult> SetTestRecording(SetLlmTestRecordingRequest req)
    {
        if (CallerId is not { } userId) return Unauthorized();

        if (req.RecordingId is { } recordingId
            && !await _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == userId))
            return NotFound($"No recording {recordingId} belonging to you.");

        var settings = await _db.UserSettings.FindAsync(userId);
        if (settings is null)
        {
            settings = new UserSettings { UserId = userId };
            _db.UserSettings.Add(settings);
        }
        settings.LlmTestRecordingId = req.RecordingId;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Creates the first model from the endpoint already configured in the environment, so an
    /// upgraded deployment has something to edit rather than a blank page.
    ///
    /// Refused once any model exists. It is a one-time migration aid, not an import button: re-running it
    /// would resurrect a model an administrator had deliberately deleted - the same defect as a seeder that
    /// keeps undoing a change on every boot.</summary>
    [HttpPost("from-environment")]
    public async Task<ActionResult<LlmModelDto>> CreateFromEnvironment()
    {
        if (await _db.LlmModels.AnyAsync())
            return Conflict("Models are already configured. Add or edit one instead.");

        if (string.IsNullOrWhiteSpace(_env.ApiBase))
            return BadRequest("No endpoint is configured in the environment to import.");

        var model = new LlmModel
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(_env.Model) ? "imported-model" : _env.Model.Trim(),
            ApiBase = _env.ApiBase.Trim(),
            ApiKeyEncrypted = _protector.Protect(_env.ApiKey),
            ContextLength = new ChatOptions().ContextLength,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.LlmModels.Add(model);
        await _db.SaveChangesAsync();

        return ToDto(model);
    }

    /// <summary>Whether this model appears in the chat model picker.
    ///
    /// Its own route rather than a field on the upsert: the editor drawer does not show this control, so an
    /// upsert carrying it would let a save from the drawer silently reset an administrator's choice.</summary>
    [HttpPut("{id:guid}/chat-enabled")]
    public async Task<IActionResult> SetChatEnabled(Guid id, SetChatEnabledRequest req)
    {
        var model = await _db.LlmModels.FirstOrDefaultAsync(m => m.Id == id);
        if (model is null) return NotFound();

        model.ChatEnabled = req.Enabled;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    // ---- Discovery (Add all from an endpoint) ----

    /// <summary>Lists the chat models on an endpoint, marking which are already configured.
    ///
    /// <b>This is the only route that fetches a URL the caller supplied.</b> Its bounds live in
    /// <see cref="LlmModelDiscoveryClient"/>, whose doc comment explains why the rule its neighbour
    /// <see cref="Test"/> follows - never accept an endpoint from the caller - was relaxed here, and what
    /// contains the relaxation. Only parsed model ids are returned; the endpoint's response body never
    /// reaches the caller, so this cannot be used as a general-purpose fetch.</summary>
    [HttpPost("discover")]
    public async Task<ActionResult<DiscoverModelsResultDto>> Discover(
        DiscoverModelsRequest req, CancellationToken ct = default)
    {
        var apiBase = req.ApiBase?.Trim();
        if (string.IsNullOrWhiteSpace(apiBase)) return BadRequest("An endpoint URL is required.");

        var listing = await _discovery.ListAsync(apiBase, req.ApiKey, ct);

        // Reached the server and it named its models, but nothing there serves an OpenAI-compatible
        // listing - so there is no address a completion could be sent to. Importing anyway is what produced
        // models that looked fine and then never answered, so it is refused with the reason.
        if (listing.ChatApiBase is null && listing.Models.Count > 0)
            return BadRequest(
                "That server answered, but it does not serve an OpenAI-compatible endpoint at that address " +
                "or at /v1. Check the URL - for LM Studio it usually ends in /v1.");

        var chat = listing.Models.Where(LlmModelDiscovery.IsChatModel).ToList();

        var existing = await _db.LlmModels
            .Where(m => chat.Select(c => c.Id).Contains(m.Name))
            .Select(m => m.Name)
            .ToListAsync(ct);

        // The RESOLVED endpoint, not the one that was typed - the dialog shows it, so a corrected URL is
        // visible rather than a silent adjustment nobody could account for later.
        return new DiscoverModelsResultDto(
            listing.ChatApiBase ?? apiBase,
            chat
                .Select(m => new DiscoveredModelDto(
                    m.Id,
                    m.ContextLength ?? LlmModelDiscovery.DefaultContextLength,
                    ContextLengthReported: m.ContextLength is not null,
                    AlreadyExists: existing.Contains(m.Id)))
                .ToList());
    }

    /// <summary>Creates a model row for each named model on this endpoint.
    ///
    /// <b>The names are re-checked against the endpoint's own listing.</b> They arrive from the client, so
    /// they are caller input: trusting them would let an administrator's session create a row for any model
    /// string against any endpoint without discovery ever having seen it - and the row is the audit trail
    /// this endpoint exists to leave. A name the endpoint does not report, or reports as something other
    /// than a chat model, is skipped rather than refused, since the same thing happens to a name that is
    /// simply already configured.</summary>
    [HttpPost("discover/import")]
    public async Task<ActionResult<ImportModelsResultDto>> Import(
        ImportModelsRequest req, CancellationToken ct = default)
    {
        var apiBase = req.ApiBase?.Trim();
        if (string.IsNullOrWhiteSpace(apiBase)) return BadRequest("An endpoint URL is required.");

        var listing = await _discovery.ListAsync(apiBase, req.ApiKey, ct);

        // No endpoint chat could call means nothing worth creating - a row whose ApiBase does not serve
        // /chat/completions is a model that exists and never answers.
        if (listing.ChatApiBase is not { } resolvedBase)
            return BadRequest(
                "That server does not serve an OpenAI-compatible endpoint at that address or at /v1, so " +
                "these models could not be called. Check the URL - for LM Studio it usually ends in /v1.");

        var found = listing.Models
            .Where(LlmModelDiscovery.IsChatModel)
            .ToDictionary(m => m.Id);

        var wanted = (req.Names ?? []).Distinct().Where(found.ContainsKey).ToList();
        var existing = await _db.LlmModels
            .Where(m => wanted.Contains(m.Name))
            .Select(m => m.Name)
            .ToListAsync(ct);

        var key = _protector.Protect(req.ApiKey);
        var added = new List<string>();
        var guessed = new List<string>();

        foreach (var name in wanted.Except(existing))
        {
            var model = found[name];
            _db.LlmModels.Add(new LlmModel
            {
                Id = Guid.NewGuid(),
                Name = name,
                // The RESOLVED base, not the typed one. Storing what was typed is what created models
                // pointing at an address /chat/completions is not served from.
                ApiBase = resolvedBase,
                ApiKeyEncrypted = key,
                ContextLength = model.ContextLength ?? LlmModelDiscovery.DefaultContextLength,
                // Not offered in chat, and no display name. Importing forty models from a server must not
                // put forty rows in everyone's picker, and inventing a label would only be a stored copy of
                // the slug that a later rename would strand.
                ChatEnabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            added.Add(name);
            if (model.ContextLength is null) guessed.Add(name);
        }

        await _db.SaveChangesAsync(ct);

        return new ImportModelsResultDto(added.Count, (req.Names ?? []).Distinct().Count() - added.Count, guessed);
    }

    // ---- helpers ----

    /// <summary>Blank means "use the slug", so it is stored as null rather than as an empty string - one
    /// representation of absent, so LlmModel.Label has a single thing to test.</summary>
    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private BadRequestObjectResult? Validate(LlmModelUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("A model name is required.");
        if (string.IsNullOrWhiteSpace(req.ApiBase)) return BadRequest("An endpoint URL is required.");
        if (req.ContextLength <= 0) return BadRequest("The context length must be greater than zero.");

        return ValidateParameters(req.Parameters);
    }

    /// <summary>Checks a group -> layer-JSON map: every group name real, every layer valid JSON, every key
    /// one the layer merge actually reads. Shared by the upsert and the test call so a parameter that would
    /// be silently ignored is refused identically by both.</summary>
    private BadRequestObjectResult? ValidateParameters(Dictionary<string, string> parameters)
    {
        foreach (var (groupName, json) in parameters)
        {
            if (!Enum.TryParse<LlmCallGroup>(groupName, out _))
                return BadRequest($"Unknown parameter group '{groupName}'.");

            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            }
            catch (JsonException)
            {
                return BadRequest($"The parameters for '{groupName}' are not valid JSON.");
            }

            if (parsed is null) return BadRequest($"The parameters for '{groupName}' must be a JSON object.");

            // An unrecognised key is rejected rather than stored: the layer merge ignores keys it does not
            // know, so accepting a typo would show a saved setting that silently does nothing.
            foreach (var (key, _) in parsed)
                if (!LlmParameterLayers.ParameterNames.Contains(key))
                    return BadRequest($"Unknown parameter '{key}' in '{groupName}'.");
        }

        return null;
    }

    /// <summary>Makes the model's stored rows match the request exactly - groups the request omits are
    /// deleted, so clearing a group's overrides in the UI actually clears them.</summary>
    private void ReplaceParameters(LlmModel model, Dictionary<string, string> parameters)
    {
        _db.LlmModelParameters.RemoveRange(model.Parameters);
        model.Parameters.Clear();

        foreach (var (groupName, json) in parameters)
        {
            var group = Enum.Parse<LlmCallGroup>(groupName);   // already validated
            var text = string.IsNullOrWhiteSpace(json) ? "{}" : json;

            // Re-serialise through JsonNode so what is stored is canonical: jsonb reformats anyway, and a
            // byte comparison against the submitted text would never match on real Postgres.
            var canonical = (JsonNode.Parse(text) as JsonObject)!.ToJsonString();

            _db.LlmModelParameters.Add(new LlmModelParameters
            {
                Id = Guid.NewGuid(),
                LlmModelId = model.Id,
                Group = group,
                ParametersJson = canonical,
            });
        }
    }

    private static LlmModelDto ToDto(LlmModel m) => new(
        m.Id, m.Name, m.ApiBase,
        HasApiKey: !string.IsNullOrEmpty(m.ApiKeyEncrypted),
        m.ContextLength,
        m.Parameters.ToDictionary(p => p.Group.ToString(), p => p.ParametersJson),
        m.DisplayName, m.ChatEnabled);
}
