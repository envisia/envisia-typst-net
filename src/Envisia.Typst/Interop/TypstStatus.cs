namespace Envisia.Typst.Interop;

internal static class TypstStatus
{
    public const int Ok = 0;
    public const int CompileError = 1;
    public const int InvalidInput = 2;
    public const int Panic = 3;
}
