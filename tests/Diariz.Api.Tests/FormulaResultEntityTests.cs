using Diariz.Domain.Entities;
using Xunit;

public class FormulaResultEntityTests
{
    [Fact]
    public void NewFormulaResult_DefaultsToGenerating()
    {
        var r = new FormulaResult();
        Assert.Equal(FormulaRunStatus.Generating, r.Status);
        Assert.Null(r.Error);
    }
}
