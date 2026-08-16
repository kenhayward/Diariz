using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The scope is how a call deep inside HttpClient learns who asked for it and why. Without it
/// every row would read Unknown, which is the whole reason the handler alone was not enough.</summary>
public class LlmCallScopeTests
{
    [Fact]
    public void NoScope_MeansNoAmbientContext()
    {
        Assert.Null(LlmCallScope.Active);
    }

    [Fact]
    public void Push_MakesTheContextAmbient_AndDisposeRestoresIt()
    {
        var userId = Guid.NewGuid();
        using (LlmCallScope.Push(LlmCallKind.Summarize, userId: userId, userEmail: "a@b.c"))
        {
            Assert.Equal(LlmCallKind.Summarize, LlmCallScope.Active!.Kind);
            Assert.Equal(userId, LlmCallScope.Active.UserId);
            Assert.Equal("a@b.c", LlmCallScope.Active.UserEmail);
        }

        Assert.Null(LlmCallScope.Active);
    }

    [Fact]
    public void SequenceIncrementsPerCall_WhichIsHowTurnsAreCounted()
    {
        // ChatToolOrchestrator loops without pushing its own scope, so each model round-trip inside one
        // user turn lands here. MAX(Sequence) per operation IS the turn count.
        using var scope = LlmCallScope.Push(LlmCallKind.ChatMessage);
        Assert.Equal(1, scope.NextSequence());
        Assert.Equal(2, scope.NextSequence());
        Assert.Equal(3, scope.NextSequence());
    }

    [Fact]
    public void EachPush_GetsItsOwnOperationId()
    {
        Guid first, second;
        using (var a = LlmCallScope.Push(LlmCallKind.Tags)) first = a.OperationId;
        using (var b = LlmCallScope.Push(LlmCallKind.Tags)) second = b.OperationId;
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NestedPush_RestoresTheOuterScope_NotNull()
    {
        using var outer = LlmCallScope.Push(LlmCallKind.MeetingTypeMinutes);
        using (LlmCallScope.Push(LlmCallKind.Summarize))
            Assert.Equal(LlmCallKind.Summarize, LlmCallScope.Active!.Kind);

        Assert.Equal(LlmCallKind.MeetingTypeMinutes, LlmCallScope.Active!.Kind);
    }

    [Fact]
    public async Task TheScope_FlowsAcrossAwait()
    {
        // Every push site is async and the call it attributes happens after several awaits. If this
        // failed, attribution would silently vanish in exactly the code paths that matter.
        using var scope = LlmCallScope.Push(LlmCallKind.FormulaRun);
        await Task.Yield();
        await Task.Delay(1);
        Assert.Equal(LlmCallKind.FormulaRun, LlmCallScope.Active!.Kind);
    }
}
