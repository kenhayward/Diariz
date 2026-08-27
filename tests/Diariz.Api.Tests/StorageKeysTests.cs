using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>What may go into an object-storage key, given the client picked the filename.
///
/// <para>A key is a path in the object store, it becomes the worker's temporary file path - and so any
/// exception text from there, including what reaches our log - and for a recording it is read back out to
/// build the <c>Content-Disposition</c> filename on download. One bad value shows up in all three.</para>
/// </summary>
public class StorageKeysTests
{

    // ---- The extension, which is the one thing here that comes from the client's own filename ----
    //
    // This class exists so a client-supplied extension is never trusted, and the storage key was taking it
    // raw. Path.GetExtension returns everything after the final dot, so it is an arbitrary client string,
    // and it reaches the object key, the worker's temp path (and from there exception text and the log),
    // and the Content-Disposition header - where the name half is slugged and this half was not.

    [Theory]
    [InlineData("meeting.wav", ".wav")]
    [InlineData("meeting.WAV", ".wav")]
    [InlineData("a.b.mp3", ".mp3")]
    [InlineData("no-extension", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SafeExtension_keeps_an_ordinary_suffix(string? fileName, string expected) =>
        Assert.Equal(expected, StorageKeys.SafeExtension(fileName));

    [Theory]
    // A newline is what forges a log line once this reaches the worker's error text.
    [InlineData("x.wav\nINFO forged")]
    [InlineData("x.wav\r\nINFO forged")]
    // A quote closes the filename in Content-Disposition early.
    [InlineData("x.wav\"")]
    [InlineData("x.wa v")]
    [InlineData("x.wav;rm")]
    // Separators would reshape the object key rather than name a file in it.
    [InlineData("x.wav/../../etc")]
    [InlineData("x.thisisnotanextension")]
    public void SafeExtension_drops_anything_that_is_not_a_plain_suffix(string fileName) =>
        Assert.Equal("", StorageKeys.SafeExtension(fileName));
}
