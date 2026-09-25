using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct TypstBuffer
{
    public nint Data;
    public nuint Length;
}
