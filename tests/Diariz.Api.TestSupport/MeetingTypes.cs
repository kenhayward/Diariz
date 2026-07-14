using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Building a meeting type in a test used to be one object: the type carried its own template. It no
/// longer does - a type points at the <b>formula</b> whose template generates its minutes - so a usable type is
/// now always two rows. This keeps that pairing in one place instead of spreading it across every test that needs
/// a template.</summary>
public static class MeetingTypes
{
    /// <summary>A meeting type plus the formula carrying <paramref name="content"/>, both added to
    /// <paramref name="db"/> (not yet saved). Returns the type, with <c>PrimaryFormulaId</c> already linked.</summary>
    public static MeetingType With(
        DiarizDbContext db,
        TemplateContent content,
        string title = "Type",
        string? key = null,
        Guid? userId = null,
        Guid? roomId = null,
        FormulaScope scope = FormulaScope.Platform,
        string overview = "")
    {
        var formula = new Formula
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            OwnerUserId = scope == FormulaScope.Personal ? userId : null,
            Name = $"{title} minutes",
            ContentJson = content.Serialize(),
            Context = FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Actions,
            Enabled = true,
        };

        var type = new MeetingType
        {
            Id = Guid.NewGuid(),
            Key = key,
            UserId = userId,
            RoomId = roomId,
            GroupName = "Standard",
            Title = title,
            Overview = overview,
            Icon = "document",
            Color = "#5C6BC0",
            PrimaryFormulaId = formula.Id,
        };

        db.Formulas.Add(formula);
        db.MeetingTypes.Add(type);
        return type;
    }

    /// <summary>The template a meeting type currently points at - i.e. its primary formula's content.</summary>
    public static TemplateContent ContentOf(DiarizDbContext db, MeetingType type)
    {
        var formula = db.Formulas.Single(f => f.Id == type.PrimaryFormulaId);
        return TemplateContent.Parse(formula.ContentJson);
    }
}
