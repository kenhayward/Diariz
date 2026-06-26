using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class AttachmentExtractorTests
{
    private static readonly AttachmentExtractor Extractor = new();

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("data.csv", "text/csv")]
    [InlineData("report.pdf", "application/pdf")]
    public void IsSupported_AcceptsTextAndPdf(string name, string contentType) =>
        Assert.True(Extractor.IsSupported(name, contentType));

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("clip.mp3", "audio/mpeg")]
    public void IsSupported_RejectsBinary(string name, string contentType) =>
        Assert.False(Extractor.IsSupported(name, contentType));

    [Fact]
    public void Extract_PlainText_ReturnsTextAndCharCount()
    {
        var bytes = Encoding.UTF8.GetBytes("Hello, world.");
        var result = Extractor.Extract("a.txt", "text/plain", bytes);

        Assert.Equal("a.txt", result.Name);
        Assert.Equal("Hello, world.", result.Text);
        Assert.Equal("Hello, world.".Length, result.Chars);
    }

    [Fact]
    public void Extract_StripsUtf8Bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("body")).ToArray();
        var result = Extractor.Extract("a.txt", "text/plain", bytes);
        Assert.Equal("body", result.Text);
    }

    [Fact]
    public void Extract_CapsLongText()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 250_000));
        var result = Extractor.Extract("big.txt", "text/plain", bytes);
        Assert.True(result.Chars <= 200_000 + 32); // cap + a short truncation marker
        Assert.Contains("truncated", result.Text);
    }

    [Fact]
    public void Extract_Empty_ReturnsEmpty()
    {
        var result = Extractor.Extract("a.txt", "text/plain", []);
        Assert.Equal("", result.Text);
        Assert.Equal(0, result.Chars);
    }

    [Fact]
    public void Extract_Unsupported_Throws() =>
        Assert.Throws<ArgumentException>(() => Extractor.Extract("a.png", "image/png", [1, 2, 3]));

    [Fact]
    public void Extract_Pdf_PullsTextFromPages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hello.pdf");
        var bytes = File.ReadAllBytes(path);

        var result = Extractor.Extract("hello.pdf", "application/pdf", bytes);

        Assert.Equal("hello.pdf", result.Name);
        Assert.Contains("Hello PDF document", result.Text);
    }
}
