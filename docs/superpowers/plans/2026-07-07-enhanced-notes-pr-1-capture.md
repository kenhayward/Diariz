# Enhanced Notes - PR 1 (Notes capture: recording + calendar) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Users can attach timestamped note lines to a recording (Notes tab) and to an upcoming calendar event (pre-meeting notes), with event notes automatically adopted onto the recording when the calendar link forms. Minutes are untouched (PR 3).

**Architecture:** One new row-per-line entity `MeetingNote`, anchored to either a recording or a `(CalendarId, EventId)` pair, mirroring the `RecordingAction` house pattern. Two thin controllers (recording-scoped + event-scoped CRUD). Adoption happens inside the existing `LinkCalendar` chokepoint (`PUT /api/recordings/{id}/calendar-link` - both the auto-match save and manual linking call it). Web: a **Notes** tab on the recording detail page and a compact notes editor on the calendar-event surfaces.

**Tech Stack:** ASP.NET Core 10 + EF Core (Npgsql), xUnit (+ Testcontainers), React 19 + TS + Vitest.

**Spec:** `docs/superpowers/specs/2026-07-07-enhanced-notes-design.md` (commit it with this plan on the PR branch).

**Reference files (read before starting):**
- `src/Diariz.Domain/Entities/RecordingAction.cs` + its config in `DiarizDbContext.cs:118` (row-per-line pattern)
- `src/Diariz.Api/Controllers/RecordingActionsController.cs` (owner-scoped CRUD pattern)
- `src/Diariz.Api/Controllers/RecordingsController.cs:555` (`LinkCalendar` - the adoption chokepoint)
- `apps/web/src/pages/RecordingDetail.tsx` (tab array ~line 865+; `?t=` handling ~194/232)
- `apps/web/src/pages/CalendarEventDetail.tsx` + `apps/web/src/components/CalendarEventDetails.tsx`

---

## File map

**Create**
- `src/Diariz.Domain/Entities/MeetingNote.cs`
- `src/Diariz.Api/Controllers/MeetingNotesController.cs` (recording-scoped)
- `src/Diariz.Api/Controllers/CalendarEventNotesController.cs` (event-scoped)
- `src/Diariz.Api/Services/MeetingNoteAdoption.cs` (pure-ish, static)
- Tests: `tests/Diariz.Api.Tests/MeetingNotesControllerTests.cs`, `CalendarEventNotesControllerTests.cs`, `MeetingNoteAdoptionTests.cs`; `tests/Diariz.Api.IntegrationTests/MeetingNotesIntegrationTests.cs`
- `apps/web/src/components/NotesSection.tsx` (+ `.test.tsx`) - shared line-list editor used by both surfaces
- Migration `AddMeetingNotes`

**Modify**
- `src/Diariz.Domain/DiarizDbContext.cs` (DbSet + config)
- `src/Diariz.Api/Contracts/ApiDtos.cs` (DTOs)
- `src/Diariz.Api/Controllers/RecordingsController.cs` (adoption call in `LinkCalendar`)
- `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`
- `apps/web/src/pages/RecordingDetail.tsx` (Notes tab), `apps/web/src/pages/CalendarEventDetail.tsx` (event notes)
- `apps/web/src/locales/{en,es,fr,de}/workspace.json`
- Docs (`docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`), `releases.ts` (`RELEASES[0]` + `CAPABILITIES`), version mirrors → **0.103.0**

---

## Task 1: Domain - `MeetingNote` entity + migration

**Files:** Create `src/Diariz.Domain/Entities/MeetingNote.cs`; modify `src/Diariz.Domain/DiarizDbContext.cs`; migration.

- [ ] **Step 1: Entity**

```csharp
namespace Diariz.Domain.Entities;

/// <summary>One line of the user's own meeting notes - a sparse trigger phrase, question, or observation.
/// Anchored to EITHER a recording (RecordingId set) OR an upcoming calendar event (CalendarId+EventId set,
/// RecordingId null); when a recording links to that event, event-anchored lines are adopted onto the
/// recording (RecordingId set, event keys cleared). <see cref="CapturedAtMs"/> is the offset into the
/// *recorded* clock (pause-aware, stamped by the live notes panel) - an immutable capture fact; null for
/// pre-meeting or post-hoc lines. Feeds minutes generation (steering + the Enhanced notes section, PR 3).</summary>
public class MeetingNote
{
    public Guid Id { get; set; }

    /// <summary>Owner. Event-anchored notes have no recording, so ownership hangs off the user directly.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public Guid? RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>Pre-meeting anchor (with <see cref="EventId"/>); cleared when adopted onto a recording.</summary>
    public string? CalendarId { get; set; }
    public string? EventId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Offset (ms) into the recording clock; null = pre-meeting/post-hoc. Not user-editable.</summary>
    public long? CapturedAtMs { get; set; }

    /// <summary>Sort order within the anchor (0-based).</summary>
    public int Ordinal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: DbContext** - add the DbSet beside `RecordingActions` (line ~23):

```csharp
    public DbSet<MeetingNote> MeetingNotes => Set<MeetingNote>();
```

and the config block after the `RecordingAction` one (line ~121):

```csharp
        builder.Entity<MeetingNote>(e =>
        {
            e.HasIndex(n => new { n.RecordingId, n.Ordinal });
            e.HasIndex(n => new { n.UserId, n.CalendarId, n.EventId });
            e.Property(n => n.Text).HasMaxLength(2048);
            e.Property(n => n.CalendarId).HasMaxLength(256);
            e.Property(n => n.EventId).HasMaxLength(256);
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(n => n.Recording).WithMany().HasForeignKey(n => n.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });
```

> Both FKs cascade. Note: user-cascade + recording-cascade on the same row is fine (recording is itself
> user-cascaded; Postgres handles the diamond). If `dotnet ef migrations add` complains about multiple
> cascade paths (SQL Server-ism - shouldn't happen on Npgsql), keep Recording cascade and make User cascade
> `NoAction`; ownership cleanup then rides the recording cascade + an event-notes cleanup on user delete.

- [ ] **Step 3: Migration**

Run: `dotnet ef migrations add AddMeetingNotes --project src/Diariz.Domain --startup-project src/Diariz.Api`
Inspect: `CreateTable MeetingNotes` with the two indexes + two FKs; **no stray empty `UpdateData`** (the
Meeting-Types gotcha). Then `dotnet build Diariz.slnx` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Diariz.Domain
git commit -m "feat(notes): MeetingNote entity (recording- or event-anchored) + migration"
```

---

## Task 2: DTOs + recording-scoped notes CRUD

**Files:** Modify `src/Diariz.Api/Contracts/ApiDtos.cs`; create `src/Diariz.Api/Controllers/MeetingNotesController.cs`; test `tests/Diariz.Api.Tests/MeetingNotesControllerTests.cs`.

- [ ] **Step 1: DTOs** (in `ApiDtos.cs`, beside the RecordingAction DTOs):

```csharp
/// <summary>One line of the user's own meeting notes. <paramref name="CapturedAtMs"/> is the offset into
/// the recording clock (null = pre-meeting/post-hoc); immutable after capture.</summary>
public record MeetingNoteDto(Guid Id, string Text, long? CapturedAtMs, int Ordinal, DateTimeOffset CreatedAt);

/// <summary>Bulk-append request: the live panel attaches all its lines after upload; single adds send one.</summary>
public record CreateMeetingNotesRequest(IReadOnlyList<CreateMeetingNoteLine> Lines);
public record CreateMeetingNoteLine(string Text, long? CapturedAtMs = null);

public record UpdateMeetingNoteRequest(string Text);
```

- [ ] **Step 2: Write the failing tests** (`MeetingNotesControllerTests.cs`):

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class MeetingNotesControllerTests
{
    private static MeetingNotesController Build(DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    private static async Task<(Guid userId, Guid recId)> Seed(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return (userId, rec.Id);
    }

    [Fact]
    public async Task Create_AppendsLines_AssignsOrdinals_SkipsBlank_AndTrims()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);

        var result = await Build(db, user).Create(rec, new CreateMeetingNotesRequest(
            [new("  Comp expectations  ", 61_000), new("   ", null), new("IPO experience APAC", null)]));

        var list = Assert.IsAssignableFrom<IReadOnlyList<MeetingNoteDto>>(result.Value);
        Assert.Equal(2, list.Count); // the blank line was skipped
        Assert.Equal("Comp expectations", list[0].Text); // trimmed
        Assert.Equal(61_000, list[0].CapturedAtMs);
        Assert.Equal([0, 1], list.Select(n => n.Ordinal));
    }

    [Fact]
    public async Task Create_ContinuesOrdinals_AfterExistingLines()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("one")]));

        var second = await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("two")]));
        Assert.Equal(1, Assert.IsAssignableFrom<IReadOnlyList<MeetingNoteDto>>(second.Value)[0].Ordinal);
    }

    [Fact]
    public async Task List_ReturnsOwnLines_InOrdinalOrder()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("a"), new("b")]));

        var list = (await Build(db, user).List(rec)).Value!;
        Assert.Equal(["a", "b"], list.Select(n => n.Text));
    }

    [Fact]
    public async Task Update_EditsTextOnly_TimestampImmutable_AndBumpsUpdatedAt()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        var created = (await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("x", 5_000)]))).Value![0];

        Assert.IsType<NoContentResult>(await Build(db, user).Update(rec, created.Id, new UpdateMeetingNoteRequest(" y ")));
        var row = await db.MeetingNotes.SingleAsync(n => n.Id == created.Id);
        Assert.Equal("y", row.Text);
        Assert.Equal(5_000, row.CapturedAtMs); // unchanged
    }

    [Fact]
    public async Task Delete_RemovesLine()
    {
        var db = TestDb.Create();
        var (user, rec) = await Seed(db);
        var created = (await Build(db, user).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Value![0];

        Assert.IsType<NoContentResult>(await Build(db, user).Delete(rec, created.Id));
        Assert.Empty(await db.MeetingNotes.ToListAsync());
    }

    [Fact]
    public async Task AllEndpoints_Return404_ForRecordingsTheCallerDoesNotOwn()
    {
        var db = TestDb.Create();
        var (_, rec) = await Seed(db);
        var stranger = Guid.NewGuid();

        Assert.IsType<NotFoundResult>((await Build(db, stranger).List(rec)).Result);
        Assert.IsType<NotFoundResult>((await Build(db, stranger).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Result);
        Assert.IsType<NotFoundResult>(await Build(db, stranger).Update(rec, Guid.NewGuid(), new UpdateMeetingNoteRequest("y")));
        Assert.IsType<NotFoundResult>(await Build(db, stranger).Delete(rec, Guid.NewGuid()));
    }
}
```

- [ ] **Step 3: Run, verify FAIL** (controller missing):
`dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~MeetingNotesControllerTests`

- [ ] **Step 4: Implement** (`MeetingNotesController.cs`):

```csharp
using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The user's own note lines for a recording (see <see cref="MeetingNote"/>). Text is editable;
/// capture timestamps are immutable facts. Blank lines are skipped on create; text is trimmed.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/notes")]
public class MeetingNotesController : ControllerBase
{
    private readonly DiarizDbContext _db;
    public MeetingNotesController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    private static MeetingNoteDto ToDto(MeetingNote n) => new(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> List(Guid recordingId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        return await _db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .OrderBy(n => n.Ordinal)
            .Select(n => new MeetingNoteDto(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt))
            .ToListAsync();
    }

    /// <summary>Bulk append (the live panel attaches all its lines after upload; single adds send one line).
    /// Blank lines are skipped, text trimmed, ordinals continue after existing lines.</summary>
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> Create(Guid recordingId, CreateMeetingNotesRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();

        var next = (await _db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .Select(n => (int?)n.Ordinal)
            .MaxAsync() ?? -1) + 1;

        var fresh = new List<MeetingNote>();
        foreach (var line in req.Lines)
        {
            var text = (line.Text ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.Length > 2048) text = text[..2048];
            fresh.Add(new MeetingNote
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                RecordingId = recordingId,
                Text = text,
                CapturedAtMs = line.CapturedAtMs,
                Ordinal = next++,
            });
        }
        _db.MeetingNotes.AddRange(fresh);
        await _db.SaveChangesAsync();
        return fresh.Select(ToDto).ToList();
    }

    [HttpPut("{noteId:guid}")]
    public async Task<IActionResult> Update(Guid recordingId, Guid noteId, UpdateMeetingNoteRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var note = await _db.MeetingNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.RecordingId == recordingId);
        if (note is null) return NotFound();

        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return BadRequest("Note text is required.");
        note.Text = text.Length > 2048 ? text[..2048] : text;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{noteId:guid}")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid noteId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var note = await _db.MeetingNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.RecordingId == recordingId);
        if (note is null) return NotFound();
        _db.MeetingNotes.Remove(note);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
```

- [ ] **Step 5: Run, verify PASS**, then commit:

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/MeetingNotesController.cs tests/Diariz.Api.Tests/MeetingNotesControllerTests.cs
git commit -m "feat(notes): recording notes CRUD (/api/recordings/{id}/notes)"
```

---

## Task 3: Event-scoped notes CRUD (pre-meeting)

**Files:** Create `src/Diariz.Api/Controllers/CalendarEventNotesController.cs`; test `tests/Diariz.Api.Tests/CalendarEventNotesControllerTests.cs`.

- [ ] **Step 1: Write the failing tests** - same shapes as Task 2 but event-anchored and **user-scoped**
(no recording to own; a stranger simply sees their own empty list). Cover: create trims/skips-blank/assigns
ordinals; list is user+event scoped (another user's notes for the same event are invisible); update/delete
own-only (404 for another user's note id); no `CapturedAtMs` on event notes (request lines may carry one but
it is ignored/stored null - assert stored null).

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class CalendarEventNotesControllerTests
{
    private static CalendarEventNotesController Build(DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Create_List_AreUserAndEventScoped_AndIgnoreCapturedAt()
    {
        var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await Build(db, alice).Create("cal1", "evt1", new CreateMeetingNotesRequest([new("agenda point", 9_999)]));
        await Build(db, bob).Create("cal1", "evt1", new CreateMeetingNotesRequest([new("bob's note")]));

        var aliceList = (await Build(db, alice).List("cal1", "evt1")).Value!;
        Assert.Single(aliceList);
        Assert.Equal("agenda point", aliceList[0].Text);
        Assert.Null(aliceList[0].CapturedAtMs); // event notes never carry a recording-clock stamp

        Assert.Empty((await Build(db, alice).List("cal1", "evt2")).Value!); // other event
    }

    [Fact]
    public async Task Update_Delete_OwnLinesOnly()
    {
        var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var created = (await Build(db, alice).Create("cal1", "evt1", new CreateMeetingNotesRequest([new("x")]))).Value![0];

        Assert.IsType<NotFoundResult>(await Build(db, Guid.NewGuid()).Update("cal1", "evt1", created.Id, new UpdateMeetingNoteRequest("y")));
        Assert.IsType<NoContentResult>(await Build(db, alice).Update("cal1", "evt1", created.Id, new UpdateMeetingNoteRequest("y")));
        Assert.IsType<NoContentResult>(await Build(db, alice).Delete("cal1", "evt1", created.Id));
        Assert.Empty(await db.MeetingNotes.ToListAsync());
    }
}
```

- [ ] **Step 2: Run, verify FAIL.** `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~CalendarEventNotesControllerTests`

- [ ] **Step 3: Implement** (`CalendarEventNotesController.cs`) - same skeleton as Task 2's controller with:
route `[Route("api/calendar/events/{calendarId}/{eventId}/notes")]` (string route params, URL-decoded by
ASP.NET automatically); scoping predicate `n => n.UserId == UserId && n.CalendarId == calendarId && n.EventId == eventId && n.RecordingId == null`;
`Create` stores `CapturedAtMs = null` always and sets `CalendarId`/`EventId` (truncate each to 256 chars);
no `OwnsAsync` (the anchor IS the user); ordinals per `(UserId, CalendarId, EventId)`.

- [ ] **Step 4: Run, verify PASS**, then commit:

```bash
git add src/Diariz.Api/Controllers/CalendarEventNotesController.cs tests/Diariz.Api.Tests/CalendarEventNotesControllerTests.cs
git commit -m "feat(notes): pre-meeting event notes CRUD (/api/calendar/events/.../notes)"
```

---

## Task 4: Adoption at the calendar-link chokepoint

**Files:** Create `src/Diariz.Api/Services/MeetingNoteAdoption.cs`; modify `src/Diariz.Api/Controllers/RecordingsController.cs` (`LinkCalendar`); test `tests/Diariz.Api.Tests/MeetingNoteAdoptionTests.cs`.

- [ ] **Step 1: Write the failing tests**:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class MeetingNoteAdoptionTests
{
    [Fact]
    public async Task Adopt_MovesEventNotesOntoRecording_AppendingAfterExistingLines_AndClearsEventKeys()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user, Title = "R" };
        db.Recordings.Add(rec);
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, RecordingId = rec.Id, Text = "live line", CapturedAtMs = 1000, Ordinal = 0 });
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal1", EventId = "evt1", Text = "prep 1", Ordinal = 0 });
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal1", EventId = "evt1", Text = "prep 2", Ordinal = 1 });
        await db.SaveChangesAsync();

        var adopted = await MeetingNoteAdoption.AdoptAsync(db, user, rec.Id, "cal1", "evt1", default);

        Assert.Equal(2, adopted);
        var lines = await db.MeetingNotes.Where(n => n.RecordingId == rec.Id).OrderBy(n => n.Ordinal).ToListAsync();
        Assert.Equal(["live line", "prep 1", "prep 2"], lines.Select(n => n.Text));
        Assert.Equal([0, 1, 2], lines.Select(n => n.Ordinal));
        Assert.All(lines, n => { Assert.Null(n.CalendarId); Assert.Null(n.EventId); });
    }

    [Fact]
    public async Task Adopt_TouchesOnlyTheOwnersNotes_ForThatEvent()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user, Title = "R" };
        db.Recordings.Add(rec);
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), CalendarId = "cal1", EventId = "evt1", Text = "someone else's", Ordinal = 0 });
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal1", EventId = "other", Text = "other event", Ordinal = 0 });
        await db.SaveChangesAsync();

        Assert.Equal(0, await MeetingNoteAdoption.AdoptAsync(db, user, rec.Id, "cal1", "evt1", default));
        Assert.Equal(2, await db.MeetingNotes.CountAsync(n => n.RecordingId == null)); // both untouched
    }
}
```

- [ ] **Step 2: Run, verify FAIL**, then **implement** (`MeetingNoteAdoption.cs`):

```csharp
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Moves a user's event-anchored note lines onto a recording when its calendar link forms (the
/// LinkCalendar chokepoint - both the auto-match save and manual linking). One-way and additive: adopted
/// lines append after any lines already on the recording; unlinking never detaches notes; linking to a
/// different event later adopts that event's notes too. Caller SaveChanges.</summary>
public static class MeetingNoteAdoption
{
    /// <summary>Returns how many lines were adopted. Does not save.</summary>
    public static async Task<int> AdoptAsync(
        DiarizDbContext db, Guid userId, Guid recordingId, string calendarId, string eventId, CancellationToken ct)
    {
        var pending = await db.MeetingNotes
            .Where(n => n.UserId == userId && n.RecordingId == null
                        && n.CalendarId == calendarId && n.EventId == eventId)
            .OrderBy(n => n.Ordinal)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var next = (await db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .Select(n => (int?)n.Ordinal)
            .MaxAsync(ct) ?? -1) + 1;

        foreach (var note in pending)
        {
            note.RecordingId = recordingId;
            note.CalendarId = null;
            note.EventId = null;
            note.Ordinal = next++;
        }
        return pending.Count;
    }
}
```

- [ ] **Step 3: Hook the chokepoint** - in `RecordingsController.LinkCalendar` (line ~591), just before the
existing `await _db.SaveChangesAsync(ct);`:

```csharp
        // Adopt any pre-meeting notes the user attached to this event (one-way, additive - see MeetingNoteAdoption).
        await MeetingNoteAdoption.AdoptAsync(_db, UserId, rec.Id, link.CalendarId, link.EventId, ct);
        await _db.SaveChangesAsync(ct);
```

- [ ] **Step 4: Add a chokepoint test** - in `MeetingNoteAdoptionTests.cs` is enough for the logic; extend an
existing `LinkCalendar_` success-path test in `RecordingsControllerTests.cs` (the one with the fake calendar
client that returns an event - search `LinkCalendar_`) to seed one event-anchored note and assert it has
`RecordingId` set after the call.

- [ ] **Step 5: Run all** `dotnet test tests/Diariz.Api.Tests` → all pass. Commit:

```bash
git add src/Diariz.Api/Services/MeetingNoteAdoption.cs src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests/MeetingNoteAdoptionTests.cs tests/Diariz.Api.Tests/RecordingsControllerTests.cs
git commit -m "feat(notes): adopt pre-meeting event notes when the calendar link forms"
```

---

## Task 5: Integration tests (real Postgres)

**Files:** Create `tests/Diariz.Api.IntegrationTests/MeetingNotesIntegrationTests.cs` (collection
`IntegrationCollection.Name`, model on `McpTokensIntegrationTests.cs` - seed users via `fx.CreateDbContext()`).

- [ ] **Step 1: Write + run.** Cover:
  - notes round-trip via `MeetingNotesController` against real PG (create → list ordering by ordinal);
  - **cascade:** deleting the recording removes its notes; deleting a user removes their event-anchored notes;
  - **adoption over real PG:** seed event notes + recording notes, call `MeetingNoteAdoption.AdoptAsync` +
    `SaveChangesAsync`, assert merged ordinals and cleared event keys.

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter FullyQualifiedName~MeetingNotesIntegrationTests` (Docker) → PASS.

- [ ] **Step 2: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/MeetingNotesIntegrationTests.cs
git commit -m "test(notes): real-Postgres round-trip, cascade, adoption"
```

---

## Task 6: Web - api client + types

**Files:** Modify `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`.

- [ ] **Step 1: Types** (beside `RecordingAction`):

```typescript
/// One line of the user's own meeting notes. capturedAtMs = offset into the recording clock
/// (null = pre-meeting/post-hoc); immutable after capture.
export interface MeetingNote {
  id: string;
  text: string;
  capturedAtMs: number | null;
  ordinal: number;
  createdAt: string;
}
```

- [ ] **Step 2: api methods** (beside the actions methods; import `MeetingNote`):

```typescript
  // ---- Meeting notes (the user's own note lines) ----

  async listNotes(recordingId: string): Promise<MeetingNote[]> {
    const { data } = await http.get<MeetingNote[]>(`/api/recordings/${recordingId}/notes`);
    return data;
  },
  /// Bulk append; used for single adds too. Returns the created lines.
  async createNotes(recordingId: string, lines: { text: string; capturedAtMs?: number | null }[]): Promise<MeetingNote[]> {
    const { data } = await http.post<MeetingNote[]>(`/api/recordings/${recordingId}/notes`, { lines });
    return data;
  },
  async updateNote(recordingId: string, noteId: string, text: string): Promise<void> {
    await http.put(`/api/recordings/${recordingId}/notes/${noteId}`, { text });
  },
  async deleteNote(recordingId: string, noteId: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/notes/${noteId}`);
  },

  async listEventNotes(calendarId: string, eventId: string): Promise<MeetingNote[]> {
    const { data } = await http.get<MeetingNote[]>(
      `/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes`);
    return data;
  },
  async createEventNotes(calendarId: string, eventId: string, lines: { text: string }[]): Promise<MeetingNote[]> {
    const { data } = await http.post<MeetingNote[]>(
      `/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes`, { lines });
    return data;
  },
  async updateEventNote(calendarId: string, eventId: string, noteId: string, text: string): Promise<void> {
    await http.put(`/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes/${noteId}`, { text });
  },
  async deleteEventNote(calendarId: string, eventId: string, noteId: string): Promise<void> {
    await http.delete(`/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes/${noteId}`);
  },
```

- [ ] **Step 3: Typecheck** `cd apps/web && npm run build` → clean. Commit:

```bash
git add apps/web/src/lib/api.ts apps/web/src/lib/types.ts
git commit -m "feat(notes): web api client + types for meeting notes"
```

---

## Task 7: Web - shared `NotesSection` component

One component serves both surfaces: a list of lines (optional `mm:ss` stamp badge, click → `onJump(ms)`),
inline edit, delete, and an add box (Enter adds). Kept dumb: all persistence via callbacks.

**Files:** Create `apps/web/src/components/NotesSection.tsx` + `apps/web/src/components/NotesSection.test.tsx`; add i18n keys.

- [ ] **Step 1: i18n keys** in all four `apps/web/src/locales/{lang}/workspace.json` (translate es/fr/de;
plain hyphens): `"notesAddPlaceholder": "Add a note..."`, `"notesAdd": "Add"`, `"notesEmpty": "No notes yet. Jot trigger phrases here - they will steer the meeting minutes."`,
`"notesEdit": "Edit note"`, `"notesDelete": "Delete note"`, `"notesSave": "Save"`, `"notesCancel": "Cancel"`,
`"notesJump": "Jump to {{time}} in the transcript"`, `"detailTabNotes": "Notes"`, `"eventNotesTitle": "Your notes"`,
`"eventNotesHint": "Prep notes for this meeting. They attach to the recording automatically once it is linked."`.

- [ ] **Step 2: Write the failing test** (`NotesSection.test.tsx`):

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import NotesSection from "./NotesSection";
import type { MeetingNote } from "../lib/types";

const note = (over: Partial<MeetingNote> = {}): MeetingNote => ({
  id: "n1", text: "Comp expectations", capturedAtMs: 61_000, ordinal: 0,
  createdAt: new Date().toISOString(), ...over,
});

describe("NotesSection", () => {
  it("lists lines with mm:ss stamps; clicking a stamp jumps", () => {
    const onJump = vi.fn();
    render(<NotesSection notes={[note()]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} onJump={onJump} />);
    fireEvent.click(screen.getByRole("button", { name: /jump to 1:01/i }));
    expect(onJump).toHaveBeenCalledWith(61_000);
  });

  it("shows no stamp for unstamped lines", () => {
    render(<NotesSection notes={[note({ capturedAtMs: null })]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.queryByRole("button", { name: /jump to/i })).toBeNull();
  });

  it("adds a line on Enter and clears the box", () => {
    const onAdd = vi.fn();
    render(<NotesSection notes={[]} onAdd={onAdd} onEdit={vi.fn()} onDelete={vi.fn()} />);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "IPO experience APAC" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("IPO experience APAC");
    expect((box as HTMLInputElement).value).toBe("");
  });

  it("edits and deletes via the row controls", () => {
    const onEdit = vi.fn(); const onDelete = vi.fn();
    render(<NotesSection notes={[note()]} onAdd={vi.fn()} onEdit={onEdit} onDelete={onDelete} />);
    fireEvent.click(screen.getByRole("button", { name: /edit note/i }));
    const input = screen.getByDisplayValue("Comp expectations");
    fireEvent.change(input, { target: { value: "Comp + equity" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    expect(onEdit).toHaveBeenCalledWith("n1", "Comp + equity");
    fireEvent.click(screen.getByRole("button", { name: /delete note/i }));
    expect(onDelete).toHaveBeenCalledWith("n1");
  });
});
```

- [ ] **Step 3: Run, verify FAIL**, then **implement** `NotesSection.tsx`:

```tsx
import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { MeetingNote } from "../lib/types";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/// A dumb list-editor for the user's own note lines: optional mm:ss stamp badge (click -> onJump), inline
/// edit, delete, and an Enter-to-add box. Persistence is the parent's job (recording- or event-backed).
export default function NotesSection({
  notes, onAdd, onEdit, onDelete, onJump,
}: {
  notes: MeetingNote[];
  onAdd: (text: string) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  onJump?: (ms: number) => void;
}) {
  const { t } = useTranslation("workspace");
  const [draft, setDraft] = useState("");
  const [editing, setEditing] = useState<string | null>(null);
  const [editText, setEditText] = useState("");

  function add() {
    const text = draft.trim();
    if (!text) return;
    onAdd(text);
    setDraft("");
  }

  const btn = "rounded border px-1.5 py-0.5 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="space-y-2">
      <div className="flex gap-2">
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); add(); } }}
          placeholder={t("notesAddPlaceholder")}
          aria-label={t("notesAddPlaceholder")}
          className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <button type="button" onClick={add} className={btn}>{t("notesAdd")}</button>
      </div>

      <ul className="space-y-1">
        {notes.map((n) => (
          <li key={n.id} className="flex items-start gap-2 text-sm dark:text-gray-200">
            {n.capturedAtMs != null && onJump ? (
              <button
                type="button"
                onClick={() => onJump(n.capturedAtMs!)}
                aria-label={t("notesJump", { time: fmt(n.capturedAtMs) })}
                className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 font-mono text-[11px] text-gray-600 hover:bg-gray-200 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700"
              >
                {fmt(n.capturedAtMs)}
              </button>
            ) : n.capturedAtMs != null ? (
              <span className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 font-mono text-[11px] text-gray-500 dark:bg-gray-800 dark:text-gray-400">{fmt(n.capturedAtMs)}</span>
            ) : null}
            {editing === n.id ? (
              <span className="flex min-w-0 flex-1 items-center gap-1">
                <input
                  value={editText}
                  onChange={(e) => setEditText(e.target.value)}
                  aria-label={t("notesEdit")}
                  className="min-w-0 flex-1 rounded border px-1.5 py-0.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                <button type="button" className={btn} onClick={() => { onEdit(n.id, editText.trim()); setEditing(null); }}>{t("notesSave")}</button>
                <button type="button" className={btn} onClick={() => setEditing(null)}>{t("notesCancel")}</button>
              </span>
            ) : (
              <>
                <span className="min-w-0 flex-1 break-words">{n.text}</span>
                <button type="button" aria-label={t("notesEdit")} className={btn} onClick={() => { setEditing(n.id); setEditText(n.text); }}>✎</button>
                <button type="button" aria-label={t("notesDelete")} className="shrink-0 text-red-600 hover:underline dark:text-red-400" onClick={() => onDelete(n.id)}>✕</button>
              </>
            )}
          </li>
        ))}
        {notes.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("notesEmpty")}</li>}
      </ul>
    </div>
  );
}
```

- [ ] **Step 4: Run, verify PASS** (`npx vitest run src/components/NotesSection.test.tsx src/locales.test.ts`). Commit:

```bash
git add apps/web/src/components/NotesSection.tsx apps/web/src/components/NotesSection.test.tsx apps/web/src/locales
git commit -m "feat(notes): shared NotesSection line editor + i18n"
```

---

## Task 8: Web - Notes tab (recording) + event-notes editor (calendar)

**Files:** Modify `apps/web/src/pages/RecordingDetail.tsx`, `apps/web/src/pages/CalendarEventDetail.tsx`;
extend `apps/web/src/pages/RecordingDetail.test.tsx`.

- [ ] **Step 1: Notes tab.** In `RecordingDetail.tsx`:
  - Query: `const { data: notes = [] } = useQuery({ queryKey: ["notes", id], queryFn: () => api.listNotes(id), enabled: Boolean(id) });`
  - Handlers (invalidate `["notes", id]` after each): `addNote(text)` → `api.createNotes(id, [{ text }])`;
    `editNote(noteId, text)` → `api.updateNote`; `removeNote(noteId)` → `api.deleteNote`.
  - Jump: `jumpToMs(ms)` sets the `t` search param and switches to the transcript tab - reuse the existing
    pattern (the page already treats `?t=` as "open transcript + scroll", see line ~194/232):
    `setSearchParams(prev => { prev.set("t", String(ms)); return prev; }, { replace: true }); selectTab("transcript");`
  - Tab entry inserted **after `actions`, before `speakers`** in the `detailTabs` array:

```tsx
    {
      key: "notes",
      label: t("workspace:detailTabNotes"),
      content: (
        <div className="px-4 pb-4">
          <NotesSection notes={notes} onAdd={addNote} onEdit={editNote} onDelete={removeNote} onJump={jumpToMs} />
        </div>
      ),
    },
```

- [ ] **Step 2: Event notes on the preview page.** In `CalendarEventDetail.tsx`, under the
`<CalendarEventDetails event={event} />` block, add a bordered "Your notes" section (event-anchored;
`event.calendarId ?? "primary"`):

```tsx
  const calId = event.calendarId ?? "primary";
  const { data: eventNotes = [] } = useQuery({
    queryKey: ["event-notes", calId, event.id],
    queryFn: () => api.listEventNotes(calId, event.id),
  });
  // handlers mirror the recording ones via createEventNotes/updateEventNote/deleteEventNote,
  // invalidating ["event-notes", calId, event.id]; render:
  <div className="mt-4 border-t pt-3 dark:border-gray-700">
    <h3 className="mb-1 text-sm font-medium text-gray-700 dark:text-gray-200">{t("eventNotesTitle")}</h3>
    <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("eventNotesHint")}</p>
    <NotesSection notes={eventNotes} onAdd={addEventNote} onEdit={editEventNote} onDelete={removeEventNote} />
  </div>
```

(No `onJump` - there's no recording yet.)

- [ ] **Step 3: Page tests.** Extend `RecordingDetail.test.tsx` (mock `api.listNotes` → one stamped line;
add `listNotes/createNotes/updateNote/deleteNote` to the api mock):
  - "shows the Notes tab and lists notes" - open tab, assert the line text renders;
  - "adds a note from the Notes tab" - type + Enter, assert `api.createNotes` called with `[{ text }]`;
  - "clicking a note stamp navigates to the transcript" - click the stamp, assert the transcript tab is
    selected (`role=tab` aria-selected) - the `?t=` effect itself is already covered by existing tests.

- [ ] **Step 4: Run** `npx vitest run` (full web suite) + `npm run build` → all pass. Commit:

```bash
git add apps/web/src/pages/RecordingDetail.tsx apps/web/src/pages/RecordingDetail.test.tsx apps/web/src/pages/CalendarEventDetail.tsx
git commit -m "feat(notes): Notes tab on the recording page + event notes on the calendar preview"
```

---

## Task 9: Docs, version, release + verification

**Files:** `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`, `apps/web/src/lib/releases.ts`,
version mirrors, and commit the spec + this plan.

- [ ] **Step 1: `Data_Schema.md`** - add the `MeetingNotes` table (all columns/indexes/FKs/cascades, the
either-recording-or-event anchoring, adoption behaviour) + `AddMeetingNotes` in the migration-history table.

- [ ] **Step 2: `Overall_Synopsis_of_Platform.md`** - a "Meeting notes (user notes)" paragraph: the entity,
the two capture surfaces in this PR, adoption at the LinkCalendar chokepoint, and a forward pointer that live
capture (PR 2) and the minutes weave (PR 3) follow.

- [ ] **Step 3: `CAPABILITIES` + release entry** - CAPABILITIES: one sentence (jot your own timestamped notes
on a recording or an upcoming meeting; prep notes attach automatically when the recording links). RELEASES[0]:
**0.103.0**, the PR number, headline "Your own meeting notes - on recordings and upcoming meetings", added
bullets, "Server redeploy (web + API); migration auto-applies; no desktop release."

- [ ] **Step 4: Version bump** - 0.102.2 → **0.103.0** across `version.json`, `apps/web/package.json`,
`apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, regenerate both lockfiles.

- [ ] **Step 5: Full verification** - `dotnet build Diariz.slnx` (0 errors); `dotnet test tests/Diariz.Api.Tests`;
`dotnet test tests/Diariz.Api.IntegrationTests --filter FullyQualifiedName~MeetingNotes` (Docker);
`cd apps/web && npm run build && npx vitest run` (incl. releases + locales gates).

- [ ] **Step 6: Live verification** - rebuild the api container; in the browser: add notes on a recording's
Notes tab (edit/delete/stamp-click jumps the transcript); add prep notes on a calendar event preview; link
that event to a recording (or open a time-matched recording so the auto-link fires) and confirm the prep
notes appear on the recording's Notes tab, after any existing lines.

- [ ] **Step 7: Commit**

```bash
git add docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md apps/web/src/lib/releases.ts version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj docs/superpowers/specs/2026-07-07-enhanced-notes-design.md docs/superpowers/plans/2026-07-07-enhanced-notes-pr-1-capture.md
git commit -m "docs+release: meeting notes capture (0.103.0)"
```

---

## Deploy surface

Server redeploy (web + API); migration `AddMeetingNotes` auto-applies; no worker rebuild; no desktop release.

## Out of scope (later PRs)

**PR 2:** LiveNotesPanel (recorder toggle, recorded-ms stamps via `recorderTiming`, IndexedDB mirror,
attach-on-upload + retry). **PR 3:** the minutes weave (steering preamble, `NotesEnhancer`, `NotesComposer`,
`notes` template field, General-template seeding, Notes-tab "Re-create minutes").
