using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Envisia.Typst.Tests;

public class TypstFontCoverageTest
{
    private static readonly Lazy<TypstFont> RegularFont = new(LoadFont);

    private static TypstFontCoverage Coverage => TypstFontCoverage.Of([RegularFont.Value]);

    [Test]
    public void Reads_The_Characters_Of_The_Font()
    {
        Coverage.Covers(new Rune('ä')).ShouldBeTrue();
        Coverage.Covers(new Rune('€')).ShouldBeTrue();
        Coverage.Covers(new Rune('\u2013')).ShouldBeTrue();
        Coverage.Covers(new Rune('\u2192')).ShouldBeFalse();
        Coverage.Covers(new Rune(0x1F60A)).ShouldBeFalse();
    }

    [Test]
    public void Reads_Every_Face_Of_A_Collection()
    {
        var coverage = TypstFontCoverage.Of([new TypstFont(Collection(RegularFont.Value.Data.ToArray()))]);

        coverage.Covers(new Rune('ä')).ShouldBeTrue();
        coverage.Covers(new Rune('\u2192')).ShouldBeFalse();
    }

    [Test]
    public void Rejects_A_Font_Whose_Tables_Point_Outside_Its_Data()
    {
        var font = RegularFont.Value.Data.ToArray().AsSpan(0, 400).ToArray();

        Should
            .Throw<ArgumentException>(() => TypstFontCoverage.Of([new TypstFont(font)]))
            .Message.ShouldContain("font 0");
    }

    [Test]
    public void Keeps_Text_The_Font_Can_Show_As_It_Is()
    {
        const string Text = "Grüße <b> & \"x\" #1 $5 *y* \\pfad\r\nzweite Zeile – €";

        Coverage.Displayable(Text).ShouldBeSameAs(Text);
    }

    [TestCase("10\u202F000 Stück", "10 000 Stück")]
    [TestCase("E\u2010Mail und E\u2011Mail", "E-Mail und E-Mail")]
    [TestCase("A\u2192B", "A->B")]
    [TestCase("eins\u2028zwei", "eins\nzwei")]
    [TestCase("CO\u2082", "CO2")]
    [TestCase("a\tb", "a b")]
    [TestCase("a\u0007b", "ab")]
    [TestCase("Danke \U0001F60A\uF0E0!", "Danke !")]
    [TestCase("此致敬礼", "")]
    public void Replaces_Or_Drops_What_The_Font_Cannot_Show(string text, string expected)
    {
        Coverage.Displayable(text).ShouldBe(expected);
    }

    [Test]
    public void A_Pdf_A_Document_Compiles_Once_Its_Text_Is_Displayable()
    {
        const string Text = "Danke \U0001F60A \u2010 \uF0E0";

        Should
            .Throw<TypstCompileException>(() => TypstCompiler.CompilePdf(Request(Text)))
            .Diagnostics.ShouldContain("could not be displayed");
        Encoding.ASCII.GetString(TypstCompiler.CompilePdf(Request(Coverage.Displayable(Text))), 0, 5).ShouldBe("%PDF-");
    }

    private static TypstCompileRequest Request(string text)
    {
        return new TypstCompileRequest
        {
            Markup =
                "#set document(date: datetime.today())\n#set text(font: \"Open Sans\")\n#text(json(\"data.json\").text)",
            Fonts = [RegularFont.Value],
            Files = [new TypstFile("data.json", JsonSerializer.SerializeToUtf8Bytes(new { text }))],
            Today = new DateOnly(2024, 5, 17),
            Standards = [TypstPdfStandard.PdfA3b],
        };
    }

    // A TrueType collection holding the font twice. The font's table offsets are relative to the start of the file, so
    // the collection header goes in front of a copy whose offsets are shifted by the header's size.
    private static byte[] Collection(byte[] font)
    {
        const int Header = 20;
        var shifted = (byte[])font.Clone();
        var tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(shifted.AsSpan(4));
        for (var i = 0; i < tables; i++)
        {
            var offset = shifted.AsSpan(12 + 16 * i + 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                offset,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(offset) + Header
            );
        }

        var collection = new byte[Header + shifted.Length];
        "ttcf"u8.CopyTo(collection);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(4), 0x00010000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(8), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(12), Header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(16), Header);
        shifted.CopyTo(collection, Header);
        return collection;
    }

    private static TypstFont LoadFont()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Envisia.Typst.Tests.open-sans-regular.ttf");
        stream.ShouldNotBeNull();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new TypstFont(buffer.ToArray());
    }
}
