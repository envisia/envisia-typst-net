namespace Envisia.Typst;

/// <summary>
/// A file the markup can reference by name, for example through Typst's <c>image()</c>, <c>json()</c> or
/// <c>#import</c>.
/// </summary>
/// <param name="Name">The path the markup refers to, relative to the main document, for example <c>data.json</c>.</param>
/// <param name="Data">The file's content.</param>
public sealed record TypstFile(string Name, ReadOnlyMemory<byte> Data);
