using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Envisia.Typst.Tests;

public class TypstCompilerTest
{
    private static readonly Lazy<byte[]> RegularFont = new(LoadFont);

    private static TypstFont[] Fonts => [new TypstFont(RegularFont.Value)];

    [Test]
    public void Compiles_Minimal_Document_To_Pdf()
    {
        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest { Markup = "#set text(font: \"Open Sans\")\n#text(\"Hallo Welt\")", Fonts = Fonts }
        );

        pdf.Length.ShouldBeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    [Test]
    public void Embeds_The_Supplied_Font()
    {
        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup = "#set text(font: \"Open Sans\")\n#text(\"Schriftprobe\")",
                Fonts = Fonts,
            }
        );

        Encoding.Latin1.GetString(pdf).ShouldContain("OpenSans");
    }

    [Test]
    public void Reports_A_Missing_Font_As_A_Compile_Warning_Free_Error()
    {
        var exception = Should.Throw<TypstCompileException>(() =>
            TypstCompiler.CompilePdf(new TypstCompileRequest { Markup = "#panic(\"kaputt\")", Fonts = Fonts })
        );

        exception.Diagnostics.ShouldContain("kaputt");
        exception.Message.ShouldContain("kaputt");
    }

    [Test]
    public void Surfaces_A_Syntax_Error_With_Position()
    {
        var exception = Should.Throw<TypstCompileException>(() =>
            TypstCompiler.CompilePdf(
                new TypstCompileRequest { Markup = "#let x =\n#unknown_function()", Fonts = Fonts }
            )
        );

        exception.Diagnostics.ShouldContain("main.typ");
        exception.Diagnostics.ShouldStartWith("error");
    }

    [Test]
    public void Rejects_A_Request_Without_Fonts()
    {
        Should.Throw<ArgumentException>(() =>
            TypstCompiler.CompilePdf(new TypstCompileRequest { Markup = "#text(\"x\")", Fonts = [] })
        );
    }

    [Test]
    public void Resolves_A_Supplied_File_Without_Touching_The_Disk()
    {
        const string Svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" fill=\"#ff0000\"/></svg>";

        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup = "#set text(font: \"Open Sans\")\n#image(\"logo.svg\", width: 50pt)",
                Fonts = Fonts,
                Files = [new TypstFile("logo.svg", Encoding.UTF8.GetBytes(Svg))],
            }
        );

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    [Test]
    public void Fails_When_A_Referenced_File_Was_Not_Supplied()
    {
        var exception = Should.Throw<TypstCompileException>(() =>
            TypstCompiler.CompilePdf(new TypstCompileRequest { Markup = "#image(\"missing.svg\")", Fonts = Fonts })
        );

        exception.Diagnostics.ShouldContain("missing.svg");
    }

    [Test]
    public void Today_Is_Only_Available_When_The_Caller_Supplies_It()
    {
        var withoutDate = Should.Throw<TypstCompileException>(() =>
            TypstCompiler.CompilePdf(new TypstCompileRequest { Markup = "#datetime.today().display()", Fonts = Fonts })
        );
        withoutDate.Diagnostics.ShouldNotBeEmpty();

        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup = "#set text(font: \"Open Sans\")\n#datetime.today().display()",
                Fonts = Fonts,
                Today = new DateOnly(2024, 5, 17),
            }
        );
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    [Test]
    public void Reads_A_Supplied_Json_File_And_Imports_A_Supplied_Document()
    {
        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup = "#import \"lib.typ\": greet\n#set text(font: \"Open Sans\")\n#greet(json(\"data.json\").name)",
                Fonts = Fonts,
                Files =
                [
                    new TypstFile("lib.typ", Encoding.UTF8.GetBytes("#let greet(name) = text(\"Hallo \" + name)")),
                    new TypstFile("data.json", Encoding.UTF8.GetBytes("{\"name\": \"Welt\"}")),
                ],
            }
        );

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    [TestCase("#panic(\"pwned\")")]
    [TestCase("\"] #panic(\"pwned\") [")]
    [TestCase("\\\") + panic(\"pwned\") + (\"")]
    [TestCase("*fett* _kursiv_ = Heading")]
    [TestCase("#read(\"/etc/passwd\")")]
    public void Hostile_Text_In_A_Data_File_Renders_As_Text_Instead_Of_Executing(string hostile)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new { text = hostile });

        var pdf = TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup = "#set text(font: \"Open Sans\")\n#text(json(\"data.json\").text)",
                Fonts = Fonts,
                Files = [new TypstFile("data.json", data)],
            }
        );

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    [Test]
    public void Names_Typst_As_The_Creator_By_Default()
    {
        var raw = Encoding.Latin1.GetString(CompileWithCreator(null));

        raw.ShouldContain("/Creator(Typst ");
        raw.ShouldContain("<xmp:CreatorTool>Typst ");
    }

    [Test]
    public void Writes_The_Supplied_Creator()
    {
        var raw = Encoding.UTF8.GetString(CompileWithCreator("Envisia Angebotsgröße"));

        raw.ShouldContain("<xmp:CreatorTool>Envisia Angebotsgröße</xmp:CreatorTool>");
        raw.ShouldContain("/Creator");
        raw.ShouldNotContain("Typst ");
    }

    [Test]
    public void Leaves_The_Creator_Out_When_It_Is_Empty()
    {
        var raw = Encoding.Latin1.GetString(CompileWithCreator(string.Empty));

        raw.ShouldNotContain("/Creator");
        raw.ShouldNotContain("CreatorTool");
    }

    [Test]
    public void Concurrent_Calls_Produce_The_Same_Pdf_As_A_Call_On_Its_Own()
    {
        var requests = Enumerable.Range(1, 8).Select(CreateIsolatedRequest).ToArray();
        var expected = requests.Select(TypstCompiler.CompilePdf).ToArray();
        for (var i = 0; i < expected.Length; i++)
        {
            Encoding.Latin1.GetString(expected[i]).ShouldContain($"<xmp:CreatorTool>Aufruf {i + 1}</xmp:CreatorTool>");
        }

        var actual = new byte[96][];
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

    // Every request uses the same file name with different content, a different date and a different creator, so a
    // call that saw another call's inputs would produce different bytes.
    private static TypstCompileRequest CreateIsolatedRequest(int number)
    {
        return new TypstCompileRequest
        {
            Markup =
                "#let d = json(\"data.json\")\n#set document(date: datetime.today())\n#set text(font: \"Open Sans\")\n"
                + "#for i in range(d.pages) [Seite #(i + 1) von #d.name #pagebreak(weak: true)]",
            Fonts = Fonts,
            Files = [new TypstFile("data.json", JsonSerializer.SerializeToUtf8Bytes(new { name = $"Dokument {number}", pages = number }))],
            Today = new DateOnly(2024, 1, number),
            Creator = $"Aufruf {number}",
        };
    }

    private static byte[] CompileWithCreator(string? creator)
    {
        return TypstCompiler.CompilePdf(
            new TypstCompileRequest
            {
                Markup = "#set text(font: \"Open Sans\")\n#text(\"Hallo\")",
                Fonts = Fonts,
                Creator = creator,
            }
        );
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
}
