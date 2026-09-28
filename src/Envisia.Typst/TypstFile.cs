namespace Envisia.Typst;

/// <summary>
/// A file the markup can reference by name, for example through Typst's <c>image()</c>, <c>json()</c> or
/// <c>#import</c>.
/// </summary>
/// <param name="Name">The path the markup refers to, relative to the main document, for example <c>data.json</c>.</param>
/// <param name="Data">
/// The file's content. Memory of a <see cref="TypstBuffer"/> is shared with Typst, any other memory is copied on
/// every call.
/// </param>
public sealed record TypstFile(string Name, ReadOnlyMemory<byte> Data)
{
    /// <summary>A file whose content is the whole <paramref name="buffer"/>.</summary>
    public TypstFile(string name, TypstBuffer buffer)
        : this(name, (buffer ?? throw new ArgumentNullException(nameof(buffer))).Memory) { }
}
