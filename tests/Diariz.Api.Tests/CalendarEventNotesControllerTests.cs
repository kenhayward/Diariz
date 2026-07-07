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
    public async Task Create_AssignsOrdinals_PerUserAndEvent()
    {
        var db = TestDb.Create();
        var alice = Guid.NewGuid();
        await Build(db, alice).Create("cal1", "evt1", new CreateMeetingNotesRequest([new("one")]));
        var second = (await Build(db, alice).Create("cal1", "evt1", new CreateMeetingNotesRequest([new("two")]))).Value!;
        Assert.Equal(1, second[0].Ordinal);
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
