namespace Envisia.Typst;

/// <summary>A font handed to the Typst compiler. Typst never reads fonts from the machine.</summary>
/// <param name="Data">A TrueType or OpenType font file, or a collection of them.</param>
public sealed record TypstFont(ReadOnlyMemory<byte> Data);
