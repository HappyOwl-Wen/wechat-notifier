using System.Globalization;
using System.Text;

namespace WeChatNotifier.Core.Notifications;

/// <summary>
/// Fits text into lines of a notification's body by an estimate of its width. Windows wraps long text and shows at
/// most <see cref="BodyLines"/> body lines in all: it drops whatever follows — without any mark, and ignoring
/// hint-maxLines (2026-10-02: a wrapped earlier message pushed the newest one out of the notification entirely).
/// Measured on Windows 11: body text is 14 effective px per CJK character, and 19 of them (266 epx) fit on a line
/// next to the avatar. Wrapping is estimated per character, as CJK text wraps; Latin words wrap whole, so an English
/// text may still need a line more than estimated. Pure logic.
/// </summary>
public static class ToastText
{
    /// <summary>Body lines Windows shows at most (all body texts together).</summary>
    public const int BodyLines = 4;

    /// <summary>Width of one body line in effective px: 266 measured, less a margin for the estimate.</summary>
    internal const double LineWidth = 256;

    private const double EllipsisWidth = 14;

    /// <summary>
    /// <paramref name="text"/> on one line (newlines become spaces), cut with "…" where it would wrap; never splits
    /// an emoji or other grapheme.
    /// </summary>
    /// <param name="textScale">Windows' text size (Settings → Accessibility → Text size), 1.0 = 100 %.</param>
    public static string FitOneLine(string text, double textScale = 1.0) => Fit(Flatten(text), 1, textScale);

    /// <summary>
    /// <paramref name="text"/> cut with "…" where it would need more than <paramref name="lines"/> lines; newlines
    /// start a new line. Never splits an emoji or other grapheme.
    /// </summary>
    public static string Fit(string text, int lines, double textScale = 1.0)
    {
        double budget = LineWidth / Math.Max(1.0, textScale);
        int line = 0;
        double width = 0;   // of the current line
        int fits = 0;       // length up to which a cut still leaves room for "…" on the current line
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (element[0] is '\n' or '\r')
            {
                if (++line >= lines) return Cut(text, fits);
                width = 0;
                fits = e.ElementIndex + element.Length;
                continue;
            }
            double w = Width(element);
            if (width + w > budget)
            {
                if (++line >= lines) return Cut(text, fits);
                width = 0;
                fits = e.ElementIndex;
            }
            width += w;
            if (width + EllipsisWidth <= budget) fits = e.ElementIndex + element.Length;
        }
        return text;
    }

    private static string Cut(string text, int length) => text[..length].TrimEnd() + "…";

    /// <summary>Estimated width in effective px of one grapheme in the body font (14 px Segoe UI / Microsoft YaHei UI).</summary>
    internal static double Width(string element)
    {
        int cp = char.IsSurrogatePair(element, 0) ? char.ConvertToUtf32(element, 0) : element[0];
        if (cp is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF) return 18; // emoji
        if (IsWide(cp)) return 14;
        return cp switch
        {
            ' ' or 'i' or 'j' or 'l' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' or '`' => 4,
            'f' or 't' or 'r' or '(' or ')' or '[' or ']' or '{' or '}' or '-' or '"' or '/' or '\\' => 5,
            'm' or 'w' => 11,
            'M' or 'W' => 13,
            >= 'A' and <= 'Z' => 9.5,
            >= '0' and <= '9' => 8,
            < 0x250 => 7.5,  // other Latin
            _ => 9,          // other scripts and symbols
        };
    }

    /// <summary>East Asian wide and fullwidth characters: CJK, kana, Hangul, fullwidth punctuation.</summary>
    private static bool IsWide(int cp) => cp is
        >= 0x1100 and <= 0x115F or   // Hangul Jamo
        >= 0x2E80 and <= 0x303E or   // CJK radicals, Kangxi, CJK symbols and punctuation
        >= 0x3041 and <= 0x33FF or   // kana, Bopomofo, Hangul compatibility, enclosed CJK
        >= 0x3400 and <= 0x4DBF or   // CJK extension A
        >= 0x4E00 and <= 0x9FFF or   // CJK unified ideographs
        >= 0xA000 and <= 0xA4CF or   // Yi
        >= 0xAC00 and <= 0xD7A3 or   // Hangul syllables
        >= 0xF900 and <= 0xFAFF or   // CJK compatibility ideographs
        >= 0xFE30 and <= 0xFE4F or   // CJK compatibility forms
        >= 0xFF00 and <= 0xFF60 or   // fullwidth forms
        >= 0xFFE0 and <= 0xFFE6 or
        >= 0x20000 and <= 0x3FFFD;   // CJK extensions B and later

    private static string Flatten(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            bool space = c is '\n' or '\r' or '\t' or ' ';
            if (space && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(space ? ' ' : c);
        }
        return sb.ToString().TrimEnd();
    }
}
