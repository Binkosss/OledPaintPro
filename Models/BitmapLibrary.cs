using System.IO;
using System.Windows.Media.Imaging;

namespace OledPaintPro.Models;

/// <summary>
/// Biblioteka predefiniowanych bitmap z folderu img/ (png)
/// + bitmapy użytkownika zapisywane w exe\bitmaps\.
/// Każdy PNG jest wczytywany i progowany (jasność > 128 → piksel świeci).
/// </summary>
public class BitmapLibrary
{
    public static readonly BitmapLibrary Instance = new();
    private BitmapLibrary() { }

    static string UserDir  => Path.Combine(AppContext.BaseDirectory, "bitmaps");
    // Folder img/ — szukamy relatywnie względem exe, potem w katalogu projektu
    static string ImgDir
    {
        get
        {
            // 1. obok .exe (po opublikowaniu)
            var exe = Path.Combine(AppContext.BaseDirectory, "img");
            if (Directory.Exists(exe)) return exe;
            // 2. katalog projektu (dev run)
            var dev = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "img");
            return Path.GetFullPath(dev);
        }
    }

    const string EXT = ".eyb";

    public List<BitmapGroup>  Groups    { get; } = new();
    public List<PixelTemplate>  Templates { get; } = new();
    public event Action? Changed;

    public void Load()
    {
        Groups.Clear();
        Templates.Clear();

        // ── Wbudowane z img/ ────────────────────────────────────────────────
        if (Directory.Exists(ImgDir))
        {
            foreach (var subDir in Directory.GetDirectories(ImgDir).OrderBy(d => d))
            {
                string groupName = Path.GetFileName(subDir);
                var group = new BitmapGroup(groupName);

                foreach (var file in Directory.GetFiles(subDir, "*.png").OrderBy(f => f))
                {
                    try
                    {
                        var tmpl = LoadFromPng(file);
                        tmpl.Id = "builtin_" + groupName + "_" + Path.GetFileNameWithoutExtension(file);
                        group.Items.Add(tmpl);
                        Templates.Add(tmpl);
                    }
                    catch { /* pomiń uszkodzone */ }
                }

                if (group.Items.Count > 0) Groups.Add(group);
            }
        }

        // ── Własne użytkownika ──────────────────────────────────────────────
        Directory.CreateDirectory(UserDir);
        var userGroup = new BitmapGroup("Własne");
        foreach (var file in Directory.GetFiles(UserDir, $"*{EXT}").OrderBy(f => f))
        {
            try
            {
                var tmpl = PixelTemplate.LoadFrom(file);
                userGroup.Items.Add(tmpl);
                Templates.Add(tmpl);
            }
            catch { }
        }
        if (userGroup.Items.Count > 0) Groups.Add(userGroup);

        Changed?.Invoke();
    }

    /// <summary>
    /// Wczytuje PNG jako bitmapę 1-bit: biały piksel (lub jasny) → true, czarny → false.
    /// Obsługuje PNG z przezroczystością: nieprzezroczysty i jasny → true.
    /// Nazwa pochodzi z nazwy pliku (bez rozszerzenia i przyrostka _WxH).
    /// </summary>
    public static PixelTemplate LoadFromPng(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource    = new Uri(path, UriKind.Absolute);
        bmp.CacheOption  = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        // Konwertuj do Bgra32 żeby mieć pewność co do formatu bajtów
        var conv = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        int w = conv.PixelWidth;
        int h = conv.PixelHeight;
        int stride = w * 4;
        byte[] raw = new byte[h * stride];
        conv.CopyPixels(raw, stride, 0);

        // Wykryj czy PNG ma przezroczyste tło (jakikolwiek piksel z alpha < 64).
        // Ikony z przezroczystym tłem (np. gaai — czarne kształty na alpha=0):
        //   każdy nieprzezroczysty piksel = świeci.
        // Ikony bez przezroczystości (np. flipper — białe kształty na czarnym tle):
        //   jasny piksel (brightness > 64) = świeci.
        bool hasTransparency = false;
        for (int i = 3; i < raw.Length; i += 4)
            if (raw[i] < 64) { hasTransparency = true; break; }

        var pixels = new bool[h, w];
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            int i = row * stride + col * 4;
            byte b = raw[i];
            byte g = raw[i + 1];
            byte r = raw[i + 2];
            byte a = raw[i + 3];
            if (hasTransparency)
                pixels[row, col] = a > 64;
            else
            {
                int brightness = (r * 299 + g * 587 + b * 114) / 1000;
                pixels[row, col] = (a > 64) && (brightness > 64);
            }
        }

        // Nazwa: usuń przyrostek _WxH z końca nazwy pliku
        string rawName = Path.GetFileNameWithoutExtension(path);
        string name = StripSizeSuffix(rawName);

        return new PixelTemplate { Name = name, Width = w, Height = h, Pixels = pixels };
    }

    static string StripSizeSuffix(string name)
    {
        // Wzorzec: _NxM na końcu np. "arrow_right_8x16" → "arrow right"
        int last = name.LastIndexOf('_');
        if (last > 0)
        {
            string suffix = name[(last + 1)..];
            if (System.Text.RegularExpressions.Regex.IsMatch(suffix, @"^\d+x\d+$"))
                name = name[..last];
        }
        return name.Replace('_', ' ');
    }

    public bool IsBuiltIn(PixelTemplate t) => t.Id.StartsWith("builtin_");

    // ── Ograniczenia importu PNG ─────────────────────────────────────────────
    public const long   MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    public const int    MaxSourceDim     = 512;              // maks. wejściowa rozdzielczość
    public const int    MaxTargetDim     = 64;               // maks. rozmiar wynikowej bitmapy

    /// <summary>
    /// Importuje PNG z dysku, skaluje do <paramref name="maxTargetDim"/> i zwraca PixelTemplate.
    /// Rzuca wyjątek z czytelnym komunikatem jeśli plik nie spełnia ograniczeń.
    /// </summary>
    public static PixelTemplate ImportFromPng(string path, string name, int maxTargetDim = MaxTargetDim)
    {
        // 1. Rozmiar pliku
        long fileSize = new FileInfo(path).Length;
        if (fileSize > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"Plik jest za duży ({fileSize / 1024} KB). Maksymalny rozmiar to {MaxFileSizeBytes / 1024} KB.");

        // 2. Wczytaj nagłówek żeby sprawdzić rozdzielczość
        var bmpInfo = new BitmapImage();
        bmpInfo.BeginInit();
        bmpInfo.UriSource   = new Uri(path, UriKind.Absolute);
        bmpInfo.CacheOption = BitmapCacheOption.OnLoad;
        bmpInfo.EndInit();
        bmpInfo.Freeze();

        if (bmpInfo.PixelWidth > MaxSourceDim || bmpInfo.PixelHeight > MaxSourceDim)
            throw new InvalidOperationException(
                $"Obraz jest za duży ({bmpInfo.PixelWidth}×{bmpInfo.PixelHeight} px). " +
                $"Maksymalna rozdzielczość to {MaxSourceDim}×{MaxSourceDim} px.");

        // 3. Oblicz docelową rozdzielczość zachowując proporcje
        int srcW = bmpInfo.PixelWidth;
        int srcH = bmpInfo.PixelHeight;
        double scale = Math.Min((double)maxTargetDim / srcW, (double)maxTargetDim / srcH);
        int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
        int dstH = Math.Max(1, (int)Math.Round(srcH * scale));

        // 4. Przeskaluj do docelowego rozmiaru (NearestNeighbor — pixel art)
        System.Windows.Media.ImageSource source = bmpInfo;
        if (dstW != srcW || dstH != srcH)
        {
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                bmpInfo,
                new System.Windows.Media.ScaleTransform(
                    (double)dstW / srcW,
                    (double)dstH / srcH));
            scaled.Freeze();
            source = scaled;
        }

        // 5. Konwertuj do Bgra32 i proguj
        var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            (System.Windows.Media.Imaging.BitmapSource)source,
            System.Windows.Media.PixelFormats.Bgra32, null, 0);

        int stride = dstW * 4;
        byte[] raw = new byte[dstH * stride];
        conv.CopyPixels(raw, stride, 0);

        bool hasTransparency = false;
        for (int i = 3; i < raw.Length; i += 4)
            if (raw[i] < 64) { hasTransparency = true; break; }

        var pixels = new bool[dstH, dstW];
        for (int row = 0; row < dstH; row++)
        for (int col = 0; col < dstW; col++)
        {
            int idx = row * stride + col * 4;
            byte b = raw[idx];
            byte g = raw[idx + 1];
            byte r = raw[idx + 2];
            byte a = raw[idx + 3];
            if (hasTransparency)
                pixels[row, col] = a > 64;
            else
            {
                int brightness = (r * 299 + g * 587 + b * 114) / 1000;
                pixels[row, col] = (a > 64) && (brightness > 64);
            }
        }

        string safeName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(path).Replace('_', ' ')
            : name.Trim();

        return new PixelTemplate
        {
            Name   = safeName,
            Width  = dstW,
            Height = dstH,
            Pixels = pixels
        };
    }

    public void Save(PixelTemplate t)
    {
        Directory.CreateDirectory(UserDir);
        string path = Path.Combine(UserDir, t.Id + EXT);
        t.SaveTo(path);
        int idx = Templates.FindIndex(x => x.Id == t.Id);
        if (idx >= 0) Templates[idx] = t;
        else
        {
            Templates.Add(t);
            // Dodaj do grupy Własne
            var ug = Groups.FirstOrDefault(g => g.Name == "Własne");
            if (ug == null) { ug = new BitmapGroup("Własne"); Groups.Add(ug); }
            ug.Items.Add(t);
        }
        Changed?.Invoke();
    }

    public void Delete(PixelTemplate t)
    {
        if (IsBuiltIn(t)) return;
        string path = Path.Combine(UserDir, t.Id + EXT);
        if (File.Exists(path)) File.Delete(path);
        Templates.RemoveAll(x => x.Id == t.Id);
        foreach (var g in Groups) g.Items.RemoveAll(x => x.Id == t.Id);
        Changed?.Invoke();
    }
}

/// <summary>Grupa bitmap (np. "gaai", "flipper", "Własne").</summary>
public class BitmapGroup(string name)
{
    public string             Name  { get; } = name;
    public List<PixelTemplate> Items { get; } = new();
}
