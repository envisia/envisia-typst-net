using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct TypstNamedBuffer
{
    public nint Name;
    public nuint NameLength;
    public nint Data;
    public nuint DataLength;
}
