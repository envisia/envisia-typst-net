using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Envisia.Typst.Interop;

namespace Envisia.Typst;

/// <summary>Entry point to the bundled Typst compiler.</summary>
public static class TypstCompiler
{
    private const uint ExpectedAbiVersion = 4;

    /// <summary>
    /// Compiles Typst markup into PDF bytes. Everything the document needs — fonts and referenced files — is passed
    /// in; the compiler touches neither the file system nor the network.
    /// </summary>
    /// <exception cref="TypstCompileException">The markup did not compile, or the inputs were rejected.</exception>
    /// <exception cref="ObjectDisposedException">A font or file lies in a <see cref="TypstBuffer"/> that was disposed.</exception>
    public static byte[] CompilePdf(TypstCompileRequest request)
    {
        using var pdf = Compile(request);
        return pdf.GetSpan().ToArray();
    }

    /// <summary>
    /// Compiles Typst markup and writes the PDF to <paramref name="destination"/>, straight from the memory the native
    /// library produced it in. Nothing is written when the document does not compile.
    /// </summary>
    /// <inheritdoc cref="CompilePdf(TypstCompileRequest)" path="/exception"/>
    public static void CompilePdf(TypstCompileRequest request, Stream destination)
    {
        ValidateDestination(destination);
        using var pdf = Compile(request);
        destination.Write(pdf.GetSpan());
    }

    /// <summary>
    /// Compiles Typst markup and writes the PDF to <paramref name="destination"/>, straight from the memory the native
    /// library produced it in. Nothing is written when the document does not compile.
    /// </summary>
    /// <remarks>
    /// Only the write is asynchronous. Typst compiles on the calling thread before the method returns its task, as
    /// in <see cref="CompilePdf(TypstCompileRequest)"/>.
    /// </remarks>
    /// <inheritdoc cref="CompilePdf(TypstCompileRequest)" path="/exception"/>
    public static Task CompilePdfAsync(
        TypstCompileRequest request,
        Stream destination,
        CancellationToken cancellationToken = default
    )
    {
        ValidateDestination(destination);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        TypstPdf pdf;
        try
        {
            pdf = Compile(request);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }

        return WriteAsync(pdf, destination, cancellationToken);
    }

    private static async Task WriteAsync(TypstPdf pdf, Stream destination, CancellationToken cancellationToken)
    {
        using (pdf)
        {
            await destination.WriteAsync(pdf.Memory, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateDestination(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("the destination stream is not writable", nameof(destination));
        }
    }

    private static TypstPdf Compile(TypstCompileRequest request)
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
        var creator = Encoding.UTF8.GetBytes(request.Creator ?? string.Empty);
        var standards = Encoding.UTF8.GetBytes(string.Join(',', request.Standards.Select(TypstPdfStandards.Name)));
        var names = new byte[request.Files.Count][];
        for (var i = 0; i < request.Files.Count; i++)
        {
            names[i] = Encoding.UTF8.GetBytes(request.Files[i].Name);
        }

        var inputs = new Inputs(request.Fonts.Count + request.Files.Count);
        try
        {
            var fonts = new TypstNativeBuffer[request.Fonts.Count];
            for (var i = 0; i < request.Fonts.Count; i++)
            {
                fonts[i] = inputs.Add(request.Fonts[i].Data);
            }

            var files = new TypstNativeNamedBuffer[request.Files.Count];
            for (var i = 0; i < request.Files.Count; i++)
            {
                var data = inputs.Add(request.Files[i].Data);
                files[i] = new TypstNativeNamedBuffer
                {
                    Data = data.Data,
                    DataLength = data.Length,
                    Owner = data.Owner,
                };
            }

            return Invoke(request, markup, creator, standards, fonts, files, names);
        }
        finally
        {
            inputs.Dispose();
        }
    }

    private static unsafe TypstPdf Invoke(
        TypstCompileRequest request,
        byte[] markup,
        byte[] creator,
        byte[] standards,
        TypstNativeBuffer[] fonts,
        TypstNativeNamedBuffer[] files,
        byte[][] names
    )
    {
        var nameHandles = new GCHandle[names.Length];
        try
        {
            fixed (byte* markupPtr = markup)
            fixed (byte* creatorPtr = creator)
            fixed (byte* standardsPtr = standards)
            {
                fixed (TypstNativeBuffer* fontPtr = fonts)
                {
                    fixed (TypstNativeNamedBuffer* filePtr = files)
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
                            creatorPtr,
                            (nuint)creator.Length,
                            request.Creator is null ? (byte)0 : (byte)1,
                            standardsPtr,
                            (nuint)standards.Length,
                            request.Tagged ? (byte)1 : (byte)0,
                            &result
                        );

                        return TypstPdf.Take(result);
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

    /// <summary>
    /// Keeps the fonts and files readable while Typst runs: managed memory is pinned for the native side to copy, the
    /// memory of a <see cref="TypstBuffer"/> is shared and held by a reference.
    /// </summary>
    private readonly unsafe struct Inputs(int capacity) : IDisposable
    {
        private readonly List<MemoryHandle> _pins = new(capacity);
        private readonly List<TypstBuffer> _buffers = [];

        public TypstNativeBuffer Add(ReadOnlyMemory<byte> data)
        {
            if (TypstBuffer.TryGet(data, out var buffer, out var pointer, out var length))
            {
                var owner = buffer.AddReference();
                _buffers.Add(buffer);
                return new TypstNativeBuffer
                {
                    Data = (nint)pointer,
                    Length = (nuint)length,
                    Owner = owner,
                };
            }

            var pin = data.Pin();
            _pins.Add(pin);
            return new TypstNativeBuffer { Data = (nint)pin.Pointer, Length = (nuint)data.Length };
        }

        public void Dispose()
        {
            foreach (var pin in _pins)
            {
                pin.Dispose();
            }

            foreach (var buffer in _buffers)
            {
                buffer.Release();
            }
        }
    }
}
