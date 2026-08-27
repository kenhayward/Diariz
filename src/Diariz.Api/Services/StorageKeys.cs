namespace Diariz.Api.Services;

/// <summary>Building object-storage keys out of values that came from a client.
///
/// <para>A key is not a private detail. It is a path in the object store, it becomes the worker's temporary
/// file path - and so any text an exception there produces, including what reaches our log - and for
/// recordings it is read back out to form the <c>Content-Disposition</c> filename on download. Whatever
/// goes into one has to survive all three.</para></summary>
public static class StorageKeys
{
    /// <summary>The suffix of <paramref name="fileName"/>, but only when it is an ordinary one: a dot and
    /// up to eight letters or digits, lowercased. Anything else - punctuation, whitespace, a separator, a
    /// control character, or something far too long to be an extension - yields the empty string.
    ///
    /// <para><c>Path.GetExtension</c> returns everything after the final dot, so used directly on a
    /// client's filename it is an arbitrary client-controlled string rather than an extension. Every key
    /// built from an upload used it that way.</para>
    ///
    /// <para>Dropping a bad one is safe: nothing needs the extension to be there. The content type is
    /// stored alongside the row, and audio is identified by decoding it rather than by its name.</para>
    ///
    /// <para>Deliberately an allow-list. A deny-list of "characters we thought of" is the version of this
    /// that quietly stops covering the case nobody predicted.</para></summary>
    public static string SafeExtension(string? fileName)
    {
        var ext = string.IsNullOrEmpty(fileName) ? "" : Path.GetExtension(fileName);
        if (ext.Length < 2 || ext.Length > 9 || ext[0] != '.') return "";

        foreach (var c in ext.AsSpan(1))
            if (!char.IsAsciiLetterOrDigit(c))
                return "";

        return ext.ToLowerInvariant();
    }
}
