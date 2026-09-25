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

    /// <summary>
    /// The application the PDF names as its creator (<c>/Creator</c> and XMP <c>CreatorTool</c>), which viewers show
    /// as the program that made the document. Left <see langword="null"/>, Typst names itself with its version; an
    /// empty string leaves the entry out.
    /// </summary>
    public string? Creator { get; init; }

    /// <summary>
    /// The PDF standards the document has to conform to, for example <see cref="TypstPdfStandard.PdfA3b"/>. Typst
    /// checks conformance while it writes the PDF and fails the call with diagnostics when the document violates one.
    /// Left empty, the output is a plain PDF 1.7. At most one PDF/A and one PDF/UA standard can be combined, plus a
    /// PDF version they both allow.
    /// </summary>
    public IReadOnlyList<TypstPdfStandard> Standards { get; init; } = [];

    /// <summary>
    /// Whether the PDF carries a structure tree (a tagged PDF) for screen readers and reflow. On by default, as in
    /// Typst. A long document nobody reads through assistive technology renders faster and smaller without it.
    /// PDF/UA and the PDF/A "a" levels require it.
    /// </summary>
    public bool Tagged { get; init; } = true;
}
