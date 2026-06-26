using System.Text;
using UglyToad.PdfPig;

namespace Diariz.Api.Services;

/// <summary>Extracted text from an uploaded attachment.</summary>
public sealed record AttachmentText(string Name, string Text, int Chars);

public interface IAttachmentExtractor
{
    bool IsSupported(string fileName, string? contentType);
    AttachmentText Extract(string fileName, string? contentType, byte[] bytes);
}

/// <summary>Turns an uploaded PDF or text-based file into plain text for use as chat context.
/// PDFs are read page-by-page with PdfPig; text files are decoded as UTF-8 (BOM-aware).</summary>
public sealed class AttachmentExtractor : IAttachmentExtractor
{
    private const int MaxChars = 200_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".mdown", ".csv", ".tsv", ".log", ".json", ".yaml", ".yml", ".rst",
    };

    public bool IsSupported(string fileName, string? contentType) =>
        IsPdf(fileName, contentType) || IsText(fileName, contentType);

    public AttachmentText Extract(string fileName, string? contentType, byte[] bytes)
    {
        var name = SafeName(fileName);
        if (bytes is null || bytes.Length == 0)
            return new AttachmentText(name, "", 0);

        string text;
        if (IsPdf(fileName, contentType))
            text = ExtractPdf(bytes);
        else if (IsText(fileName, contentType))
            text = DecodeText(bytes);
        else
            throw new ArgumentException("Only PDF and text-based files (.pdf, .txt, .md, …) are supported.");

        text = text.Trim();
        if (text.Length > MaxChars)
            text = text[..MaxChars] + "\n[document truncated]";
        return new AttachmentText(name, text, text.Length);
    }

    private static bool IsPdf(string fileName, string? contentType) =>
        HasExtension(fileName, ".pdf")
        || string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (!string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext)) return true;
        return contentType is { } ct && ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    private static string DecodeText(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes, start, bytes.Length - start);
    }

    private static bool HasExtension(string fileName, string ext) =>
        Path.GetExtension(fileName ?? "").Equals(ext, StringComparison.OrdinalIgnoreCase);

    private static string SafeName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "").Trim();
        return string.IsNullOrEmpty(name) ? "attachment" : name;
    }
}
