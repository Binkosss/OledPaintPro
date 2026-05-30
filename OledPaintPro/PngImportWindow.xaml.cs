using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OledPaintPro.Models;

namespace OledPaintPro;

public partial class PngImportWindow : Window
{
    // ── Wejście ──────────────────────────────────────────────────────────────
    private readonly string   _sourcePath;
    private readonly BitmapSource _sourceBitmap;   // oryginalny PNG (pełna rozdzielczość)
    private readonly int      _origW, _origH;

    // ── Wynik ────────────────────────────────────────────────────────────────
    /// <summary>Gotowy szablon po zatwierdzeniu — null gdy anulowano.</summary>
    public PixelTemplate? Result { get; private set; }

    // ── Stan UI ──────────────────────────────────────────────────────────────
    private bool _ready = false;
    private bool _updatingSize = false;

    // ── DPI ──────────────────────────────────────────────────────────────────
    private double _dpiScaleX = 1.0, _dpiScaleY = 1.0;

    // ── Ciemny pasek tytułu ──────────────────────────────────────────────────
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    // ═════════════════════════════════════════════════════════════════════════

    public PngImportWindow(string pngPath)
    {
        InitializeComponent();

        _sourcePath = pngPath;

        // Wczytaj PNG
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource      = new Uri(pngPath, UriKind.Absolute);
        bi.CacheOption    = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        _sourceBitmap = bi;

        _origW = _sourceBitmap.PixelWidth;
        _origH = _sourceBitmap.PixelHeight;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int col = 0x2A2A2A;
            DwmSetWindowAttribute(hwnd, 35, ref col, sizeof(int));

            var ps = PresentationSource.FromVisual(this);
            _dpiScaleX = ps?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            _dpiScaleY = ps?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = false;

        // Ustaw domyślne wartości
        NameBox.Text    = Path.GetFileNameWithoutExtension(_sourcePath);
        OriginalSizeLabel.Text = $"Oryginał: {_origW}×{_origH}";

        // Rozmiar docelowy — ogranicz do 512
        int tw = Math.Min(_origW, 512);
        int th = Math.Min(_origH, 512);
        WidthBox.Text  = tw.ToString();
        HeightBox.Text = th.ToString();

        // Podgląd oryginalnego PNG
        OriginalImage.Source = _sourceBitmap;
        OriginalImage.Width  = tw;
        OriginalImage.Height = th;
        CheckerBg.Width  = tw;
        CheckerBg.Height = th;

        _ready = true;
        UpdatePreview();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ZDARZENIA UI
    // ═════════════════════════════════════════════════════════════════════════

    private void Settings_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_ready) return;
        ThresholdLabel.Text = ((int)ThresholdSlider.Value).ToString();
        UpdatePreview();
    }

    private void SizeBox_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ready || _updatingSize) return;
        if (!int.TryParse(WidthBox.Text,  out int w) || w < 1) return;
        if (!int.TryParse(HeightBox.Text, out int h) || h < 1) return;

        w = Math.Clamp(w, 1, 512);
        h = Math.Clamp(h, 1, 512);

        if (LockAspect.IsChecked == true && sender == WidthBox && _origW > 0)
        {
            _updatingSize = true;
            h = Math.Max(1, (int)Math.Round(w * (double)_origH / _origW));
            h = Math.Clamp(h, 1, 512);
            HeightBox.Text = h.ToString();
            _updatingSize = false;
        }
        else if (LockAspect.IsChecked == true && sender == HeightBox && _origH > 0)
        {
            _updatingSize = true;
            w = Math.Max(1, (int)Math.Round(h * (double)_origW / _origH));
            w = Math.Clamp(w, 1, 512);
            WidthBox.Text = w.ToString();
            _updatingSize = false;
        }

        UpdatePreview();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var parts = btn.Tag?.ToString()?.Split(',');
        if (parts?.Length != 2) return;
        if (!int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) return;

        _ready = false;
        WidthBox.Text  = w.ToString();
        HeightBox.Text = h.ToString();
        _ready = true;
        UpdatePreview();
    }

    private void PreviewZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        int z = (int)e.NewValue;
        PreviewZoomLabel.Text = $"{z}×";
        ApplyZoomToPreview(z);
    }

    private void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        OriginalImage.Opacity = OverlayCheck.IsChecked == true ? 0.25 : 0.0;
    }

    private void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        var tmpl = BuildTemplate();
        if (tmpl == null) return;
        Result = tmpl;
        Result.ResultAction = PngImportAction.OpenEditor;
        DialogResult = true;
        Close();
    }

    private void SaveLibrary_Click(object sender, RoutedEventArgs e)
    {
        var tmpl = BuildTemplate();
        if (tmpl == null) return;
        Result = tmpl;
        Result.ResultAction = PngImportAction.SaveLibrary;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  RENDEROWANIE PODGLĄDU
    // ═════════════════════════════════════════════════════════════════════════

    private void UpdatePreview()
    {
        if (!_ready) return;
        if (!int.TryParse(WidthBox.Text,  out int tw) || tw < 1) return;
        if (!int.TryParse(HeightBox.Text, out int th) || th < 1) return;
        tw = Math.Clamp(tw, 1, 512);
        th = Math.Clamp(th, 1, 512);

        ThresholdLabel.Text = ((int)ThresholdSlider.Value).ToString();

        // Skaluj PNG do rozmiaru docelowego
        var scaled = ScaleBitmap(_sourceBitmap, tw, th);

        // Konwertuj do bool[,]
        var pixels = ConvertToPixels(scaled, tw, th,
            (int)ThresholdSlider.Value,
            GetAlgorithm(),
            InvertCheck.IsChecked == true);

        // Renderuj podgląd jako WriteableBitmap
        var preview = RenderPreview(pixels, tw, th);

        int z = (int)PreviewZoomSlider.Value;
        PreviewImage.Source = preview;
        PreviewImage.Width  = tw * z;
        PreviewImage.Height = th * z;
        OriginalImage.Width  = tw * z;
        OriginalImage.Height = th * z;
        CheckerBg.Width  = tw * z;
        CheckerBg.Height = th * z;

        PreviewSizeLabel.Text = $"{tw} × {th} px";
        StatusLabel.Text = $"{tw}×{th} px · {tw * th / 8} B";
    }

    private void ApplyZoomToPreview(int zoom)
    {
        if (PreviewImage.Source is not WriteableBitmap bmp) return;
        int tw = bmp.PixelWidth, th = bmp.PixelHeight;
        PreviewImage.Width   = tw * zoom;
        PreviewImage.Height  = th * zoom;
        OriginalImage.Width  = tw * zoom;
        OriginalImage.Height = th * zoom;
        CheckerBg.Width  = tw * zoom;
        CheckerBg.Height = th * zoom;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ALGORYTMY
    // ═════════════════════════════════════════════════════════════════════════

    private enum Algorithm { Threshold, FloydSteinberg, Bayer, Atkinson }

    private Algorithm GetAlgorithm()
    {
        if (AlgoFloyd.IsChecked    == true) return Algorithm.FloydSteinberg;
        if (AlgoBayer.IsChecked    == true) return Algorithm.Bayer;
        if (AlgoAtkinson.IsChecked == true) return Algorithm.Atkinson;
        return Algorithm.Threshold;
    }

    private BitmapSource ScaleBitmap(BitmapSource src, int tw, int th)
    {
        if (src.PixelWidth == tw && src.PixelHeight == th) return src;

        BitmapScalingMode mode = BitmapScalingMode.NearestNeighbor;
        if      (ScaleBilinear.IsChecked == true) mode = BitmapScalingMode.Linear;
        else if (ScaleBicubic.IsChecked  == true) mode = BitmapScalingMode.HighQuality;

        var tb = new TransformedBitmap();
        tb.BeginInit();
        tb.Source    = src;
        tb.Transform = new ScaleTransform((double)tw / src.PixelWidth, (double)th / src.PixelHeight);
        tb.EndInit();
        tb.Freeze();

        // Użyj RenderTargetBitmap aby zastosować BitmapScalingMode
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(dv, mode);
            dc.DrawImage(src, new Rect(0, 0, tw, th));
        }
        var rtb = new RenderTargetBitmap(tw, th, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static bool[,] ConvertToPixels(BitmapSource bmp, int w, int h, int threshold, Algorithm algo, bool invert)
    {
        // Pobierz piksele jako BGRA
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = w * 4;
        byte[] raw = new byte[h * stride];
        converted.CopyPixels(raw, stride, 0);

        // Zbuduj tablicę szarości
        float[,] gray = new float[h, w];
        for (int r = 0; r < h; r++)
        for (int c = 0; c < w; c++)
        {
            int i = r * stride + c * 4;
            float b = raw[i] / 255f, g = raw[i+1] / 255f, re = raw[i+2] / 255f, a = raw[i+3] / 255f;
            // Blend z czarnym tłem (pre-multiply alpha)
            gray[r, c] = (0.299f * re + 0.587f * g + 0.114f * b) * a;
        }

        float t = threshold / 255f;
        var pixels = new bool[h, w];

        switch (algo)
        {
            case Algorithm.Threshold:
                for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    pixels[r, c] = invert ? gray[r, c] < t : gray[r, c] >= t;
                break;

            case Algorithm.FloydSteinberg:
            {
                var err = (float[,])gray.Clone();
                for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    float v   = Math.Clamp(err[r, c], 0f, 1f);
                    bool  on  = invert ? v < t : v >= t;
                    pixels[r, c] = on;
                    float e = v - (on ^ invert ? 1f : 0f);
                    if (c + 1 < w)               err[r,     c + 1] += e * 7 / 16f;
                    if (r + 1 < h && c > 0)      err[r + 1, c - 1] += e * 3 / 16f;
                    if (r + 1 < h)               err[r + 1, c    ] += e * 5 / 16f;
                    if (r + 1 < h && c + 1 < w)  err[r + 1, c + 1] += e * 1 / 16f;
                }
                break;
            }

            case Algorithm.Bayer:
            {
                float[,] bayer = {
                    {  0/16f,  8/16f,  2/16f, 10/16f },
                    { 12/16f,  4/16f, 14/16f,  6/16f },
                    {  3/16f, 11/16f,  1/16f,  9/16f },
                    { 15/16f,  7/16f, 13/16f,  5/16f },
                };
                for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    float v  = gray[r, c] + (bayer[r % 4, c % 4] - 0.5f) * 0.5f;
                    pixels[r, c] = invert ? v < t : v >= t;
                }
                break;
            }

            case Algorithm.Atkinson:
            {
                var err = (float[,])gray.Clone();
                for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    float v  = Math.Clamp(err[r, c], 0f, 1f);
                    bool  on = invert ? v < t : v >= t;
                    pixels[r, c] = on;
                    float e = (v - (on ^ invert ? 1f : 0f)) / 8f;
                    if (c + 1 < w)               err[r,     c + 1] += e;
                    if (c + 2 < w)               err[r,     c + 2] += e;
                    if (r + 1 < h && c > 0)      err[r + 1, c - 1] += e;
                    if (r + 1 < h)               err[r + 1, c    ] += e;
                    if (r + 1 < h && c + 1 < w)  err[r + 1, c + 1] += e;
                    if (r + 2 < h)               err[r + 2, c    ] += e;
                }
                break;
            }
        }

        return pixels;
    }

    private static WriteableBitmap RenderPreview(bool[,] pixels, int w, int h)
    {
        const uint COL_ON  = 0xFF_F2_F2_FF;
        const uint COL_OFF = 0xFF_02_02_10;

        var buf = new uint[w * h];
        for (int r = 0; r < h; r++)
        for (int c = 0; c < w; c++)
            buf[r * w + c] = pixels[r, c] ? COL_ON : COL_OFF;

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), buf, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BUDOWANIE SZABLONU
    // ═════════════════════════════════════════════════════════════════════════

    private PixelTemplate? BuildTemplate()
    {
        if (!int.TryParse(WidthBox.Text,  out int tw) || tw < 1 ||
            !int.TryParse(HeightBox.Text, out int th) || th < 1)
        {
            StatusLabel.Text = "⚠ Nieprawidłowy rozmiar";
            return null;
        }
        tw = Math.Clamp(tw, 1, 512);
        th = Math.Clamp(th, 1, 512);

        string name = NameBox.Text.Trim();
        if (name.Length == 0) name = "bitmap";

        var scaled = ScaleBitmap(_sourceBitmap, tw, th);
        var pix    = ConvertToPixels(scaled, tw, th,
            (int)ThresholdSlider.Value,
            GetAlgorithm(),
            InvertCheck.IsChecked == true);

        return new PixelTemplate
        {
            Id     = "user_" + Guid.NewGuid().ToString("N")[..8],
            Name   = name,
            Width  = tw,
            Height = th,
            Pixels = pix,
        };
    }
}

/// <summary>Akcja po zatwierdzeniu importu.</summary>
public enum PngImportAction { OpenEditor, SaveLibrary }
