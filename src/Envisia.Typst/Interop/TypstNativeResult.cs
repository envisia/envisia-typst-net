using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct TypstNativeResult
{
    public int Status;
    public nint Pdf;
    public nuint PdfLength;
    public nint Message;
    public nuint MessageLength;
    public nint Handle;
}
