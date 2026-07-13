using Diariz.Domain.Entities;
using Xunit;

public class SectionFormulaResultEntityTests
{
    [Fact]
    public void NewSectionFormulaResult_DefaultsToGenerating()
    {
        var r = new SectionFormulaResult();
        Assert.Equal(FormulaRunStatus.Generating, r.Status);
        Assert.Null(r.Error);
    }
}
