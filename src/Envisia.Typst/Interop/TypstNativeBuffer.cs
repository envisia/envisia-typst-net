using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct TypstNativeBuffer
{
    public nint Data;
    public nuint Length;
    public nint Owner;
}
