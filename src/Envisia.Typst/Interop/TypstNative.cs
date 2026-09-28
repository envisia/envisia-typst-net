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
        TypstNativeBuffer* fonts,
        nuint fontCount,
        TypstNativeNamedBuffer* files,
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

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_buffer_new")]
    internal static partial nint BufferNew(nuint capacity);

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_buffer_reserve")]
    internal static partial int BufferReserve(nint buffer, nuint additional, byte** spare, nuint* spareLength);

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_buffer_commit")]
    internal static partial int BufferCommit(nint buffer, nuint count);

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_buffer_seal")]
    internal static partial int BufferSeal(nint buffer, byte** data, nuint* length);

    [LibraryImport(LibraryName, EntryPoint = "envisia_typst_buffer_release")]
    internal static partial void BufferRelease(nint buffer);
}
