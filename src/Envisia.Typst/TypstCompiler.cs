using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Envisia.Typst.Interop;

namespace Envisia.Typst;

/// <summary>Entry point to the bundled Typst compiler.</summary>
public static unsafe class TypstCompiler
{
    private const uint ExpectedAbiVersion = 1;

    /// <summary>
    /// Compiles Typst markup into PDF bytes. Everything the document needs — fonts and referenced files — is passed
    /// in; the compiler touches neither the file system nor the network.
    /// </summary>
    /// <exception cref="TypstCompileException">The markup did not compile, or the inputs were rejected.</exception>
    public static byte[] CompilePdf(TypstCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Markup);
        if (request.Fonts.Count == 0)
        {
            throw new ArgumentException("at least one font is required", nameof(request));
        }

        var abi = TypstNative.AbiVersion();
        if (abi != ExpectedAbiVersion)
        {
            throw new TypstCompileException(
                $"the native typst library speaks ABI version {abi}, this assembly expects {ExpectedAbiVersion}"
            );
        }

        var markup = Encoding.UTF8.GetBytes(request.Markup);
        var names = new byte[request.Files.Count][];
        for (var i = 0; i < request.Files.Count; i++)
        {
            names[i] = Encoding.UTF8.GetBytes(request.Files[i].Name);
        }

        var handles = new List<MemoryHandle>(request.Fonts.Count + request.Files.Count);
        try
        {
            var fonts = new TypstBuffer[request.Fonts.Count];
            for (var i = 0; i < request.Fonts.Count; i++)
            {
                var handle = request.Fonts[i].Data.Pin();
                handles.Add(handle);
                fonts[i] = new TypstBuffer
                {
                    Data = (nint)handle.Pointer,
                    Length = (nuint)request.Fonts[i].Data.Length,
                };
            }

            var files = new TypstNamedBuffer[request.Files.Count];
            for (var i = 0; i < request.Files.Count; i++)
            {
                var handle = request.Files[i].Data.Pin();
                handles.Add(handle);
                files[i] = new TypstNamedBuffer
                {
                    Data = (nint)handle.Pointer,
                    DataLength = (nuint)request.Files[i].Data.Length,
                };
            }

            return Invoke(request, markup, fonts, files, names);
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    private static byte[] Invoke(
        TypstCompileRequest request,
        byte[] markup,
        TypstBuffer[] fonts,
        TypstNamedBuffer[] files,
        byte[][] names
    )
    {
        var nameHandles = new GCHandle[names.Length];
        try
        {
            fixed (byte* markupPtr = markup)
            {
                fixed (TypstBuffer* fontPtr = fonts)
                {
                    fixed (TypstNamedBuffer* filePtr = files)
                    {
                        for (var i = 0; i < names.Length; i++)
                        {
                            nameHandles[i] = GCHandle.Alloc(names[i], GCHandleType.Pinned);
                            filePtr[i].Name = nameHandles[i].AddrOfPinnedObject();
                            filePtr[i].NameLength = (nuint)names[i].Length;
                        }

                        TypstNativeResult result = default;
                        TypstNative.CompilePdf(
                            markupPtr,
                            (nuint)markup.Length,
                            fontPtr,
                            (nuint)fonts.Length,
                            filePtr,
                            (nuint)files.Length,
                            request.Today?.Year ?? 0,
                            (byte)(request.Today?.Month ?? 0),
                            (byte)(request.Today?.Day ?? 0),
                            &result
                        );

                        return ReadResult(&result);
                    }
                }
            }
        }
        finally
        {
            foreach (var handle in nameHandles)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }
    }

    private static byte[] ReadResult(TypstNativeResult* result)
    {
        try
        {
            var message =
                result->Message == 0 || result->MessageLength == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString((byte*)result->Message, (int)result->MessageLength);

            if (result->Status != TypstStatus.Ok)
            {
                throw new TypstCompileException(message);
            }

            if (result->Pdf == 0 || result->PdfLength == 0)
            {
                throw new TypstCompileException("typst reported success but produced no PDF bytes");
            }

            return new ReadOnlySpan<byte>((byte*)result->Pdf, (int)result->PdfLength).ToArray();
        }
        finally
        {
            TypstNative.ResultFree(result);
        }
    }
}
