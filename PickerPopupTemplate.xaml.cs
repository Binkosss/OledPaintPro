using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OledPaintPro;

/// <summary>
/// Wzorzec popup dla biblioteki szablonów. Skopiuj, zmień nazwę klasy i pliku,
/// podmień MyLibrary/MyTemplate na właściwe typy.
/// </summary>
public partial class PickerPopupTemplate : Window
{
    // ── stałe miniaturki ─────────────────────────────────────────────────
    const int THUMB_W = 56;
    const int THUMB_H = 40;

    // ── zdarzenia ────────────────────────────────────────────────────────
    public event Action<PixelTemplate>? TemplateSelected;

    // ── konstruktor ──────────────────────────────────────────────────────
    public PickerPopupTemplate()
    {
        InitializeComponent();
        // Podłącz własną bibliotekę: MyLibrary.Instance.Changed += Refresh;
        OtherLibrary.Instance.Changed += Refresh;
        Refresh();
    }

    // ── odświeżanie listy ─────────────────────────────────────────────────
    void Refresh()
    {
        ThumbPanel.Children.Clear();
        var templates = OtherLibrary.Instance.Templates;

        EmptyLabel.Visibility = templates.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var t in templates)
            ThumbPanel.Children.Add(BuildThumbBorder(t));
    }

    // ── budowanie kafelka ─────────────────────────────────────────────────
    Border BuildThumbBorder(PixelTemplate t)
    {
        var img = new Image
        {
            Source  = RenderThumb(t),
            Width   = THUMB_W,
            Height  = THUMB_H,
            Stretch = Stretch.None,
            Cursor  = Cursors.Hand,
            ToolTip = t.Name,
            Margin  = new Thickness(2),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

        var normalBrush    = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        var highlightBrush = new SolidColorBrush(Color.FromRgb(0x28, 0xD0, 0x9A));

        var border = new Border
        {
            BorderBrush     = normalBrush,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Background      = new SolidColorBrush(Color.FromRgb(0x02, 0x02, 0x10)),
            Child           = img,
            Tag             = t,
            Cursor          = Cursors.Hand,
            ToolTip         = t.Name,
            Margin          = new Thickness(2),
        };

        border.MouseEnter          += (_, _) => border.BorderBrush = highlightBrush;
        border.MouseLeave          += (_, _) => border.BorderBrush = normalBrush;
        border.MouseRightButtonDown += (_, _) => _contextMenuOpen = true;
        border.MouseLeftButtonUp   += (_, _) => { TemplateSelected?.Invoke(t); SafeClose(); };
        border.ContextMenu          = BuildContextMenu(t);

        return border;
    }

    // ── menu kontekstowe ──────────────────────────────────────────────────
    ContextMenu BuildContextMenu(PixelTemplate t)
    {
        var editItem = new MenuItem { Header = "✏ Edytuj" };
        editItem.Click += (_, _) => { OpenEditor(t); SafeClose(); };

        var delItem = new MenuItem { Header = "🗑 Usuń", Foreground = Brushes.IndianRed };
        delItem.Click += (_, _) => OtherLibrary.Instance.Delete(t);

        var ctx = new ContextMenu();
        ctx.Items.Add(editItem);
        ctx.Items.Add(new Separator());
        ctx.Items.Add(delItem);
        ctx.Closed += (_, _) => { _contextMenuOpen = false; if (!IsActive) SafeClose(); };

        return ctx;
    }

    // ── renderowanie miniaturki ───────────────────────────────────────────
    static WriteableBitmap RenderThumb(PixelTemplate t)
    {
        const uint COL_BG  = 0xFF_02_02_10;
        const uint COL_PIX = 0xFF_F2_F2_FF;

        int stride = THUMB_W * 4;
        uint[] buf = new uint[THUMB_W * THUMB_H];
        Array.Fill(buf, COL_BG);

        bool hasPixels = t.Width > 0 && t.Height > 0
                      && t.Pixels != null
                      && t.Pixels.GetLength(0) >= t.Height
                      && t.Pixels.GetLength(1) >= t.Width;

        if (hasPixels)
        {
            for (int dy = 0; dy < THUMB_H; dy++)
            {
                int sy = dy * t.Height / THUMB_H;
                if (sy >= t.Height) continue;
                for (int dx = 0; dx < THUMB_W; dx++)
                {
                    int sx = dx * t.Width / THUMB_W;
                    if (sx >= t.Width) continue;
                    if (t.Pixels[sy, sx])
                        buf[dy * THUMB_W + dx] = COL_PIX;
                }
            }
        }

        var bmp = new WriteableBitmap(THUMB_W, THUMB_H, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, THUMB_W, THUMB_H), buf, stride, 0);
        return bmp;
    }

    // ── przyciski nagłówka ────────────────────────────────────────────────
    private void New_Click(object sender, RoutedEventArgs e)
    {
        OpenEditor(null);
        SafeClose();
    }

    // ── logika edytora ────────────────────────────────────────────────────
    void OpenEditor(PixelTemplate? existing)
    {
        var tmpl = existing ?? new PixelTemplate
        {
            Name   = $"Nowy {OtherLibrary.Instance.Templates.Count + 1}",
            Width  = 24,
            Height = 24,
        };

        if (Application.Current.MainWindow is MainWindow mw)
            mw.OpenOtherTab(tmpl);
    }

    // ── zamknięcie okna ───────────────────────────────────────────────────
    bool _closing         = false;
    bool _contextMenuOpen = false;

    void SafeClose() { if (_closing) return; _closing = true; Close(); }
    private void Window_Deactivated(object sender, EventArgs e) { if (!_contextMenuOpen) SafeClose(); }
}
