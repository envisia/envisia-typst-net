namespace Envisia.Typst;

/// <summary>Everything a single <see cref="TypstCompiler.CompilePdf"/> call needs.</summary>
public sealed class TypstCompileRequest
{
    /// <summary>The main document's Typst markup. Diagnostics refer to it as <c>main.typ</c>.</summary>
    public required string Markup { get; init; }

    /// <summary>The fonts the document may use; at least one is required. No system font is ever consulted.</summary>
    public required IReadOnlyList<TypstFont> Fonts { get; init; }

    /// <summary>The files the markup can read, include or import by name.</summary>
    public IReadOnlyList<TypstFile> Files { get; init; } = [];

    /// <summary>
    /// The date Typst's <c>datetime.today()</c> resolves to. Left unset, that call fails to compile, which keeps
    /// documents deterministic instead of hiding a wall clock read inside the renderer.
    /// </summary>
    public DateOnly? Today { get; init; }
}
