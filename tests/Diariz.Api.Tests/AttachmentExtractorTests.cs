using System.Text;
using Diariz.Api.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MimeKit;

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

    [Theory]
    [InlineData(@"..\..\notes.txt")]
    [InlineData("../../notes.txt")]
    public void Extract_StripsSmuggledPath_FromName(string smuggled)
    {
        // Must strip on every platform — Path.GetFileName only handles '\' on Windows.
        var result = Extractor.Extract(smuggled, "text/plain", Encoding.UTF8.GetBytes("x"));
        Assert.Equal("notes.txt", result.Name);
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

    [Theory]
    [InlineData("report.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("invite.ics", "text/calendar")]
    [InlineData("mail.eml", "message/rfc822")]
    public void IsSupported_AcceptsOfficeEmailAndCalendar(string name, string contentType) =>
        Assert.True(Extractor.IsSupported(name, contentType));

    [Fact]
    public void Extract_Docx_PullsBodyText()
    {
        var result = Extractor.Extract("notes.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Docx("The quarterly numbers look strong."));

        Assert.Contains("quarterly numbers look strong", result.Text);
    }

    [Fact]
    public void Extract_Eml_IncludesSubjectAndBody()
    {
        var result = Extractor.Extract("mail.eml", "message/rfc822", Eml("Budget review", "Please cut spend by 10%."));

        Assert.Contains("Budget review", result.Text);
        Assert.Contains("cut spend by 10%", result.Text);
    }

    [Fact]
    public void Extract_Ics_SurfacesEventProperties()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Sprint planning\r\n" +
                  "DTSTART;TZID=UTC:20260701T090000\r\nLOCATION:Room 4\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var result = Extractor.Extract("invite.ics", "text/calendar", Encoding.UTF8.GetBytes(ics));

        Assert.Contains("SUMMARY: Sprint planning", result.Text);
        Assert.Contains("LOCATION: Room 4", result.Text);
        Assert.Contains("DTSTART: 20260701T090000", result.Text); // TZID param stripped from the key
    }

    private static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] Eml(string subject, string body)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Alice", "alice@x.test"));
        msg.To.Add(new MailboxAddress("Bob", "bob@x.test"));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        return ms.ToArray();
    }
}
