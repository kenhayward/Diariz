using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using MimeKit;
using UglyToad.PdfPig;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace Diariz.Api.Services;

/// <summary>Extracted text from an uploaded attachment.</summary>
public sealed record AttachmentText(string Name, string Text, int Chars);

public interface IAttachmentExtractor
{
    bool IsSupported(string fileName, string? contentType);
    AttachmentText Extract(string fileName, string? contentType, byte[] bytes);
}

/// <summary>Turns an uploaded document into plain text for use as chat context. Supports PDF (PdfPig),
/// text-based files, Office formats (.docx/.xlsx/.pptx via OpenXML), emails (.eml via MimeKit), and
/// calendar invites (.ics). Unsupported types raise <see cref="ArgumentException"/>.</summary>
public sealed class AttachmentExtractor : IAttachmentExtractor
{
    private const int MaxChars = 200_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".mdown", ".csv", ".tsv", ".log", ".json", ".yaml", ".yml", ".rst",
    };

    public bool IsSupported(string fileName, string? contentType) =>
        IsPdf(fileName, contentType) || IsDocx(fileName, contentType) || IsXlsx(fileName, contentType)
        || IsPptx(fileName, contentType) || IsEml(fileName, contentType) || IsIcs(fileName, contentType)
        || IsText(fileName, contentType);

    public AttachmentText Extract(string fileName, string? contentType, byte[] bytes)
    {
        var name = SafeName(fileName);
        if (bytes is null || bytes.Length == 0)
            return new AttachmentText(name, "", 0);

        string text;
        if (IsPdf(fileName, contentType)) text = ExtractPdf(bytes);
        else if (IsDocx(fileName, contentType)) text = ExtractDocx(bytes);
        else if (IsXlsx(fileName, contentType)) text = ExtractXlsx(bytes);
        else if (IsPptx(fileName, contentType)) text = ExtractPptx(bytes);
        else if (IsEml(fileName, contentType)) text = ExtractEml(bytes);
        else if (IsIcs(fileName, contentType)) text = ExtractIcs(bytes);   // before IsText (text/calendar)
        else if (IsText(fileName, contentType)) text = DecodeText(bytes);
        else
            throw new ArgumentException(
                "Only PDF, text, Office (.docx/.xlsx/.pptx), email (.eml) and calendar (.ics) files are supported.");

        text = text.Trim();
        if (text.Length > MaxChars)
            text = text[..MaxChars] + "\n[document truncated]";
        return new AttachmentText(name, text, text.Length);
    }

    // ---- format detection ----

    private static bool IsPdf(string fileName, string? contentType) =>
        HasExt(fileName, ".pdf") || CtIs(contentType, "application/pdf");

    private static bool IsDocx(string fileName, string? ct) =>
        HasExt(fileName, ".docx") || CtIs(ct, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

    private static bool IsXlsx(string fileName, string? ct) =>
        HasExt(fileName, ".xlsx") || CtIs(ct, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

    private static bool IsPptx(string fileName, string? ct) =>
        HasExt(fileName, ".pptx") || CtIs(ct, "application/vnd.openxmlformats-officedocument.presentationml.presentation");

    private static bool IsEml(string fileName, string? ct) =>
        HasExt(fileName, ".eml") || CtIs(ct, "message/rfc822");

    private static bool IsIcs(string fileName, string? ct) =>
        HasExt(fileName, ".ics") || CtIs(ct, "text/calendar");

    private static bool IsText(string fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (!string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext)) return true;
        return contentType is { } ct && ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    // ---- extractors ----

    private static string ExtractPdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart?.Document?.Body?.InnerText ?? "";
    }

    private static string ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var wb = doc.WorkbookPart;
        if (wb is null) return "";
        var shared = wb.SharedStringTablePart?.SharedStringTable;
        var sb = new StringBuilder();
        foreach (var sheetPart in wb.WorksheetParts)
            foreach (var row in sheetPart.Worksheet.Descendants<Row>())
            {
                var cells = row.Elements<Cell>()
                    .Select(c => CellText(c, shared))
                    .Where(v => !string.IsNullOrWhiteSpace(v));
                var line = string.Join("\t", cells);
                if (line.Length > 0) sb.AppendLine(line);
            }
        return sb.ToString();
    }

    private static string CellText(Cell cell, SharedStringTable? shared)
    {
        var raw = cell.CellValue?.InnerText ?? cell.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString && shared is not null
            && int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.ChildElements.Count)
            return shared.ChildElements[idx].InnerText;
        return raw ?? "";
    }

    private static string ExtractPptx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = PresentationDocument.Open(ms, false);
        var sb = new StringBuilder();
        var slideParts = doc.PresentationPart?.SlideParts ?? Enumerable.Empty<SlidePart>();
        foreach (var slide in slideParts)
            foreach (var text in slide.Slide.Descendants<Drawing.Text>())
                if (!string.IsNullOrWhiteSpace(text.Text)) sb.AppendLine(text.Text);
        return sb.ToString();
    }

    private static string ExtractEml(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var msg = MimeMessage.Load(ms);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(msg.Subject)) sb.Append("Subject: ").AppendLine(msg.Subject);
        if (msg.From.Count > 0) sb.Append("From: ").AppendLine(msg.From.ToString());
        if (msg.To.Count > 0) sb.Append("To: ").AppendLine(msg.To.ToString());
        sb.Append("Date: ").AppendLine(msg.Date.ToString("u"));
        sb.AppendLine();
        var body = msg.TextBody ?? (msg.HtmlBody is { } html ? HtmlText.ToPlainText(html) : "");
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>Decode an iCalendar (.ics) into a readable summary of its events. Unfolds wrapped lines and
    /// surfaces the human-relevant properties (summary, time, location, organiser, attendees, description).</summary>
    private static string ExtractIcs(byte[] bytes)
    {
        var raw = DecodeText(bytes);
        // Unfold: a line beginning with a space/tab continues the previous one (RFC 5545).
        var unfolded = raw.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n ", "").Replace("\n\t", "");
        var keep = new[] { "SUMMARY", "DTSTART", "DTEND", "LOCATION", "ORGANIZER", "ATTENDEE", "DESCRIPTION" };
        var sb = new StringBuilder();
        foreach (var line in unfolded.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Split(';')[0].ToUpperInvariant(); // strip params (e.g. DTSTART;TZID=…)
            if (!keep.Contains(key)) continue;
            var value = line[(colon + 1)..].Replace("\\n", " ").Replace("\\,", ",").Trim();
            if (value.Length > 0) sb.Append(key).Append(": ").AppendLine(value);
        }
        return sb.ToString();
    }

    private static string DecodeText(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes, start, bytes.Length - start);
    }

    private static bool HasExt(string fileName, string ext) =>
        Path.GetExtension(fileName ?? "").Equals(ext, StringComparison.OrdinalIgnoreCase);

    private static bool CtIs(string? contentType, string value) =>
        string.Equals(contentType, value, StringComparison.OrdinalIgnoreCase);

    private static string SafeName(string? fileName)
    {
        // Strip any directory path cross-platform (take everything after the last '/' or '\'). Path.GetFileName
        // only strips '\' on Windows, so on the Linux servers a name like "a\b.pdf" would keep its path here.
        var raw = fileName ?? "";
        var cut = raw.LastIndexOfAny(new[] { '/', '\\' });
        var name = (cut >= 0 ? raw[(cut + 1)..] : raw).Trim();
        return string.IsNullOrEmpty(name) ? "attachment" : name;
    }
}
