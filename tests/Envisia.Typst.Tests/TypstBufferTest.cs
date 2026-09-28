using System.Reflection;
using System.Text;

namespace Envisia.Typst.Tests;

public class TypstBufferTest
{
    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" fill=\"#ff0000\"/></svg>";

    private static readonly Lazy<byte[]> RegularFont = new(LoadFont);

    [Test]
    public void Holds_What_Is_Left_Of_A_Seekable_Stream()
    {
        var content = Content(300_000);
        using var stream = new MemoryStream(content);
        stream.Position = 1000;

        using var buffer = TypstBuffer.FromStream(stream);

        buffer.Length.ShouldBe(content.Length - 1000);
        buffer.Memory.Span.SequenceEqual(content.AsSpan(1000)).ShouldBeTrue();
    }

    [Test]
    public void Grows_While_It_Reads_A_Stream_Of_Unknown_Length()
    {
        var content = Content(1_000_000);

        using var buffer = TypstBuffer.FromStream(new TrickleStream(content, 7_000));

        buffer.Length.ShouldBe(content.Length);
        buffer.Memory.Span.SequenceEqual(content).ShouldBeTrue();
    }

    [Test]
    public async Task Reads_A_Stream_Asynchronously()
    {
        var content = Content(500_000);

        using var buffer = await TypstBuffer.FromStreamAsync(new TrickleStream(content, 64_000));

        buffer.Memory.Span.SequenceEqual(content).ShouldBeTrue();
    }

    [Test]
    public void Reads_An_Empty_Stream()
    {
        using var buffer = TypstBuffer.FromStream(new MemoryStream());

        buffer.Length.ShouldBe(0);
        buffer.Memory.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public async Task Does_Not_Read_When_Already_Cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var stream = new TrickleStream(Content(10), 1);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            TypstBuffer.FromStreamAsync(stream, cancellation.Token)
        );
        stream.Reads.ShouldBe(0);
    }

    [Test]
    public async Task Stops_Reading_When_Cancelled_Midway()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new TrickleStream(
            Content(100_000),
            1_000,
            reads =>
            {
                if (reads == 3)
                {
                    cancellation.Cancel();
                }
            }
        );

        await Should.ThrowAsync<OperationCanceledException>(() =>
            TypstBuffer.FromStreamAsync(stream, cancellation.Token)
        );
        stream.Reads.ShouldBe(3);
    }

    [Test]
    public void Shares_A_Font_And_A_File_Without_Changing_The_Pdf()
    {
        var svg = Encoding.UTF8.GetBytes(Svg);
        using var font = TypstBuffer.FromStream(new MemoryStream(RegularFont.Value));
        using var logo = TypstBuffer.FromStream(new MemoryStream(svg));

        var copied = TypstCompiler.CompilePdf(
            LogoRequest([new TypstFont(RegularFont.Value)], new TypstFile("logo.svg", svg))
        );
        var shared = TypstCompiler.CompilePdf(LogoRequest([new TypstFont(font)], new TypstFile("logo.svg", logo)));

        shared.AsSpan().SequenceEqual(copied).ShouldBeTrue();
    }

    [Test]
    public void Reads_A_File_From_A_Slice_Of_A_Buffer()
    {
        const string Json = "{\"name\":\"Mitte\"}";
        using var buffer = TypstBuffer.FromStream(new MemoryStream(Encoding.UTF8.GetBytes($"vorne{Json}hinten")));

        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup =
                    "#let d = json(\"data.json\")\n#assert.eq(d.name, \"Mitte\")\n#set text(font: \"Open Sans\")\n#d.name",
                Fonts = [new TypstFont(RegularFont.Value)],
                Files = [new TypstFile("data.json", buffer.Memory.Slice(5, Json.Length))],
            }
        );

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    [Test]
    public void Refuses_A_Buffer_That_Was_Disposed()
    {
        var buffer = TypstBuffer.FromStream(new MemoryStream(Encoding.UTF8.GetBytes(Svg)));
        var file = new TypstFile("logo.svg", buffer);
        buffer.Dispose();

        Should.Throw<ObjectDisposedException>(() =>
            TypstCompiler.CompilePdf(LogoRequest([new TypstFont(RegularFont.Value)], file))
        );
        Should.Throw<ObjectDisposedException>(() => _ = buffer.Memory);
    }

    [Test]
    public void Concurrent_Calls_Share_One_Buffer()
    {
        using var font = TypstBuffer.FromStream(new MemoryStream(RegularFont.Value));
        var requests = Enumerable
            .Range(1, 6)
            .Select(number => new TypstCompileRequest
            {
                Markup =
                    $"#set text(font: \"Open Sans\")\n#for i in range({number}) [Seite #(i + 1) #pagebreak(weak: true)]",
                Fonts = [new TypstFont(font)],
            })
            .ToArray();
        var expected = requests.Select(TypstCompiler.CompilePdf).ToArray();

        var actual = new byte[72][];
        Parallel.For(
            0,
            actual.Length,
            new ParallelOptions { MaxDegreeOfParallelism = 12 },
            i => actual[i] = TypstCompiler.CompilePdf(requests[i % requests.Length])
        );

        for (var i = 0; i < actual.Length; i++)
        {
            actual[i].AsSpan().SequenceEqual(expected[i % requests.Length]).ShouldBeTrue($"call {i}");
        }
    }

    [Test]
    public void Disposing_It_While_Other_Threads_Compile_Only_Fails_The_Calls_That_Start_Afterwards()
    {
        var font = TypstBuffer.FromStream(new MemoryStream(RegularFont.Value));
        var request = new TypstCompileRequest
        {
            Markup = "#set text(font: \"Open Sans\")\n#lorem(400)",
            Fonts = [new TypstFont(font)],
        };
        var expected = TypstCompiler.CompilePdf(request);

        var outcomes = new byte[]?[48];
        Parallel.For(
            0,
            outcomes.Length,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i =>
            {
                if (i == 12)
                {
                    font.Dispose();
                }

                try
                {
                    outcomes[i] = TypstCompiler.CompilePdf(request);
                }
                catch (ObjectDisposedException)
                {
                    outcomes[i] = null;
                }
            }
        );

        outcomes[12].ShouldBeNull();
        foreach (var pdf in outcomes.OfType<byte[]>())
        {
            pdf.AsSpan().SequenceEqual(expected).ShouldBeTrue();
        }
    }

    private static TypstCompileRequest LogoRequest(TypstFont[] fonts, TypstFile logo)
    {
        return new TypstCompileRequest
        {
            Markup = "#set text(font: \"Open Sans\")\n#image(\"logo.svg\", width: 50pt)\nLogo",
            Fonts = fonts,
            Files = [logo],
        };
    }

    private static byte[] Content(int length)
    {
        var content = new byte[length];
        new Random(length).NextBytes(content);
        return content;
    }

    private static byte[] LoadFont()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Envisia.Typst.Tests.open-sans-regular.ttf");
        stream.ShouldNotBeNull();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// A stream that cannot seek and hands out at most <paramref name="chunk"/> bytes per read, reporting the number of
    /// reads so far to <paramref name="afterRead"/>.
    /// </summary>
    private sealed class TrickleStream(byte[] content, int chunk, Action<int>? afterRead = null) : Stream
    {
        private int _position;

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, chunk), content.Length - _position);
            content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            afterRead?.Invoke(++Reads);
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer.Span);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
