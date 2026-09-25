using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Envisia.Typst;

/// <summary>
/// The characters a set of fonts can show, read from their <c>cmap</c> tables. Typst only has the fonts it is handed:
/// a character none of them has is drawn as the font's missing glyph box, and under a PDF/A standard Typst refuses
/// the whole document instead. Text from users can be passed through <see cref="Displayable"/> first, which keeps
/// what the fonts can show and substitutes or drops the rest.
/// </summary>
/// <remarks>Build it once per set of fonts and reuse it; it is immutable and safe to share between threads.</remarks>
public sealed class TypstFontCoverage
{
    private readonly HashSet<int> _codePoints;

    private TypstFontCoverage(HashSet<int> codePoints)
    {
        _codePoints = codePoints;
    }

    /// <summary>Reads the characters the fonts, typically the ones of a <see cref="TypstCompileRequest"/>, can show.</summary>
    /// <param name="fonts">TrueType or OpenType fonts, or collections of them. Every face of a collection counts.</param>
    /// <exception cref="ArgumentException">A font's tables point outside its data.</exception>
    public static TypstFontCoverage Of(IEnumerable<TypstFont> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        var codePoints = new HashSet<int>();
        var index = 0;
        foreach (var font in fonts)
        {
            ArgumentNullException.ThrowIfNull(font, nameof(fonts));
            try
            {
                foreach (var face in Faces(font.Data.Span))
                {
                    ReadCmap(font.Data.Span, face, codePoints);
                }
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new ArgumentException(
                    $"font {index} is not a readable TrueType or OpenType font",
                    nameof(fonts),
                    exception
                );
            }

            index++;
        }

        return new TypstFontCoverage(codePoints);
    }

    /// <summary>Whether at least one of the fonts has a glyph for <paramref name="rune"/>.</summary>
    public bool Covers(Rune rune)
    {
        return _codePoints.Contains(rune.Value);
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every character the fonts cannot show replaced by one they can: other
    /// spaces become a space, line and paragraph separators a line break, dashes that only differ in their break
    /// behaviour a hyphen-minus, and anything with a compatibility decomposition (subscript digits, full width forms)
    /// its decomposition, when the fonts can show all of it. What is left (emoji, symbol font code points, scripts the
    /// fonts lack) is dropped. Tabs become a space, line feeds and carriage returns are kept and other control
    /// characters are dropped.
    /// </summary>
    /// <returns><paramref name="text"/> itself when there is nothing to change.</returns>
    public string Displayable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var index = FirstUnsupported(text);
        if (index < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, index);
        foreach (var rune in text.AsSpan(index).EnumerateRunes())
        {
            Append(builder, rune);
        }

        return builder.ToString();
    }

    private int FirstUnsupported(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsSurrogate(c) || !IsKept(c))
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsKept(int codePoint)
    {
        return codePoint is '\n' or '\r' || (codePoint >= ' ' && _codePoints.Contains(codePoint));
    }

    private void Append(StringBuilder builder, Rune rune)
    {
        if (IsKept(rune.Value))
        {
            builder.Append(rune.ToString());
            return;
        }

        var substitute = rune.Value switch
        {
            '\t' => " ",
            0x2028 or 0x2029 => "\n",
            0x2010 or 0x2011 or 0x2012 or 0x2043 => "-",
            0x2190 => "<-",
            0x2192 => "->",
            0x21D2 => "=>",
            _ when Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator => " ",
            _ => rune.ToString().Normalize(NormalizationForm.FormKC),
        };

        foreach (var part in substitute.EnumerateRunes())
        {
            if (!IsKept(part.Value))
            {
                return;
            }
        }

        builder.Append(substitute);
    }

    private static List<int> Faces(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return [];
        }

        // "ttcf": a collection, its header lists the offset of every face's table directory.
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != 0x74746366)
        {
            return [0];
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var faces = new List<int>((int)Math.Min(count, (uint)(data.Length - 12) / 4));
        for (var i = 0; i < count && 16 + 4 * i <= data.Length; i++)
        {
            faces.Add((int)BinaryPrimitives.ReadUInt32BigEndian(data[(12 + 4 * i)..]));
        }

        return faces;
    }

    private static void ReadCmap(ReadOnlySpan<byte> data, int face, HashSet<int> codePoints)
    {
        var tables = BinaryPrimitives.ReadUInt16BigEndian(data[(face + 4)..]);
        for (var i = 0; i < tables; i++)
        {
            var record = data[(face + 12 + 16 * i)..];
            // "cmap"
            if (BinaryPrimitives.ReadUInt32BigEndian(record) != 0x636D6170)
            {
                continue;
            }

            var cmap = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var subtables = BinaryPrimitives.ReadUInt16BigEndian(data[(cmap + 2)..]);
            for (var j = 0; j < subtables; j++)
            {
                var entry = data[(cmap + 4 + 8 * j)..];
                var platform = BinaryPrimitives.ReadUInt16BigEndian(entry);
                var encoding = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
                // Unicode platform, or Windows with the BMP (1) or full (10) Unicode encoding.
                if (platform != 0 && !(platform == 3 && encoding is 1 or 10))
                {
                    continue;
                }

                var subtable = data[(cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]))..];
                switch (BinaryPrimitives.ReadUInt16BigEndian(subtable))
                {
                    case 4:
                        ReadFormat4(subtable, codePoints);
                        break;
                    case 12:
                        ReadFormat12(subtable, codePoints);
                        break;
                }
            }

            return;
        }
    }

    private static void ReadFormat4(ReadOnlySpan<byte> table, HashSet<int> codePoints)
    {
        var segments = BinaryPrimitives.ReadUInt16BigEndian(table[6..]) / 2;
        var ends = table[14..];
        var starts = table[(16 + 2 * segments)..];
        var deltas = table[(16 + 4 * segments)..];
        var rangeOffsets = table[(16 + 6 * segments)..];
        for (var s = 0; s < segments; s++)
        {
            var start = BinaryPrimitives.ReadUInt16BigEndian(starts[(2 * s)..]);
            var end = BinaryPrimitives.ReadUInt16BigEndian(ends[(2 * s)..]);
            var delta = BinaryPrimitives.ReadInt16BigEndian(deltas[(2 * s)..]);
            var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(rangeOffsets[(2 * s)..]);
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var at = 2 * s + rangeOffset + 2 * (c - start);
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(rangeOffsets[at..]);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    codePoints.Add(c);
                }
            }
        }
    }

    private static void ReadFormat12(ReadOnlySpan<byte> table, HashSet<int> codePoints)
    {
        var groups = BinaryPrimitives.ReadUInt32BigEndian(table[12..]);
        for (var g = 0; g < groups; g++)
        {
            var group = table[(16 + 12 * g)..];
            var start = BinaryPrimitives.ReadUInt32BigEndian(group);
            var end = BinaryPrimitives.ReadUInt32BigEndian(group[4..]);
            var glyph = BinaryPrimitives.ReadUInt32BigEndian(group[8..]);
            for (var c = start; c <= end && c <= 0x10FFFF; c++)
            {
                if (glyph + (c - start) != 0)
                {
                    codePoints.Add((int)c);
                }
            }
        }
    }
}
