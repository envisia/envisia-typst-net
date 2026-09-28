using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

/// <summary>One reference to a native buffer; releasing it frees the buffer once no compilation uses it any more.</summary>
internal sealed class TypstBufferHandle : SafeHandle
{
    public TypstBufferHandle(nint handle)
        : base(0, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        TypstNative.BufferRelease(handle);
        return true;
    }
}
