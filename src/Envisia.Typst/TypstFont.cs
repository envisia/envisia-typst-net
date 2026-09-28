namespace Envisia.Typst;

/// <summary>A font handed to the Typst compiler. Typst never reads fonts from the machine.</summary>
/// <param name="Data">
/// A TrueType or OpenType font file, or a collection of them. Memory of a <see cref="TypstBuffer"/> is shared with
/// Typst, any other memory is copied on every call.
/// </param>
public sealed record TypstFont(ReadOnlyMemory<byte> Data)
{
    /// <summary>A font read from the whole <paramref name="buffer"/>.</summary>
    public TypstFont(TypstBuffer buffer)
        : this((buffer ?? throw new ArgumentNullException(nameof(buffer))).Memory) { }
}
