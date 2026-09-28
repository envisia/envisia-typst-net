using System.Buffers;
using System.Runtime.InteropServices;
using Envisia.Typst.Interop;

namespace Envisia.Typst;

/// <summary>
/// File or font content held in memory the native library owns. It is filled once from a stream and from then on
/// handed to every compilation without being copied: a <see cref="TypstFile"/> or <see cref="TypstFont"/> over
/// <see cref="Memory"/> shares these bytes with Typst, where plain managed memory is copied on every call.
/// </summary>
/// <remarks>
/// A buffer never changes after it is created and can be used by any number of concurrent compilations. Disposing it
/// gives up this object's reference; the memory is freed once no compilation uses it any more, so a buffer can be
/// disposed while another thread still compiles with it. Compiling with a buffer that was already disposed throws
/// <see cref="ObjectDisposedException"/>. A buffer nobody disposes is released when the garbage collector collects it.
/// </remarks>
public sealed class TypstBuffer : IDisposable
{
    private const int DefaultCapacity = 64 * 1024;

    private readonly TypstBufferHandle _handle;
    private readonly SealedMemory _memory;
    private volatile bool _disposed;

    private unsafe TypstBuffer(TypstBufferHandle handle, byte* data, int length)
    {
        _handle = handle;
        _memory = new SealedMemory(this, data, length);
    }

    /// <summary>The number of bytes the buffer holds.</summary>
    public int Length => _memory.Length;

    /// <summary>The buffer's content, valid until the buffer is disposed.</summary>
    /// <remarks>
    /// A compilation holds a reference while it uses the memory; reading it yourself does not. Do not dispose the
    /// buffer while another thread still reads it, unless that thread pinned it: a pin also holds a reference.
    /// </remarks>
    public ReadOnlyMemory<byte> Memory => _memory.Memory;

    /// <summary>Reads <paramref name="stream"/> from its current position to its end into a new buffer.</summary>
    /// <remarks>
    /// A seekable stream is read straight into memory of its remaining length. Any other stream is read into memory
    /// that grows as needed and is trimmed to size at the end. The content never passes through a managed array.
    /// </remarks>
    /// <exception cref="OutOfMemoryException">The native library could not allocate the memory.</exception>
    public static TypstBuffer FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var builder = new Builder(InitialCapacity(stream));
        while (true)
        {
            var read = stream.Read(builder.Reserve().Span);
            if (read == 0)
            {
                return builder.Seal();
            }

            builder.Commit(read);
        }
    }

    /// <inheritdoc cref="FromStream"/>
    public static async Task<TypstBuffer> FromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var builder = new Builder(InitialCapacity(stream));
        while (true)
        {
            var read = await stream.ReadAsync(builder.Reserve(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Seal();
            }

            builder.Commit(read);
        }
    }

    /// <summary>Gives up this object's reference to the memory.</summary>
    public void Dispose()
    {
        _disposed = true;
        _handle.Dispose();
    }

    /// <summary>
    /// Finds the buffer <paramref name="memory"/> points into, so a compilation can share it instead of copying it.
    /// </summary>
    internal static unsafe bool TryGet(
        ReadOnlyMemory<byte> memory,
        out TypstBuffer buffer,
        out byte* data,
        out int length
    )
    {
        if (MemoryMarshal.TryGetMemoryManager(memory, out SealedMemory? manager, out var start, out length))
        {
            buffer = manager.Owner;
            data = manager.Data + start;
            return true;
        }

        buffer = null!;
        data = null;
        return false;
    }

    /// <summary>Keeps the memory alive for a compilation; every call needs a matching <see cref="Release"/>.</summary>
    internal nint AddReference()
    {
        // The handle only closes once the last compilation lets go of it, so it would still take references then.
        ObjectDisposedException.ThrowIf(_disposed, this);
        var added = false;
        _handle.DangerousAddRef(ref added);
        return _handle.DangerousGetHandle();
    }

    internal void Release()
    {
        _handle.DangerousRelease();
    }

    private static int InitialCapacity(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return DefaultCapacity;
        }

        // One byte more than what is left, so the read that finds the end of the stream needs no room of its own.
        var remaining = stream.Length - stream.Position;
        return remaining >= 0 && remaining < Array.MaxLength ? (int)remaining + 1 : DefaultCapacity;
    }

    private static void ThrowOnFailure(int status)
    {
        switch (status)
        {
            case TypstStatus.Ok:
                return;
            case TypstStatus.OutOfMemory:
                throw new OutOfMemoryException("the native typst library could not allocate the buffer");
            case TypstStatus.Panic:
                throw new InvalidOperationException("the native typst library panicked in a buffer call");
            default:
                throw new InvalidOperationException($"the native typst library rejected a buffer call ({status})");
        }
    }

    /// <summary>A buffer while it is being filled. Disposing it before it is sealed frees the memory.</summary>
    private sealed unsafe class Builder : IDisposable
    {
        private readonly SpareMemory _spare = new();
        private TypstBufferHandle? _handle;
        private int _length;

        public Builder(int capacity)
        {
            var handle = TypstNative.BufferNew((nuint)capacity);
            if (handle == 0)
            {
                throw new OutOfMemoryException("the native typst library could not allocate the buffer");
            }

            _handle = new TypstBufferHandle(handle);
        }

        /// <summary>The unused capacity after the committed bytes, grown when there is none left.</summary>
        public Memory<byte> Reserve()
        {
            byte* spare;
            nuint spareLength;
            ThrowOnFailure(TypstNative.BufferReserve(Handle, 1, &spare, &spareLength));
            _spare.Reset(spare, (int)Math.Min(spareLength, (nuint)(Array.MaxLength - _length)));
            if (_spare.Length == 0)
            {
                throw new InvalidOperationException($"a {nameof(TypstBuffer)} holds at most {Array.MaxLength} bytes");
            }

            return _spare.Memory;
        }

        public void Commit(int count)
        {
            ThrowOnFailure(TypstNative.BufferCommit(Handle, (nuint)count));
            _length += count;
        }

        public TypstBuffer Seal()
        {
            byte* data;
            nuint length;
            ThrowOnFailure(TypstNative.BufferSeal(Handle, &data, &length));
            var buffer = new TypstBuffer(_handle!, data, (int)length);
            _handle = null;
            return buffer;
        }

        public void Dispose()
        {
            _handle?.Dispose();
        }

        private nint Handle => _handle!.DangerousGetHandle();
    }

    /// <summary>
    /// The spare capacity a stream reads into. Native memory never moves, so pinning it only has to hand out the
    /// pointer; the builder keeps it alive.
    /// </summary>
    private sealed unsafe class SpareMemory : MemoryManager<byte>
    {
        private byte* _data;

        public int Length { get; private set; }

        public void Reset(byte* data, int length)
        {
            _data = data;
            Length = length;
        }

        public override Span<byte> GetSpan()
        {
            return new Span<byte>(_data, Length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)Length, nameof(elementIndex));
            return new MemoryHandle(_data + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    /// <summary>The sealed content. Pinning it holds a reference, so the memory outlives a pin even across dispose.</summary>
    private sealed unsafe class SealedMemory(TypstBuffer owner, byte* data, int length) : MemoryManager<byte>
    {
        public TypstBuffer Owner => owner;

        public byte* Data => data;

        public int Length => length;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            return new Span<byte>(data, length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)length, nameof(elementIndex));
            owner.AddReference();
            return new MemoryHandle(data + elementIndex, pinnable: this);
        }

        public override void Unpin()
        {
            owner.Release();
        }

        protected override void Dispose(bool disposing) { }
    }
}
