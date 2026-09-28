using System.Buffers;
using System.Text;

namespace Envisia.Typst.Interop;

/// <summary>A compiled PDF in the memory the native library produced it in, freed on dispose.</summary>
internal sealed unsafe class TypstPdf : MemoryManager<byte>
{
    private TypstNativeResult _result;

    private TypstPdf(TypstNativeResult result)
    {
        _result = result;
    }

    /// <summary>Takes over a filled result: returns the PDF, or frees the result and throws its diagnostics.</summary>
    public static TypstPdf Take(TypstNativeResult result)
    {
        var pdf = new TypstPdf(result);
        if (result.Status == TypstStatus.Ok && result.Pdf != 0 && result.PdfLength != 0)
        {
            return pdf;
        }

        using (pdf)
        {
            if (result.Status != TypstStatus.Ok)
            {
                throw new TypstCompileException(
                    result.Message == 0 || result.MessageLength == 0
                        ? string.Empty
                        : Encoding.UTF8.GetString((byte*)result.Message, (int)result.MessageLength)
                );
            }

            throw new TypstCompileException("typst reported success but produced no PDF bytes");
        }
    }

    public override Span<byte> GetSpan()
    {
        return new Span<byte>((byte*)_result.Pdf, (int)_result.PdfLength);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (uint)elementIndex,
            (uint)_result.PdfLength,
            nameof(elementIndex)
        );
        return new MemoryHandle((byte*)_result.Pdf + elementIndex);
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        fixed (TypstNativeResult* result = &_result)
        {
            TypstNative.ResultFree(result);
        }
    }
}
