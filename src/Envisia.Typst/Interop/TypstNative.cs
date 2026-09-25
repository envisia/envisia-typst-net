using System.Runtime.InteropServices;

namespace Envisia.Typst.Interop;

internal static unsafe partial class TypstNative
{
    private const string LibraryName = "envisia_typst";

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_compile_pdf")]
    internal static partial int CompilePdf(
        byte* markup,
        nuint markupLength,
        TypstBuffer* fonts,
        nuint fontCount,
        TypstNamedBuffer* files,
        nuint fileCount,
        int year,
        byte month,
        byte day,
        byte* creator,
        nuint creatorLength,
        byte creatorSet,
        byte* standards,
        nuint standardsLength,
        byte tagged,
        TypstNativeResult* result
    );

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_result_free")]
    internal static partial void ResultFree(TypstNativeResult* result);
}
