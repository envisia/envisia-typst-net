namespace Envisia.Typst;

/// <summary>A PDF standard Typst can enforce conformance with, see <see cref="TypstCompileRequest.Standards"/>.</summary>
public enum TypstPdfStandard
{
    /// <summary>PDF 1.4.</summary>
    Pdf14,

    /// <summary>PDF 1.5.</summary>
    Pdf15,

    /// <summary>PDF 1.6.</summary>
    Pdf16,

    /// <summary>PDF 1.7, what Typst writes when no version is requested.</summary>
    Pdf17,

    /// <summary>PDF 2.0.</summary>
    Pdf20,

    /// <summary>PDF/A-1b (ISO 19005-1, basic conformance).</summary>
    PdfA1b,

    /// <summary>PDF/A-1a (ISO 19005-1, accessible conformance; needs a tagged PDF).</summary>
    PdfA1a,

    /// <summary>PDF/A-2b (ISO 19005-2, basic conformance).</summary>
    PdfA2b,

    /// <summary>PDF/A-2u (ISO 19005-2, all text mapped to Unicode).</summary>
    PdfA2u,

    /// <summary>PDF/A-2a (ISO 19005-2, accessible conformance; needs a tagged PDF).</summary>
    PdfA2a,

    /// <summary>PDF/A-3b (ISO 19005-3, basic conformance).</summary>
    PdfA3b,

    /// <summary>PDF/A-3u (ISO 19005-3, all text mapped to Unicode).</summary>
    PdfA3u,

    /// <summary>PDF/A-3a (ISO 19005-3, accessible conformance; needs a tagged PDF).</summary>
    PdfA3a,

    /// <summary>PDF/A-4 (ISO 19005-4).</summary>
    PdfA4,

    /// <summary>PDF/A-4f (ISO 19005-4, with embedded files).</summary>
    PdfA4f,

    /// <summary>PDF/A-4e (ISO 19005-4, engineering documents).</summary>
    PdfA4e,

    /// <summary>PDF/UA-1 (ISO 14289-1, universal accessibility; needs a tagged PDF and a document title).</summary>
    PdfUa1,
}

internal static class TypstPdfStandards
{
    // The names of typst's --pdf-standard option, which the native layer parses.
    public static string Name(TypstPdfStandard standard)
    {
        return standard switch
        {
            TypstPdfStandard.Pdf14 => "1.4",
            TypstPdfStandard.Pdf15 => "1.5",
            TypstPdfStandard.Pdf16 => "1.6",
            TypstPdfStandard.Pdf17 => "1.7",
            TypstPdfStandard.Pdf20 => "2.0",
            TypstPdfStandard.PdfA1b => "a-1b",
            TypstPdfStandard.PdfA1a => "a-1a",
            TypstPdfStandard.PdfA2b => "a-2b",
            TypstPdfStandard.PdfA2u => "a-2u",
            TypstPdfStandard.PdfA2a => "a-2a",
            TypstPdfStandard.PdfA3b => "a-3b",
            TypstPdfStandard.PdfA3u => "a-3u",
            TypstPdfStandard.PdfA3a => "a-3a",
            TypstPdfStandard.PdfA4 => "a-4",
            TypstPdfStandard.PdfA4f => "a-4f",
            TypstPdfStandard.PdfA4e => "a-4e",
            TypstPdfStandard.PdfUa1 => "ua-1",
            _ => throw new ArgumentOutOfRangeException(nameof(standard), standard, "unknown pdf standard"),
        };
    }
}
