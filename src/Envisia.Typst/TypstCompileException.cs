namespace Envisia.Typst;

/// <summary>The markup did not compile, or the native layer rejected the inputs.</summary>
public sealed class TypstCompileException : Exception
{
    /// <summary>Creates the exception from Typst's formatted diagnostics.</summary>
    public TypstCompileException(string diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Typst's own diagnostics, one per line, with source positions and hints where Typst supplies them.</summary>
    public string Diagnostics { get; }

    private static string BuildMessage(string diagnostics)
    {
        return string.IsNullOrEmpty(diagnostics)
            ? "typst failed to compile the document"
            : $"typst failed to compile the document:{Environment.NewLine}{diagnostics}";
    }
}
