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
