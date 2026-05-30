using System.Text.RegularExpressions;

namespace OledPaintPro;

/// <summary>
/// Parsuje tablicę C/Arduino PROGMEM (MSB-first) do bool[,].
/// Obsługuje: hex 0xFF, binarny 0b10110011, dziesiętny 255.
/// Rozmiar: z #define *WIDTH* / *HEIGHT*, lub podany ręcznie (overrideW/H > 0).
/// </summary>
public static class BitmapParser
{
    public record ParseResult(bool[,] Pixels, int Width, int Height, string Name);

    public static ParseResult? TryParse(string code, out string error,
        int overrideW = 0, int overrideH = 0)
    {
        error = string.Empty;

        // ── 1. Nazwa zmiennej ──────────────────────────────────────────────
        var nameMatch = Regex.Match(code,
            @"\b(\w+)\s*\[\s*\]\s*(?:PROGMEM\s*)?=\s*\{", RegexOptions.IgnoreCase);
        string name = nameMatch.Success ? nameMatch.Groups[1].Value : "bitmap";
        if (name is "PROGMEM" or "char" or "unsigned" or "const" or "static")
            name = "bitmap";

        // ── 2. Rozmiar z #define (WIDTH / HEIGHT gdziekolwiek w nazwie) ───
        int defW = overrideW, defH = overrideH;
        if (defW <= 0)
        {
            var m = Regex.Match(code, @"#define\s+\w*WIDTH\w*\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) int.TryParse(m.Groups[1].Value, out defW);
        }
        if (defH <= 0)
        {
            var m = Regex.Match(code, @"#define\s+\w*HEIGHT\w*\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) int.TryParse(m.Groups[1].Value, out defH);
        }

        // ── 3. Wyciągnij ciało tablicy { ... } ────────────────────────────
        int braceStart = code.IndexOf('{');
        int braceEnd   = -1;
        if (braceStart >= 0)
        {
            int depth = 0;
            for (int i = braceStart; i < code.Length; i++)
            {
                if      (code[i] == '{') depth++;
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0) { braceEnd = i; break; }
                }
            }
        }
        string arrayBody = (braceStart >= 0 && braceEnd > braceStart)
            ? code.Substring(braceStart + 1, braceEnd - braceStart - 1)
            : code;

        // Usuń komentarze C/C++
        arrayBody = Regex.Replace(arrayBody, @"//[^\r\n]*", " ");
        arrayBody = Regex.Replace(arrayBody, @"/\*.*?\*/",  " ", RegexOptions.Singleline);

        // ── 4. Zbierz bajty ────────────────────────────────────────────────
        var bytes = new List<byte>(4096);
        foreach (Match m in Regex.Matches(arrayBody,
            @"0[bB][01]+|0[xX][0-9A-Fa-f]+|\b\d+\b"))
        {
            string v = m.Value;
            byte b;
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                try   { b = Convert.ToByte(v, 16); }
                catch { continue; }
            }
            else if (v.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                try   { b = Convert.ToByte(v[2..], 2); }
                catch { continue; }
            }
            else
            {
                if (!byte.TryParse(v, out b)) continue;
            }
            bytes.Add(b);
        }

        if (bytes.Count == 0)
        {
            error = "Nie znaleziono żadnych bajtów w kodzie.";
            return null;
        }

        // ── 5. Ustal rozmiar ──────────────────────────────────────────────
        int w, h;
        if (defW > 0 && defH > 0)
        {
            w = defW;
            h = defH;
        }
        else
        {
            (w, h) = GuessSize(bytes.Count);
            if (w == 0)
            {
                error = $"Nie można ustalić rozmiaru dla {bytes.Count} bajtów. " +
                         "Wpisz W i H ręcznie w polach poniżej.";
                return null;
            }
        }

        // ── 6. Walidacja ──────────────────────────────────────────────────
        int bpr    = (w + 7) / 8;
        int needed = bpr * h;
        if (bytes.Count < needed)
        {
            error = $"Za mało bajtów: potrzeba {needed}, znaleziono {bytes.Count} " +
                    $"(W={w}, H={h}, {bpr} B/wiersz).";
            return null;
        }

        // ── 7. Dekoduj MSB-first ──────────────────────────────────────────
        var pixels = new bool[h, w];
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            int byteIdx      = row * bpr + col / 8;
            int bit          = 7 - (col % 8);
            pixels[row, col] = (bytes[byteIdx] & (1 << bit)) != 0;
        }

        return new ParseResult(pixels, w, h, name);
    }

    // ── Parsowanie animacji (wiele tablic klatek) ─────────────────────────
    public record AnimationParseResult(List<bool[,]> Frames, int Width, int Height, string BaseName, int Fps);

    /// <summary>
    /// Wykrywa wiele tablic klatek w pliku .h wygenerowanym przez OLED Paint Pro
    /// (lub inne narzędzia). Zwraca null jeśli plik nie zawiera wielu klatek —
    /// wtedy należy użyć TryParse().
    /// </summary>
    public static AnimationParseResult? TryParseAnimation(string code, out string error,
        int overrideW = 0, int overrideH = 0)
    {
        error = string.Empty;

        // ── Rozmiar z #define ─────────────────────────────────────────────
        int defW = overrideW, defH = overrideH;
        if (defW <= 0)
        {
            var m = Regex.Match(code, @"#define\s+\w*WIDTH\w*\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) int.TryParse(m.Groups[1].Value, out defW);
        }
        if (defH <= 0)
        {
            var m = Regex.Match(code, @"#define\s+\w*HEIGHT\w*\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) int.TryParse(m.Groups[1].Value, out defH);
        }

        // ── FPS z #define ─────────────────────────────────────────────────
        int fps = 10;
        {
            var m = Regex.Match(code, @"#define\s+\w*FRAME_DELAY\w*\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int delayMs) && delayMs > 0)
                fps = Math.Clamp(1000 / delayMs, 1, 60);
        }

        // ── Nazwa bazowa ──────────────────────────────────────────────────
        string baseName = "anim";
        {
            var m = Regex.Match(code, @"#define\s+(\w+)_FRAME_COUNT", RegexOptions.IgnoreCase);
            if (m.Success)
                baseName = m.Groups[1].Value.ToLowerInvariant();
        }

        // ── Znajdź wszystkie tablice klatek ───────────────────────────────
        // Strategia A: szukaj wzorca NazwaBasowa_f001[], NazwaBasowa_f002[] ...
        // Strategia B: szukaj tablicy wskaźników *_frames[] PROGMEM
        // Strategia C: wszystkie tablice const unsigned char ... [] = { ... }

        // Znajdź wszystkie deklaracje tablic: const unsigned char [PROGMEM] Nazwa[] = { ... };
        var arrayMatches = Regex.Matches(code,
            @"const\s+unsigned\s+char\s+(?:PROGMEM\s+)?(\w+)\s*\[\s*\]\s*(?:PROGMEM\s*)?=\s*\{([^}]*(?:\}(?!\s*;)[^}]*)*)\}\s*;",
            RegexOptions.Singleline);

        if (arrayMatches.Count == 0)
        {
            error = "Nie znaleziono żadnych tablic klatek.";
            return null;
        }

        // Odfiltruj tablicę wskaźników (zawiera nazwy, nie bajty)
        var frameArrays = new List<(string name, string body)>();
        foreach (Match m in arrayMatches)
        {
            string aName = m.Groups[1].Value;
            string aBody = m.Groups[2].Value;
            // Pomiń tablicę wskaźników (_frames) — zawiera tylko identyfikatory
            if (aName.EndsWith("_frames", StringComparison.OrdinalIgnoreCase)) continue;
            // Pomiń jeśli ciało nie zawiera żadnych wartości hex/dec
            if (!Regex.IsMatch(aBody, @"0[xX][0-9A-Fa-f]+|\b\d{2,3}\b")) continue;
            frameArrays.Add((aName, aBody));
        }

        if (frameArrays.Count == 0)
        {
            error = "Nie znaleziono tablic z bajtami klatek.";
            return null;
        }

        // ── Parsuj bajty każdej klatki ────────────────────────────────────
        var frames = new List<bool[,]>();
        int fw = 0, fh = 0;

        foreach (var (aName, aBody) in frameArrays)
        {
            var bytes = new List<byte>(4096);
            foreach (Match bm in Regex.Matches(aBody,
                @"0[bB][01]+|0[xX][0-9A-Fa-f]+|\b\d+\b"))
            {
                string v = bm.Value;
                byte b;
                if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                { try { b = Convert.ToByte(v, 16); } catch { continue; } }
                else if (v.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                { try { b = Convert.ToByte(v[2..], 2); } catch { continue; } }
                else
                { if (!byte.TryParse(v, out b)) continue; }
                bytes.Add(b);
            }

            if (bytes.Count == 0) continue;

            // Ustal rozmiar (raz dla pierwszej klatki, dalej reużywaj)
            int w, h;
            if (fw > 0 && fh > 0)
            {
                w = fw; h = fh;
            }
            else if (defW > 0 && defH > 0)
            {
                w = defW; h = defH;
            }
            else
            {
                (w, h) = GuessSize(bytes.Count);
                if (w == 0)
                {
                    error = $"Nie można ustalić rozmiaru dla klatki '{aName}' ({bytes.Count} bajtów).";
                    return null;
                }
            }

            int bpr    = (w + 7) / 8;
            int needed = bpr * h;
            if (bytes.Count < needed) continue; // zbyt mała tablica, pomijamy

            var pixels = new bool[h, w];
            for (int row = 0; row < h; row++)
            for (int col = 0; col < w;  col++)
            {
                int byteIdx      = row * bpr + col / 8;
                int bit          = 7 - (col % 8);
                pixels[row, col] = (bytes[byteIdx] & (1 << bit)) != 0;
            }

            frames.Add(pixels);
            if (fw == 0) { fw = w; fh = h; }
        }

        if (frames.Count == 0)
        {
            error = "Nie udało się sparsować żadnej klatki.";
            return null;
        }

        // Jeśli tylko jedna klatka — nie traktuj jako animacji
        if (frames.Count == 1)
            return null;

        return new AnimationParseResult(frames, fw, fh, baseName, fps);
    }

    // ── Znane rozmiary OLED ───────────────────────────────────────────────
    static readonly (int w, int h)[] KnownSizes =
    {
        (128, 64), (128, 32), (128, 16),
        (64,  48), (64,  32), (64,  64),
        (96,  16), (84,  48), (32,  32),
        (16,  16), (8,    8),
    };

    static (int w, int h) GuessSize(int total)
    {
        foreach (var (w, h) in KnownSizes)
            if (((w + 7) / 8) * h == total) return (w, h);

        foreach (int h in new[] { 64, 48, 32, 16, 8 })
        {
            if (total % h == 0)
            {
                int bpr = total / h;
                int w   = bpr * 8;
                if (w is >= 8 and <= 256) return (w, h);
            }
        }
        return (0, 0);
    }
}
