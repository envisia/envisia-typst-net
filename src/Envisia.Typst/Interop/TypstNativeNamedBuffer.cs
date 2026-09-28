using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct TypstNativeNamedBuffer
{
    public nint Name;
    public nuint NameLength;
    public nint Data;
    public nuint DataLength;
    public nint Owner;
}
