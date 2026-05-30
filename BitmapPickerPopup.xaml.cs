using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OledPaintPro.Models;

namespace OledPaintPro;

public partial class BitmapPickerPopup : Window
{
    const int THUMB_W = 40;
    const int THUMB_H = 40;

    public event Action<PixelTemplate>? TemplateSelected;

    public BitmapPickerPopup()
    {
        InitializeComponent();
        BitmapLibrary.Instance.Changed += OnLibraryChanged;
        Refresh(FilterBox.Text);
    }

    void OnLibraryChanged() => Refresh(FilterBox.Text);

    void Refresh(string filter)
    {
        GroupTabs.Items.Clear();
        string f = filter.Trim().ToLowerInvariant();

        foreach (var group in BitmapLibrary.Instance.Groups)
        {
            var items = group.Items
                .Where(t => string.IsNullOrEmpty(f) || t.Name.ToLowerInvariant().Contains(f))
                .ToList();

            var panel = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var t in items)
                panel.Children.Add(MakeThumbBorder(t));

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 340,
                Content = panel
            };

            var tab = new TabItem
            {
                Header = group.Name,
                Content = scroll
            };
            GroupTabs.Items.Add(tab);
        }

        if (GroupTabs.Items.Count > 0)
            GroupTabs.SelectedIndex = 0;

        // EmptyUserLabel — pokaż jeśli zakładka "Własne" jest pusta
        var userGroup = BitmapLibrary.Instance.Groups
            .FirstOrDefault(g => g.Name == "Własne");
        bool userEmpty = userGroup == null || !userGroup.Items
            .Any(t => string.IsNullOrEmpty(f) || t.Name.ToLowerInvariant().Contains(f));
        EmptyUserLabel.Visibility = userEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    Border MakeThumbBorder(PixelTemplate t)
    {
        var bmp = RenderThumb(t);
        var img = new Image
        {
            Source = bmp, Width = THUMB_W, Height = THUMB_H,
            Stretch = Stretch.None, Cursor = Cursors.Hand,
            ToolTip = t.Name, Margin = new Thickness(1)
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

        var label = new TextBlock
        {
            Text = t.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 8,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = THUMB_W + 4
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(img);
        stack.Children.Add(label);

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0x02, 0x02, 0x10)),
            Child = stack,
            Tag = t,
            Cursor = Cursors.Hand,
            ToolTip = t.Name,
            Margin = new Thickness(2)
        };

        border.MouseEnter += (_, _) =>
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0xA0, 0xD8));
        border.MouseLeave += (_, _) =>
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        border.MouseRightButtonDown += (_, _) => _contextMenuOpen = true;
        border.MouseLeftButtonUp += (_, _) => { TemplateSelected?.Invoke(t); SafeClose(); };

        bool isBuiltIn = BitmapLibrary.Instance.IsBuiltIn(t);
        var ctx = new ContextMenu();

        if (!isBuiltIn)
        {
            var editItem = new MenuItem { Header = "✏ Edytuj" };
            editItem.Click += (_, _) => { OpenEditor(t); SafeClose(); };
            ctx.Items.Add(editItem);
            ctx.Items.Add(new Separator());
        }

        var delItem = new MenuItem
        {
            Header = isBuiltIn ? "ℹ Wbudowana (tylko do odczytu)" : "🗑 Usuń",
            IsEnabled = !isBuiltIn,
            Foreground = isBuiltIn ? Brushes.Gray : Brushes.IndianRed
        };
        if (!isBuiltIn)
            delItem.Click += (_, _) => BitmapLibrary.Instance.Delete(t);
        ctx.Items.Add(delItem);

        ctx.Closed += (_, _) => { _contextMenuOpen = false; if (!IsActive) SafeClose(); };
        border.ContextMenu = ctx;

        return border;
    }

    static WriteableBitmap RenderThumb(PixelTemplate t)
    {
        const uint COL_BG  = 0xFF_02_02_10;   // ciemny granat (piksel OFF)
        const uint COL_PIX = 0xFF_F2_F2_FF;   // biały (piksel ON)

        int stride = THUMB_W * 4;
        uint[] buf = new uint[THUMB_W * THUMB_H];

        Array.Fill(buf, COL_BG);

        // Piksele szablonu — nearest-neighbor scaling
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
                    if (t.Pixels![sy, sx])
                        buf[dy * THUMB_W + dx] = COL_PIX;
                }
            }
        }

        var bmp = new WriteableBitmap(THUMB_W, THUMB_H, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, THUMB_W, THUMB_H), buf, stride, 0);
        return bmp;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        => Refresh(FilterBox.Text);

    private void NewBitmap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title       = "Wybierz plik PNG do importu",
            Filter      = "Obrazy PNG (*.png)|*.png",
            Multiselect = false
        };
        _dialogOpen = true;
        bool? dlgResult = dlg.ShowDialog(this);
        _dialogOpen = false;
        if (dlgResult != true) return;

        string path = dlg.FileName;

        _dialogOpen = true;
        var importWin = new PngImportWindow(path) { Owner = Application.Current.MainWindow };
        bool? importResult = importWin.ShowDialog();
        _dialogOpen = false;

        if (importResult != true || importWin.Result == null) return;

        var tmpl = importWin.Result;

        if (tmpl.ResultAction == PngImportAction.SaveLibrary)
        {
            BitmapLibrary.Instance.Save(tmpl);
        }
        else
        {
            // OpenEditor — wyślij szablon do MainWindow jako nową zakładkę Drawing
            BitmapLibrary.Instance.Save(tmpl);   // zapisz też w bibliotece
            TemplateSelected?.Invoke(tmpl);
            SafeClose();
        }
    }

    /// <summary>Prosty dialog z jednym polem tekstowym. Zwraca null jeśli anulowano.</summary>
    static string? PromptName(string title, string defaultValue)
    {
        var win = new Window
        {
            Title           = title,
            Width           = 300, Height = 110,
            WindowStyle     = WindowStyle.ToolWindow,
            ResizeMode      = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            ShowInTaskbar   = false,
            Topmost         = true
        };
        var txt = new TextBox
        {
            Text       = defaultValue,
            Margin     = new Thickness(10, 10, 10, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xD4, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Padding    = new Thickness(4, 2, 4, 2)
        };
        var ok = new Button
        {
            Content    = "OK",
            Width = 70, Height = 22,
            Margin     = new Thickness(10, 0, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x18, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x9A, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0xA0, 0xD0)),
            IsDefault  = true
        };
        bool confirmed = false;
        ok.Click += (_, _) => { confirmed = true; win.Close(); };
        txt.KeyDown += (_, ev) => { if (ev.Key == Key.Escape) win.Close(); };

        var panel = new StackPanel();
        panel.Children.Add(txt);
        panel.Children.Add(ok);
        win.Content = panel;
        win.Loaded += (_, _) => { txt.SelectAll(); txt.Focus(); };
        win.ShowDialog();
        return confirmed && !string.IsNullOrWhiteSpace(txt.Text) ? txt.Text.Trim() : null;
    }

    void OpenEditor(PixelTemplate? existing)
    {
        var tmpl = existing ?? new PixelTemplate
        {
            Name = $"Bitmapa {BitmapLibrary.Instance.Templates.Count + 1}",
            Width = 24,
            Height = 24
        };
        if (Application.Current.MainWindow is MainWindow mw)
            mw.OpenBitmapTab(tmpl);
    }

    bool _closing = false;
    bool _contextMenuOpen = false;
    bool _dialogOpen = false;

    void SafeClose() { if (_closing) return; _closing = true; Close(); }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_contextMenuOpen && !_dialogOpen) SafeClose();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        BitmapLibrary.Instance.Changed -= OnLibraryChanged;
        base.OnClosed(e);
    }
}
