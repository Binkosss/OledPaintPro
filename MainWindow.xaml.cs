using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OledPaintPro.Drawing;

namespace OledPaintPro;



// ════════════════════════════════════════════════════════════════════════════
public partial class MainWindow : Window
{
    // ── Wymiary OLED ────────────────────────────────────────────────────────
    static int W = 128;
    static int H = 64;

    // ── Stan pikseli ────────────────────────────────────────────────────────
    bool[,] _pixels = new bool[H, W];   // zatwierdzony stan
    bool[,] _work   = new bool[H, W];   // roboczy (podgląd kształtu)

    // ── Undo / Redo ─────────────────────────────────────────────────────────
    readonly Stack<bool[,]> _undo = new();
    readonly Stack<bool[,]> _redo = new();

    // ── Stan rysowania ──────────────────────────────────────────────────────
    DrawTool _tool       = DrawTool.Pencil;
    bool     _dragging   = false;
    bool     _useWork    = false;    // true = renderuj z _work
    bool     _paintValue = true;     // true = biały, false = czarny
    int      _startX, _startY;      // punkt startowy przeciągania
    int      _prevX,  _prevY;       // poprzednia pozycja (interpolacja ołówka)
    int      _scatterSeed;          // seed dla scatter (deterministyczny)

    // ── Zoom ────────────────────────────────────────────────────────────────
    int _zoom = 6;
    const int ZOOM_MIN = 2;
    const int ZOOM_MAX = 16;

    // ── Siatka ──────────────────────────────────────────────────────────────
    bool _showGrid  = true;
    bool _showMinor = true;

    // ── Rendering ───────────────────────────────────────────────────────────
    readonly PixelRenderer _renderer = new();

    // ── Init guard (eventy XAML odpalają się przed załadowaniem kontrolek) ──
    bool _ready = false;

    // ── Stan panelu ustawień ─────────────────────────────────────────────────
    bool _rightPanelCollapsed = false;

    // ── Animacja — podgląd na głównym canvasie ───────────────────────────────
    AnimationEditorControl? _animPreviewAnim;   // śledzi aktywną animację (do odsubskrybowania)

    // ── Aktywny szablon oka (stamp) ──────────────────────────────────────────
    // ── Globalny schowek zaznaczenia (Ctrl+C/V między zakładkami) ────────────
    static bool[,]? _globalClipboard = null;

    // ── Aktywny szablon oka (stamp) ───────────────────────────────────────────
    PixelTemplate? _activePixelTemplate   = null;
    PixelTemplate? _activeEyeTemplate   = null;
    PixelTemplate? _activeMouthTemplate = null;
    PixelTemplate? _activeOtherTemplate = null;
    PixelTemplate? _activeBitmapTemplate = null;

    // ── Zaznaczenie (Select tool) ────────────────────────────────────────────
    readonly SelectionState _sel = new();

    // ════════════════════════════════════════════════════════════════════════
    public MainWindow() => InitializeComponent();

    // ── Ciemny pasek tytułu (Windows 11 / 10) ───────────────────────────────
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_CAPTION_COLOR           = 35;  // Windows 11+

    // Kolor w formacie 0x00BBGGRR
    static int ToCOLORREF(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Szary kolor paska tytułu (#2A2A2A)
        int captionColor = ToCOLORREF(0x2A, 0x2A, 0x2A);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
    }

    // ── Pan przez kółko myszy i środkowy przycisk ──────────────────────────
    private ScrollViewer? _panTarget;
    private Point _panStart;
    private double _panOriginH;
    private double _panOriginV;

    private static ScrollViewer? FindScrollViewer(DependencyObject? d)
    {
        while (d != null && d is not ScrollViewer)
            d = VisualTreeHelper.GetParent(d);
        return d as ScrollViewer;
    }

    // Shift + obrót kółka → poziomo   |   Ctrl + obrót kółka → pionowo
    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        bool ctrl  = mods.HasFlag(ModifierKeys.Control);
        if (!shift && !ctrl) return;

        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv == null) return;

        if (shift)
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.5);
        else
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.5);

        e.Handled = true;
    }

    // Shift + środkowy przycisk + ruch → pan w kierunku myszy (2D)
    private void Window_PreviewMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv == null) return;
        _panTarget  = sv;
        _panStart   = e.GetPosition(this);
        _panOriginH = sv.HorizontalOffset;
        _panOriginV = sv.VerticalOffset;
        sv.CaptureMouse();
        e.Handled = true;
    }

    private void Window_PreviewMiddleMove(object sender, MouseEventArgs e)
    {
        if (_panTarget == null || e.MiddleButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _panStart;
        _panTarget.ScrollToHorizontalOffset(_panOriginH - delta.X);
        _panTarget.ScrollToVerticalOffset(_panOriginV - delta.Y);
        e.Handled = true;
    }

    private void Window_PreviewMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _panTarget == null) return;
        _panTarget.ReleaseMouseCapture();
        _panTarget = null;
        e.Handled = true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        EyeLibrary.Instance.Load();
        MouthLibrary.Instance.Load();
        OtherLibrary.Instance.Load();
        BitmapLibrary.Instance.Load();

        // Podpięcie eventów canvasu
        CanvasImage.MouseDown  += Canvas_MouseDown;
        CanvasImage.MouseMove  += Canvas_MouseMove;
        CanvasImage.MouseUp    += Canvas_MouseUp;
        CanvasImage.MouseLeave += Canvas_MouseLeave;
        CanvasImage.MouseWheel += Canvas_MouseWheel;

        InitTabs();
        RenderCanvas();
        RefreshCode();
        UpdateZoomLabel();
        // Zaktualizuj panel opcji narzędzia — RadioButton mógł odpalić przed Loaded
        UpdateToolOptionsPanel();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  RENDEROWANIE
    // ════════════════════════════════════════════════════════════════════════
    void RenderCanvas()
    {
        var src = _useWork ? _work : _pixels;
        bool showSel = _tool == DrawTool.Select && _sel.IsActive;
        var bmp = _renderer.Render(src, W, H, _zoom, _showMinor, _showGrid,
            floatPixels: _sel.FloatPixels, floatX: _sel.X, floatY: _sel.Y, floatW: _sel.W, floatH: _sel.H,
            showSelBorder: showSel, selBX: _sel.X, selBY: _sel.Y, selBW: _sel.W, selBH: _sel.H);
        CanvasImage.Width  = bmp.PixelWidth;
        CanvasImage.Height = bmp.PixelHeight;
        CanvasImage.Source = bmp;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ALGORYTMY RYSOWANIA — delegowane do PixelDraw (DRY)
    // ════════════════════════════════════════════════════════════════════════

    // Wewnętrzne skróty z bieżącym W/H
    static void SetPixel(bool[,] c, int x, int y, bool v)
        => PixelDraw.SetPixel(c, x, y, v, W, H);
    static void DrawLine(bool[,] c, int x0, int y0, int x1, int y1, bool v)
        => PixelDraw.DrawLine(c, x0, y0, x1, y1, v, W, H);
    static void FloodFill(bool[,] c, int x, int y, bool v)
        => PixelDraw.FloodFill(c, x, y, v, W, H);

    // Publiczne przeciążenia — używane przez PixelCanvasControl
    internal static void SetPixel(bool[,] c, int x, int y, bool v, int w, int h)
        => PixelDraw.SetPixel(c, x, y, v, w, h);
    internal static void DrawLine(bool[,] c, int x0, int y0, int x1, int y1, bool v, int w, int h)
        => PixelDraw.DrawLine(c, x0, y0, x1, y1, v, w, h);
    internal static void FloodFill(bool[,] c, int x, int y, bool v, int w, int h)
        => PixelDraw.FloodFill(c, x, y, v, w, h);
    internal static void ApplyShapeStatic(bool[,] c, DrawTool tool,
        int x0, int y0, int x1, int y1, bool v, int w, int h, int seed)
        => PixelDraw.ApplyShape(c, tool, x0, y0, x1, y1, v, w, h, seed);

    void ApplyShapeTool(bool[,] c, int x0, int y0, int x1, int y1)
        => PixelDraw.ApplyShape(c, _tool, x0, y0, x1, y1, _paintValue, W, H,
                                _scatterSeed, _activePixelTemplate);

    bool IsShapeTool() => PixelDraw.IsShapeTool(_tool);

    static void StampEye(bool[,] c, PixelTemplate t, int x0, int y0, int x1, int y1, bool v)
        => PixelDraw.StampEye(c, t, x0, y0, x1, y1, v, W, H);

    // ════════════════════════════════════════════════════════════════════════
    //  MYSZ
    // ════════════════════════════════════════════════════════════════════════

    (int px, int py) PixelAt(MouseEventArgs e)
    {
        var pt = e.GetPosition(CanvasImage);
        return (Math.Clamp((int)(pt.X / _zoom), 0, W - 1),
                Math.Clamp((int)(pt.Y / _zoom), 0, H - 1));
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        bool leftBtn  = e.LeftButton  == MouseButtonState.Pressed;
        bool rightBtn = e.RightButton == MouseButtonState.Pressed;
        if (!leftBtn && !rightBtn) return;

        var (px, py) = PixelAt(e);

        // ── Zaznaczenie ────────────────────────────────────────────────────────
        if (_tool == DrawTool.Select)
        {
            CanvasImage.CaptureMouse();
            if (!leftBtn) { CommitSelection(); CanvasImage.ReleaseMouseCapture(); return; }

            if (_sel.Current == SelectionState.Phase.Moving && _sel.HitTest(px, py))
            {
                _sel.BeginMove(px, py);
                _dragging = true;
            }
            else
            {
                if (_sel.IsActive) CommitSelection();
                _sel.BeginDefine(px, py);
                _dragging = true;
            }
            RenderCanvas();
            return;
        }

        if (_dragging) return;

        CanvasImage.CaptureMouse();

        _dragging    = true;
        _startX      = px;  _startY = py;
        _prevX       = px;  _prevY  = py;
        _scatterSeed = Environment.TickCount;

        // Eraser: LPM = gumka, PPM = rysuj | Reszta: LPM = rysuj, PPM = gumka
        _paintValue = (_tool == DrawTool.Eraser) ? !leftBtn : leftBtn;

        SaveUndo();

        if (_tool == DrawTool.FloodFill)
        {
            FloodFill(_pixels, px, py, _paintValue);
            _useWork  = false;
            _dragging = false;
            CanvasImage.ReleaseMouseCapture();
            RenderCanvas();
            RefreshCode();
        }
        else if (IsShapeTool())
        {
            Array.Copy(_pixels, _work, _pixels.Length);
            ApplyShapeTool(_work, px, py, px, py);
            _useWork = true;
            RenderCanvas();
        }
        else
        {
            SetPixel(_pixels, px, py, _paintValue);
            _useWork = false;
            RenderCanvas();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var (px, py) = PixelAt(e);

        // Aktualizuj wyświetlanie współrzędnych
        CoordLabel.Text = $"x={px,3}  y={py,2}  │  byte[{px / 8}] bit{7 - px % 8}";

        // ── Select: kursor + przeciąganie ─────────────────────────────────────
        if (_tool == DrawTool.Select)
        {
            if (_sel.Current == SelectionState.Phase.Moving && !_dragging)
                CanvasImage.Cursor = _sel.HitTest(px, py) ? Cursors.SizeAll : Cursors.Cross;
            if (!_dragging) return;
            if (_sel.Current == SelectionState.Phase.Defining)
            {
                _sel.UpdateDefine(px, py);
                RenderCanvas();
            }
            else if (_sel.Current == SelectionState.Phase.Moving)
            {
                _sel.UpdateMove(px, py, W, H);
                RenderCanvas();
            }
            return;
        }

        if (!_dragging) return;

        if (_tool == DrawTool.Pencil || _tool == DrawTool.Eraser)
        {
            // Interpolacja linią Bresenhama między poprzednią a aktualną pozycją
            DrawLine(_pixels, _prevX, _prevY, px, py, _paintValue);
            _prevX = px; _prevY = py;
            RenderCanvas();
        }
        else if (IsShapeTool())
        {
            Array.Copy(_pixels, _work, _pixels.Length);
            ApplyShapeTool(_work, _startX, _startY, px, py);
            RenderCanvas();
        }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // ── Select ──────────────────────────────────────────────────────────────
        if (_tool == DrawTool.Select)
        {
            if (!_dragging) return;
            _dragging = false;
            CanvasImage.ReleaseMouseCapture();
            var (px2, py2) = PixelAt(e);
            if (_sel.Current == SelectionState.Phase.Defining)
            {
                _sel.UpdateDefine(px2, py2);
                if (_sel.W > 1 || _sel.H > 1)
                    LiftSelection();
                else
                    _sel.Reset();   // klik bez przeciągnięcia = brak zaznaczenia
            }
            // faza Moving: zaznaczenie pozostaje uniesione
            RenderCanvas();
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        CanvasImage.ReleaseMouseCapture();

        if (IsShapeTool())
        {
            Array.Copy(_work, _pixels, _pixels.Length);
            _useWork = false;
        }
        RenderCanvas();
        RefreshCode();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging) CoordLabel.Text = "—";
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ZOOM
    // ════════════════════════════════════════════════════════════════════════
    void ChangeZoom(int delta)
    {
        int nz = Math.Clamp(_zoom + delta, ZOOM_MIN, ZOOM_MAX);
        if (nz == _zoom) return;
        _zoom = nz;
        UpdateZoomLabel();
        RenderCanvas();
    }

    void UpdateZoomLabel()
    {
        ZoomLabel.Text       = $"{_zoom}×";
        CanvasSizeLabel.Text = $"{W * _zoom}×{H * _zoom} px";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WYBÓR EKRANU OLED
    // ════════════════════════════════════════════════════════════════════════
    private void OledDisplay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (OledDisplayCombo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "128x64";

        if (tag == "custom")
        {
            CustomSizePanel.Visibility = Visibility.Visible;
            return;
        }
        CustomSizePanel.Visibility = Visibility.Collapsed;

        var parts = tag.Split('x');
        if (parts.Length != 2) return;
        if (!int.TryParse(parts[0], out int nw) || !int.TryParse(parts[1], out int nh)) return;

        // Jeśli aktywna karta to Drawing/Eye/Mouth/Other — zmień rozmiar tamtego canvasu
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Control is PixelCanvasControl ctrl)
        {
            ctrl.ResizeCanvas(nw, nh);
            _tabs[_activeTabIndex].Settings.W       = nw;
            _tabs[_activeTabIndex].Settings.H       = nh;
            _tabs[_activeTabIndex].Settings.OledTag = tag;
            HeaderSizeLabel.Text = $" · {nw}×{nh} px";
            StatusLabel.Text = $"Rozmiar karty zmieniony na {nw}×{nh} px";
            return;
        }

        // Brak aktywnej karty z canvasem — nic do zrobienia
    }

    private void ZoomIn_Click(object  sender, RoutedEventArgs e) => ActiveTabZoom(+1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ActiveTabZoom(-1);

    void ActiveTabZoom(int delta)
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Control is PixelCanvasControl ctrl)
            ctrl.ZoomBy(delta);
        else if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].AnimControl is AnimationEditorControl anim)
            anim.ActiveEditorControl?.ZoomBy(delta);
    }

    // ── Otwórz picker oczu ───────────────────────────────────────────────────
    internal void ActivateEyeStamp(PixelTemplate t)
    {
        _activeEyeTemplate   = t;
        _activePixelTemplate = t;
        _tool = DrawTool.EyeStamp;
        if (_ready) ToolHintLabel.Text = $"👁 Stempel: {t.Name} — przeciągnij aby wkleić";
        StatusLabel.Text = $"Stempel: {t.Name}";
        SyncStampToActiveTab();
    }

    internal void ActivateMouthStamp(PixelTemplate t)
    {
        _activeMouthTemplate = t;
        _activePixelTemplate   = t;
        _tool = DrawTool.MouthStamp;
        if (_ready) ToolHintLabel.Text = $"😊 Usta: {t.Name} — przeciągnij aby wkleić";
        StatusLabel.Text = $"Stempel ust: {t.Name}";
        SyncStampToActiveTab();
    }

    internal void ActivateOtherStamp(PixelTemplate t)
    {
        _activeOtherTemplate = t;
        _activePixelTemplate   = t;
        _tool = DrawTool.OtherStamp;
        if (_ready) ToolHintLabel.Text = $"★ Inne: {t.Name} — przeciągnij aby wkleić";
        StatusLabel.Text = $"Stempel: {t.Name}";
        SyncStampToActiveTab();
    }

    internal void ActivateBitmapStamp(PixelTemplate t)
    {
        _activeBitmapTemplate = t;
        _activePixelTemplate    = t;
        _tool = DrawTool.BitmapStamp;
        if (_ready) ToolHintLabel.Text = $"🖼 Bitmap: {t.Name} ({t.Width}×{t.Height}) — przeciągnij aby wkleić";
        StatusLabel.Text = $"Bitmap: {t.Name}";
        SyncStampToActiveTab();
    }

    internal void OpenBitmapTab(PixelTemplate tmpl)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Kind == TabKind.Other && _tabs[i].Template?.Id == tmpl.Id)
            { ActivateTab(i); return; }
        }
        OpenEditorTab(TabKind.Other, tmpl, $"🖼 {tmpl.Name}");
    }

    void SyncStampToActiveTab()
    {
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            if (tab.Control != null)
            {
                tab.Control.ActiveTool          = _tool;
                tab.Control.ActiveStampTemplate = _activePixelTemplate;
            }
            else if (tab.AnimControl != null)
            {
                tab.AnimControl.ActiveTool = _tool;
                tab.AnimControl.SetStampTemplate(_activePixelTemplate);
            }
        }
    }

    // ── Zakładki ─────────────────────────────────────────────────────────────
    enum TabKind { Main, Drawing, Eye, Mouth, Other, Animation, Sprite }

    // Ustawienia per-karta (Main + każda karta edytora)
    class TabSettings
    {
        public int        W          = 128;
        public int        H          = 64;
        public int        Zoom       = 6;
        public bool       ShowGrid   = true;
        public bool       ShowMinor  = true;
        public string     VarName    = "myBitmap";
        public string     OledTag    = "128x64";
        public bool[,]    Pixels     = new bool[64, 128];
        public int        BrushSize  = 1;
        public BrushShape BrushShape = BrushShape.Circle;
    }

    record TabEntry(TabKind Kind, PixelTemplate? Template, PixelCanvasControl? Control, Border Tab, AnimationEditorControl? AnimControl = null, SpriteEditorControl? SpriteControl = null)
    {
        public TabSettings Settings { get; } = new();
    }

    readonly List<TabEntry> _tabs = new();
    int _activeTabIndex = -1;

    // Zachowaj stary alias dla kompatybilności z EyePickerPopup
    readonly List<(PixelTemplate Template, PixelCanvasControl Control, Border Tab)> _eyeTabs = new();

    void InitTabs()
    {
        var tmpl = new PixelTemplate { Name = "mybitmap1", Width = W, Height = H, Pixels = new bool[H, W] };
        OpenEditorTab(TabKind.Drawing, tmpl, "mybitmap1");
    }

    internal void OpenEyeTab(PixelTemplate template)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Kind == TabKind.Eye && _tabs[i].Template?.Id == template.Id)
            { ActivateTab(i); return; }
        }
        OpenEditorTab(TabKind.Eye, template, $"👁 {template.Name}");
    }

    internal void OpenMouthTab(PixelTemplate template)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Kind == TabKind.Mouth && _tabs[i].Template?.Id == template.Id)
            { ActivateTab(i); return; }
        }
        OpenEditorTab(TabKind.Mouth, template, $"😊 {template.Name}");
    }

    internal void OpenOtherTab(PixelTemplate template)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Kind == TabKind.Other && _tabs[i].Template?.Id == template.Id)
            { ActivateTab(i); return; }
        }
        OpenEditorTab(TabKind.Other, template, $"★ {template.Name}");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  TOPBAR — OTWÓRZ .H / WKLEJ KOD / KOPIUJ / ANIMACJA
    // ════════════════════════════════════════════════════════════════════════

    internal static void DwmSetWindowAttributePublic(IntPtr hwnd, int attr, ref int val, int size)
        => DwmSetWindowAttribute(hwnd, attr, ref val, size);

    // Następna wolna nazwa mybitmap{n} (globalna po wszystkich zakładkach)
    string NextBitmapName()
    {
        int max = 0;
        foreach (var t in _tabs)
        {
            var nm = t.Settings.VarName;
            if (nm.StartsWith("mybitmap") && int.TryParse(nm["mybitmap".Length..], out int n))
                max = Math.Max(max, n);
        }
        return $"mybitmap{max + 1}";
    }

    void OpenFromFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Otwórz bitmapę z pliku .h",
            Filter = "Arduino Header (*.h)|*.h|Pliki C/C++ (*.c;*.cpp)|*.c;*.cpp|Wszystkie pliki (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        string code     = File.ReadAllText(dlg.FileName, Encoding.UTF8);
        string hintName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        OpenBitmapFromCode(code, hintName);
    }

    void PasteCode_Click(object sender, RoutedEventArgs e)
    {
        ShowPasteCodeDialog();
    }

    void ShowPasteCodeDialog()
    {
        var win = new Window
        {
            Title                 = "Wklej kod C — tablica bitmapy",
            Width                 = 620,
            Height                = 480,
            ResizeMode            = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        };

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // hint
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // textbox
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // W/H row
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // error
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        // ── Hint ─────────────────────────────────────────────────────────────
        var hint = new TextBlock
        {
            Text         = "Wklej kod C z tablicą PROGMEM (LCD Image Converter, u8g2, Adafruit GFX…):",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize     = 11,
            Margin       = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(hint, 0);
        root.Children.Add(hint);

        // ── Textbox ──────────────────────────────────────────────────────────
        var tb = new TextBox
        {
            AcceptsReturn    = true, AcceptsTab = true,
            TextWrapping     = TextWrapping.NoWrap,
            FontFamily       = new FontFamily("Consolas"), FontSize = 11,
            Background       = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E)),
            Foreground       = new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0xCC)),
            BorderBrush      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness  = new Thickness(1),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding          = new Thickness(8),
            Text             = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty,
        };
        Grid.SetRow(tb, 1);
        root.Children.Add(tb);

        // ── Rozmiar ręcznie (zawsze widoczne) ────────────────────────────────
        var sizeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 8, 0, 4),
        };

        void MakeLabel(string txt) => sizeRow.Children.Add(new TextBlock
        {
            Text = txt, Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        TextBox MakeNumBox(string val) {
            var box = new TextBox
            {
                Text = val, Width = 52, Padding = new Thickness(5, 3, 5, 3),
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0xCC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x66, 0x44)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                Margin = new Thickness(0, 0, 10, 0),
            };
            return box;
        }

        sizeRow.Children.Add(new TextBlock
        {
            Text = "Rozmiar (jeśli auto-detekcja nie działa):",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 10,
        });
        MakeLabel("W:");
        var wBox = MakeNumBox("128");
        sizeRow.Children.Add(wBox);
        MakeLabel("H:");
        var hBox = MakeNumBox("64");
        sizeRow.Children.Add(hBox);

        Grid.SetRow(sizeRow, 2);
        root.Children.Add(sizeRow);

        // ── Błąd ─────────────────────────────────────────────────────────────
        var errLabel = new TextBlock
        {
            Foreground   = new SolidColorBrush(Color.FromRgb(0xFF, 0x77, 0x44)),
            FontSize     = 10,
            Margin       = new Thickness(0, 2, 0, 4),
            TextWrapping = TextWrapping.Wrap,
            Visibility   = Visibility.Collapsed,
        };
        Grid.SetRow(errLabel, 3);
        root.Children.Add(errLabel);

        // ── Przyciski ─────────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(btnRow, 4);

        bool ok = false;
        var okBtn = new Button
        {
            Content = "✔ Otwórz bitmapę",
            Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x28, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0xFF, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x88, 0x55)),
            BorderThickness = new Thickness(1),
        };
        okBtn.Click += (_, _) =>
        {
            errLabel.Visibility = Visibility.Collapsed;
            var code = tb.Text.Trim();
            if (string.IsNullOrEmpty(code)) { errLabel.Text = "⚠ Pole kodu jest puste."; errLabel.Visibility = Visibility.Visible; return; }

            // Pobierz ręczny override W/H (0 = auto)
            int.TryParse(wBox.Text, out int manW);
            int.TryParse(hBox.Text, out int manH);

            var result = BitmapParser.TryParse(code, out string err, manW, manH);
            if (result == null)
            {
                errLabel.Text       = $"⚠ {err}";
                errLabel.Visibility = Visibility.Visible;
                return;
            }
            ok = true;
            win.Tag = (code, manW, manH);
            win.Close();
        };
        btnRow.Children.Add(okBtn);

        var cancelBtn = new Button
        {
            Content = "Anuluj", Padding = new Thickness(12, 7, 12, 7),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
        };
        cancelBtn.Click += (_, _) => win.Close();
        btnRow.Children.Add(cancelBtn);

        root.Children.Add(btnRow);
        win.Content = root;
        win.ShowDialog();

        if (ok && win.Tag is (string parsedCode, int ow, int oh))
            OpenBitmapFromCode(parsedCode, null, ow, oh);
    }


    void OpenBitmapFromCode(string code, string? hintName, int overrideW = 0, int overrideH = 0)
    {
        // ── Spróbuj wczytać jako animację wieloklatową ────────────────────
        var animResult = BitmapParser.TryParseAnimation(code, out string animErr, overrideW, overrideH);
        if (animResult != null)
        {
            string animName = hintName ?? animResult.BaseName;
            if (animName.Length == 0) animName = "anim1";
            LoadAnimationFromCode(animResult, animName);
            return;
        }

        var result = BitmapParser.TryParse(code, out string err, overrideW, overrideH);
        if (result == null)
        {
            MessageBox.Show($"Błąd parsowania:\n{err}", "OLED Paint Pro",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Jeśli aktywna karta to animacja — dodaj klatkę do animacji
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Kind == TabKind.Animation
            && _tabs[_activeTabIndex].AnimControl is AnimationEditorControl animCtrl)
        {
            // Auto-skaluj animację jeśli rozmiar bitmapy różni się od rozmiaru klatek
            if (result.Width != animCtrl.FrameWidth || result.Height != animCtrl.FrameHeight)
            {
                int nw = result.Width, nh = result.Height;
                animCtrl.ResizeAllFrames(nw, nh);
                var tab2 = _tabs[_activeTabIndex];
                tab2.Settings.W = nw;
                tab2.Settings.H = nh;
                string newTag = $"{nw}x{nh}";
                tab2.Settings.OledTag = newTag;
                HeaderSizeLabel.Text = $" · {nw}×{nh} px";
                // Zaktualizuj combo do nowego rozmiaru lub przestaw na "custom"
                _ready = false;
                bool found = false;
                foreach (ComboBoxItem ci in AnimOledCombo.Items)
                    if (ci.Tag?.ToString() == newTag) { AnimOledCombo.SelectedItem = ci; found = true; break; }
                if (!found)
                {
                    foreach (ComboBoxItem ci in AnimOledCombo.Items)
                        if (ci.Tag?.ToString() == "custom") { AnimOledCombo.SelectedItem = ci; break; }
                    AnimCustomSizePanel.Visibility = Visibility.Visible;
                    AnimCustomWidthBox.Text  = nw.ToString();
                    AnimCustomHeightBox.Text = nh.ToString();
                }
                _ready = true;
                StatusLabel.Text = $"🎬 Auto-skalowanie animacji do {nw}×{nh} px";
            }

            var px = new bool[result.Height, result.Width];
            for (int r = 0; r < result.Height; r++)
                for (int c = 0; c < result.Width; c++)
                    px[r, c] = result.Pixels[r, c];
            animCtrl.AddFrameFromPixels(px);
            StatusLabel.Text = $"Dodano klatkę do animacji '{_tabs[_activeTabIndex].Settings.VarName}'";
            return;
        }

        string name = hintName ?? result.Name;
        // Sprawdź czy nie jest to mybitmap — lepiej dać własną nazwę
        if (name.Length == 0 || name == "bitmap") name = NextBitmapName();

        var tmpl = new PixelTemplate
        {
            Name   = name,
            Width  = result.Width,
            Height = result.Height,
            Pixels = new bool[result.Height, result.Width],   // jawna alokacja!
        };
        for (int r = 0; r < result.Height; r++)
            for (int c = 0; c < result.Width; c++)
                tmpl.Pixels[r, c] = result.Pixels[r, c];

        OpenEditorTab(TabKind.Drawing, tmpl, name);
        StatusLabel.Text = $"Otwarto '{name}' ({result.Width}×{result.Height}px)";
    }

    void LoadAnimationFromCode(BitmapParser.AnimationParseResult animResult, string animName)
    {
        // Jeśli aktywna karta to animacja — załaduj do niej (zastąp klatki)
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Kind == TabKind.Animation
            && _tabs[_activeTabIndex].AnimControl is AnimationEditorControl existingAnim)
        {
            var res = MessageBox.Show(
                $"Wczytano animację: {animResult.Frames.Count} klatek, {animResult.Width}×{animResult.Height}px.\n\n" +
                "Zastąpić klatki w bieżącej animacji?",
                "Wczytaj animację", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            existingAnim.LoadFrames(animResult.Frames, animResult.Width, animResult.Height,
                animName, animResult.Fps);
            var tab = _tabs[_activeTabIndex];
            tab.Settings.W = animResult.Width;
            tab.Settings.H = animResult.Height;
            tab.Settings.OledTag = $"{animResult.Width}x{animResult.Height}";
            tab.Settings.VarName = animName;
            UpdateAnimComboForSize(animResult.Width, animResult.Height);
            AnimNameBox.Text     = animName;
            HeaderSizeLabel.Text = $" · {animResult.Width}×{animResult.Height} px";
            StatusLabel.Text     = $"🎬 Wczytano animację '{animName}' — {animResult.Frames.Count} klatek, {animResult.Fps} FPS";
            return;
        }

        // Otwórz nową kartę animacji
        var settings = new TabSettings
        {
            VarName = animName,
            W       = animResult.Width,
            H       = animResult.Height,
            OledTag = $"{animResult.Width}x{animResult.Height}",
        };
        var animCtrl = new AnimationEditorControl(animName, animResult.Width, animResult.Height);
        animCtrl.LoadFrames(animResult.Frames, animResult.Width, animResult.Height,
            animName, animResult.Fps);

        OpenAnimationTab(animCtrl, settings);
        StatusLabel.Text = $"🎬 Otwarto animację '{animName}' — {animResult.Frames.Count} klatek, {animResult.Fps} FPS";
    }

    void UpdateAnimComboForSize(int w, int h)
    {
        string tag = $"{w}x{h}";
        _ready = false;
        bool found = false;
        foreach (ComboBoxItem ci in AnimOledCombo.Items)
            if (ci.Tag?.ToString() == tag) { AnimOledCombo.SelectedItem = ci; found = true; break; }
        if (!found)
        {
            foreach (ComboBoxItem ci in AnimOledCombo.Items)
                if (ci.Tag?.ToString() == "custom") { AnimOledCombo.SelectedItem = ci; break; }
            AnimCustomSizePanel.Visibility = Visibility.Visible;
            AnimCustomWidthBox.Text  = w.ToString();
            AnimCustomHeightBox.Text = h.ToString();
        }
        else
        {
            AnimCustomSizePanel.Visibility = Visibility.Collapsed;
        }
        _ready = true;
    }

    void CopyCurrentDrawing_Click(object sender, RoutedEventArgs e)
    {
        bool[,] srcPixels;
        int sw, sh;
        string srcName;

        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Control is PixelCanvasControl ctrl)
        {
            srcPixels = ctrl.CurrentPixels;
            sw        = ctrl.Template.Width;
            sh        = ctrl.Template.Height;
            srcName   = ctrl.Template.Name;
        }
        else return;

        // Wyznacz nową nazwę: jeśli kończy się cyfrą, zwiększ; inaczej dodaj 2
        string newName = IncrementName(srcName);

        var tmpl = new PixelTemplate
        {
            Name   = newName,
            Width  = sw,
            Height = sh,
            Pixels = new bool[sh, sw],   // jawna alokacja!
        };
        for (int r = 0; r < sh; r++)
            for (int c = 0; c < sw; c++)
                tmpl.Pixels[r, c] = srcPixels[r, c];

        OpenEditorTab(TabKind.Drawing, tmpl, newName);
        StatusLabel.Text = $"Kopia '{srcName}' → '{newName}'";
    }

    static string IncrementName(string name)
    {
        // Znajdź końcową liczbę i zwiększ ją
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        if (i < name.Length - 1)
        {
            string prefix = name[..(i + 1)];
            int num       = int.Parse(name[(i + 1)..]);
            return $"{prefix}{num + 1}";
        }
        return name + "2";
    }

    void NewAnimation_Click(object sender, RoutedEventArgs e)
    {
        int n  = _tabs.Count(t => t.Kind == TabKind.Animation) + 1;
        string nm = $"anim{n}";
        // Użyj rozmiaru aktywnej karty lub domyślnego 128x64
        int aw = 128, ah = 64;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            aw = _tabs[_activeTabIndex].Settings.W;
            ah = _tabs[_activeTabIndex].Settings.H;
        }
        OpenAnimationTab(nm, aw, ah);
    }

    void NewSprite_Click(object sender, RoutedEventArgs e)
    {
        int n = _tabs.Count(t => t.Kind == TabKind.Sprite) + 1;
        string nm = $"sprite{n}";
        int aw = 32, ah = 32;   // domyślny rozmiar sprite'a
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            aw = _tabs[_activeTabIndex].Settings.W;
            ah = _tabs[_activeTabIndex].Settings.H;
        }
        OpenSpriteTab(nm, aw, ah);
    }

    void OpenSpriteTab(string name, int width, int height)
    {
        var spriteCtrl = new SpriteEditorControl(name, width, height);
        var settings   = new TabSettings { W = width, H = height, VarName = name, OledTag = $"{width}x{height}" };

        var accent    = Color.FromRgb(0xFF, 0x99, 0x00);
        var tabBorder = BuildTabHeader($"🕹️ {name}", canClose: true, accent);

        int idx   = _tabs.Count;
        var entry = new TabEntry(TabKind.Sprite, null, null, tabBorder, null, spriteCtrl);
        entry.Settings.W       = settings.W;
        entry.Settings.H       = settings.H;
        entry.Settings.VarName = settings.VarName;
        entry.Settings.OledTag = settings.OledTag;
        _tabs.Add(entry);

        tabBorder.MouseLeftButtonUp += (_, _) => { int i = _tabs.IndexOf(entry); if (i >= 0) ActivateTab(i); };
        tabBorder.MouseDown += (_, ev) =>
        {
            if (ev.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
            int i = _tabs.IndexOf(entry); if (i >= 0) CloseTab(i);
            ev.Handled = true;
        };
        if (tabBorder.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is Button closeBtn)
            closeBtn.Click += (_, _) => { int i = _tabs.IndexOf(entry); if (i >= 0) CloseTab(i); };

        TabBar.Children.Add(tabBorder);
        ActivateTab(idx);
    }

    SpriteEditorControl? ActiveSprite =>
        (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count &&
         _tabs[_activeTabIndex].Kind == TabKind.Sprite)
            ? _tabs[_activeTabIndex].SpriteControl
            : null;

    void OpenAnimationTab(string name, int width, int height)
    {
        var animCtrl = new AnimationEditorControl(name, width, height);
        OpenAnimationTab(animCtrl, new TabSettings { W = width, H = height, VarName = name, OledTag = $"{width}x{height}" });
    }

    void OpenAnimationTab(AnimationEditorControl animCtrl, TabSettings settings)
    {
        var accent    = Color.FromRgb(0xFF, 0xCC, 0x00);
        var tabBorder = BuildTabHeader($"🎬 {settings.VarName}", canClose: true, accent);

        int idx   = _tabs.Count;
        var entry = new TabEntry(TabKind.Animation, null, null, tabBorder, animCtrl);
        entry.Settings.W       = settings.W;
        entry.Settings.H       = settings.H;
        entry.Settings.VarName = settings.VarName;
        entry.Settings.OledTag = settings.OledTag;
        _tabs.Add(entry);

        tabBorder.MouseLeftButtonUp += (_, _) =>
        {
            int i = _tabs.IndexOf(entry);
            if (i >= 0) ActivateTab(i);
        };
        tabBorder.MouseDown += (_, ev) =>
        {
            if (ev.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
            int i = _tabs.IndexOf(entry);
            if (i >= 0) CloseTab(i);
            ev.Handled = true;
        };
        if (tabBorder.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is Button closeBtn)
        {
            closeBtn.Click += (_, _) =>
            {
                int i = _tabs.IndexOf(entry);
                if (i >= 0) CloseTab(i);
            };
        }
        TabBar.Children.Add(tabBorder);
        animCtrl.FrameCountChanged += () => PopulateTweenCombos(animCtrl);
        ActivateTab(idx);
    }

    void NewDrawingTab_Click(object sender, RoutedEventArgs e)    {
        int n = _tabs.Count(t => t.Kind == TabKind.Main || t.Kind == TabKind.Drawing) + 1;
        string defaultName = $"mybitmap{n}";
        var tmpl = new PixelTemplate
        {
            Name   = defaultName,
            Width  = 128,
            Height = 64,
        };
        OpenEditorTab(TabKind.Drawing, tmpl, defaultName);
    }

    void OpenEditorTab(TabKind kind, PixelTemplate template, string label)
    {
        var ctrl = new PixelCanvasControl(template);
        ctrl.ActiveTool = _tool;
        // Settings dla tej karty
        var settings = new TabSettings
        {
            W       = template.Width,
            H       = template.Height,
            OledTag = $"{template.Width}x{template.Height}",
        };
        ctrl.SaveRequested += ec =>
        {
            if (kind == TabKind.Eye)
            {
                EyeLibrary.Instance.Save(ec.Template);
                StatusLabel.Text = $"Zapisano: {ec.Template.Name}";
            }
            else if (kind == TabKind.Mouth)
            {
                MouthLibrary.Instance.Save(ec.Template);
                StatusLabel.Text = $"Zapisano usta: {ec.Template.Name}";
            }
            else if (kind == TabKind.Other)
            {
                OtherLibrary.Instance.Save(ec.Template);
                StatusLabel.Text = $"Zapisano: {ec.Template.Name}";
            }
            else
            {
                StatusLabel.Text = $"Rysunek '{ec.Template.Name}' gotowy";
            }
        };

        // Odśwież kod C gdy piksele się zmienią
        ctrl.PixelsChanged += ec =>
        {
            int i = _tabs.FindIndex(t => t.Control == ec);
            if (i == _activeTabIndex) RefreshCode();
        };

        // Aktualizuj ZoomLabel gdy zoom się zmieni
        ctrl.ZoomChanged += zoom =>
        {
            int i = _tabs.FindIndex(t => t.Control == ctrl);
            if (i == _activeTabIndex)
            {
                ZoomLabel.Text       = $"{zoom}×";
                CanvasSizeLabel.Text = $"{ctrl.Template.Width * zoom}×{ctrl.Template.Height * zoom} px";
            }
        };

        // Aktualizuj CoordLabel w prawym panelu MainWindow
        ctrl.CoordChanged += text =>
        {
            int i = _tabs.FindIndex(t => t.Control == ctrl);
            if (i == _activeTabIndex) CoordLabel.Text = text;
        };

        var accentColor = kind switch
        {
            TabKind.Eye       => Color.FromRgb(0x28, 0xD0, 0x9A),
            TabKind.Mouth     => Color.FromRgb(0xFF, 0x88, 0x44),
            TabKind.Other     => Color.FromRgb(0xCC, 0x88, 0xFF),
            TabKind.Animation => Color.FromRgb(0xFF, 0xCC, 0x00),
            _                 => Color.FromRgb(0x55, 0x88, 0xEE),
        };

        var tabBorder = BuildTabHeader(label, canClose: true, accentColor);

        int idx   = _tabs.Count;
        var entry = new TabEntry(kind, template, ctrl, tabBorder);
        // Skopiuj settings
        entry.Settings.W       = settings.W;
        entry.Settings.H       = settings.H;
        entry.Settings.OledTag = settings.OledTag;
        entry.Settings.VarName = template.Name;
        _tabs.Add(entry);
        if (kind == TabKind.Eye || kind == TabKind.Mouth || kind == TabKind.Other)
            _eyeTabs.Add((template, ctrl, tabBorder));

        tabBorder.MouseLeftButtonUp += (_, _) =>
        {
            int i = _tabs.IndexOf(entry);
            if (i >= 0) ActivateTab(i);
        };
        tabBorder.MouseDown += (_, ev) =>
        {
            if (ev.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
            int i = _tabs.IndexOf(entry);
            if (i >= 0) CloseTab(i);
            ev.Handled = true;
        };
        // przycisk zamknięcia — drugi child stackpanela
        if (tabBorder.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is Button closeBtn)
        {
            closeBtn.Click += (_, _) =>
            {
                int i = _tabs.IndexOf(entry);
                if (i >= 0) CloseTab(i);
            };
        }

        TabBar.Children.Add(tabBorder);
        ActivateTab(idx);
    }

    Border BuildTabHeader(string label, bool canClose, Color accentColor)
    {
        var tabBorder = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius    = new CornerRadius(4, 4, 0, 0),
            Margin          = new Thickness(2, 3, 0, 0),
            Cursor          = Cursors.Hand,
            Padding         = new Thickness(10, 4, canClose ? 4 : 10, 4),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sp.Children.Add(lbl);
        if (canClose)
        {
            var closeBtn = new Button
            {
                Content               = "×",
                FontSize              = 12,
                Foreground            = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                Background            = Brushes.Transparent,
                BorderThickness       = new Thickness(0),
                Cursor                = Cursors.Hand,
                Margin                = new Thickness(6, 0, 0, 0),
                VerticalAlignment     = VerticalAlignment.Center,
                OverridesDefaultStyle = true,
                Template              = CreateCloseBtnTemplate(),
            };
            sp.Children.Add(closeBtn);
        }
        tabBorder.Child = sp;
        return tabBorder;
    }

    static ControlTemplate CreateCloseBtnTemplate()
    {
        var tpl = new ControlTemplate(typeof(Button));
        var bd  = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp  = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        return tpl;
    }

    void ActivateTab(int idx)
    {
        if (idx < 0 || idx >= _tabs.Count) return;

        // ── Odepnij eventy poprzedniej animacji ─────────────────────────────
        if (_animPreviewAnim != null)
        {
            _animPreviewAnim.FramePreview    -= Anim_FramePreview;
            _animPreviewAnim.PlaybackStopped -= Anim_PlaybackStopped;
            _animPreviewAnim = null;
        }

        _activeTabIndex = idx;
        var active = _tabs[idx];

        // ── Style zakładek ───────────────────────────────────────────────────
        for (int i = 0; i < _tabs.Count; i++)
        {
            var t      = _tabs[i];
            bool isAct = i == idx;
            var accent = t.Kind switch
                {
                    TabKind.Eye       => Color.FromRgb(0x28, 0xD0, 0x9A),
                    TabKind.Mouth     => Color.FromRgb(0xFF, 0x88, 0x44),
                    TabKind.Other     => Color.FromRgb(0xCC, 0x88, 0xFF),
                    TabKind.Animation => Color.FromRgb(0xFF, 0xCC, 0x00),
                    TabKind.Sprite    => Color.FromRgb(0xFF, 0x99, 0x00),
                    _                 => Color.FromRgb(0x55, 0x88, 0xEE),
                };
            t.Tab.Background  = new SolidColorBrush(isAct
                ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0x14, 0x14, 0x14));
            t.Tab.BorderBrush = new SolidColorBrush(isAct
                ? accent : Color.FromRgb(0x38, 0x38, 0x38));
            if (t.Tab.Child is StackPanel sp && sp.Children[0] is TextBlock lbl)
                lbl.Foreground = new SolidColorBrush(isAct
                    ? accent : Color.FromRgb(0x80, 0x80, 0x80));
        }

        var s = active.Settings;

        // Stary canvas MainWindow zawsze ukryty — każda karta to PixelCanvasControl
        CanvasScrollBorder.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility    = Visibility.Collapsed;

        // ── Sync wspólnych kontrolek UI ──────────────────────────────────────
        _ready = false;
        VarNameBox.Text      = s.VarName;
        HeaderSizeLabel.Text = $" · {s.W}×{s.H} px";
        string oledTag = $"{s.W}x{s.H}";
        foreach (ComboBoxItem item in OledDisplayCombo.Items)
            if (item.Tag?.ToString() == oledTag) { OledDisplayCombo.SelectedItem = item; break; }
        _ready = true;

        if (active.Kind == TabKind.Animation && active.AnimControl != null)
        {
            active.AnimControl.ActiveTool = _tool;
            active.AnimControl.SetGrid(_showGrid, _showMinor);
            active.AnimControl.SetStampTemplate(_activePixelTemplate);
            active.AnimControl.SyncFrames = SyncSelectionChk.IsChecked == true;
            // Synchronizuj symetrię z ToggleButtonów
            bool sv = FindName("SymVToggle") is System.Windows.Controls.Primitives.ToggleButton tv && tv.IsChecked == true;
            bool sh = FindName("SymHToggle") is System.Windows.Controls.Primitives.ToggleButton th && th.IsChecked == true;
            active.AnimControl.SetSymmetry(sv, sh);
            TabContent.Content         = active.AnimControl;
            TabContent.Visibility      = Visibility.Visible;
            SaveTemplatePanel.Visibility  = Visibility.Collapsed;
            AnimSettingsPanel.Visibility    = Visibility.Visible;
            DrawingSettingsPanel.Visibility = Visibility.Collapsed;
            CodeSection.Visibility        = Visibility.Collapsed;
            CodeActionPanel.Visibility    = Visibility.Collapsed;
            _ready = false;
            bool animTagFound = false;
            foreach (ComboBoxItem item in AnimOledCombo.Items)
                if (item.Tag?.ToString() == oledTag) { AnimOledCombo.SelectedItem = item; animTagFound = true; break; }
            if (!animTagFound)
            {
                foreach (ComboBoxItem item in AnimOledCombo.Items)
                    if (item.Tag?.ToString() == "custom") { AnimOledCombo.SelectedItem = item; break; }
                AnimCustomSizePanel.Visibility = Visibility.Visible;
                AnimCustomWidthBox.Text  = s.W.ToString();
                AnimCustomHeightBox.Text = s.H.ToString();
            }
            else
            {
                AnimCustomSizePanel.Visibility = Visibility.Collapsed;
            }
            AnimNameBox.Text = s.VarName;
            _ready = true;
            _animPreviewAnim = active.AnimControl;
            active.AnimControl.FramePreview    += Anim_FramePreview;
            active.AnimControl.PlaybackStopped += Anim_PlaybackStopped;
            active.AnimControl.CoordChanged    += text => CoordLabel.Text = text;
            active.AnimControl.ZoomChanged     += zoom =>
            {
                ZoomLabel.Text       = $"{zoom}×";
                var ac = active.AnimControl.ActiveEditorControl;
                if (ac != null)
                    CanvasSizeLabel.Text = $"{ac.Template.Width * zoom}×{ac.Template.Height * zoom} px";
            };
            PopulateTweenCombos(active.AnimControl);
            if (_tool == DrawTool.Pencil || _tool == DrawTool.Eraser)
                SyncBrushToActiveTab();
            StatusLabel.Text = s.VarName;
        }
        else if (active.Kind == TabKind.Sprite && active.SpriteControl != null)
        {
            active.SpriteControl.ActiveTool = _tool;
            active.SpriteControl.SetGrid(_showGrid, _showMinor);
            active.SpriteControl.SetStampTemplate(_activePixelTemplate);
            bool sv2 = FindName("SymVToggle") is System.Windows.Controls.Primitives.ToggleButton tv2 && tv2.IsChecked == true;
            bool sh2 = FindName("SymHToggle") is System.Windows.Controls.Primitives.ToggleButton th2 && th2.IsChecked == true;
            active.SpriteControl.SetSymmetry(sv2, sh2);
            TabContent.Content              = active.SpriteControl;
            TabContent.Visibility           = Visibility.Visible;
            SaveTemplatePanel.Visibility    = Visibility.Collapsed;
            AnimSettingsPanel.Visibility    = Visibility.Collapsed;
            SpriteSettingsPanel.Visibility  = Visibility.Visible;
            DrawingSettingsPanel.Visibility = Visibility.Collapsed;
            CodeSection.Visibility          = Visibility.Collapsed;
            CodeActionPanel.Visibility      = Visibility.Collapsed;
            _ready = false;
            SpriteNameBox.Text = s.VarName;
            var ms = active.SpriteControl.MotionSettings;
            SpriteStartXBox.Text  = ms.StartX.ToString();
            SpriteStartYBox.Text  = ms.StartY.ToString();
            SpriteEndXBox.Text    = ms.EndX.ToString();
            SpriteEndYBox.Text    = ms.EndY.ToString();
            SpriteDelayBox.Text   = ms.DelayMs.ToString();
            SpriteLoopOnce.IsChecked   = ms.LoopMode == Models.SpriteLoopMode.Once;
            SpriteLoopLoop.IsChecked   = ms.LoopMode == Models.SpriteLoopMode.Loop;
            SpriteLoopBounce.IsChecked = ms.LoopMode == Models.SpriteLoopMode.Bounce;
            _ready = true;
            active.SpriteControl.CoordChanged += text => CoordLabel.Text = text;
            active.SpriteControl.ZoomChanged  += zoom =>
            {
                ZoomLabel.Text = $"{zoom}×";
                var ac = active.SpriteControl.ActiveEditorControl;
                if (ac != null)
                    CanvasSizeLabel.Text = $"{ac.Template.Width * zoom}×{ac.Template.Height * zoom} px";
            };
            if (_tool == DrawTool.Pencil || _tool == DrawTool.Eraser)
                SyncBrushToActiveTab();
            StatusLabel.Text = s.VarName;
        }
        else if (active.Control != null)
        {
            active.Control.ActiveTool          = _tool;
            active.Control.ActiveStampTemplate = _activePixelTemplate;
            active.Control.SetGrid(_showGrid, _showMinor);
            // Synchronizuj stan ToggleButtonów symetrii z aktywną zakładką
            if (FindName("SymVToggle") is System.Windows.Controls.Primitives.ToggleButton symV)
                symV.IsChecked = active.Control.SymmetryV;
            if (FindName("SymHToggle") is System.Windows.Controls.Primitives.ToggleButton symH)
                symH.IsChecked = active.Control.SymmetryH;
            TabContent.Content         = active.Control;
            TabContent.Visibility      = Visibility.Visible;
            AnimSettingsPanel.Visibility    = Visibility.Collapsed;
            DrawingSettingsPanel.Visibility = Visibility.Visible;
            CodeSection.Visibility        = Visibility.Visible;
            CodeActionPanel.Visibility    = Visibility.Visible;
            SaveTemplatePanel.Visibility  = Visibility.Visible;
            int z = active.Control.CurrentZoom;
            ZoomLabel.Text       = $"{z}×";
            CanvasSizeLabel.Text = $"{s.W * z}×{s.H * z} px";
            if (_tool == DrawTool.Pencil || _tool == DrawTool.Eraser)
                SyncBrushToActiveTab();
            RefreshCode();
            StatusLabel.Text = s.VarName;
        }
    }

    void CloseTab(int idx)
    {
        if (idx < 0 || idx >= _tabs.Count) return;
        var entry = _tabs[idx];
        TabBar.Children.Remove(entry.Tab);
        if (entry.Kind == TabKind.Eye)
            _eyeTabs.RemoveAll(x => x.Tab == entry.Tab);
        _tabs.RemoveAt(idx);
        _activeTabIndex = -1;
        if (_tabs.Count == 0) { ShowEmptyState(); return; }
        ActivateTab(Math.Min(idx, _tabs.Count - 1));
    }

    void ShowEmptyState()
    {
        CanvasScrollBorder.Visibility  = Visibility.Collapsed;
        TabContent.Visibility          = Visibility.Collapsed;
        EmptyStatePanel.Visibility     = Visibility.Visible;
        ToolOptionsSection.Visibility  = Visibility.Collapsed;
        CodeSection.Visibility         = Visibility.Collapsed;
        CodeActionPanel.Visibility     = Visibility.Collapsed;
        SaveTemplatePanel.Visibility   = Visibility.Collapsed;
        AnimSettingsPanel.Visibility    = Visibility.Collapsed;
        DrawingSettingsPanel.Visibility = Visibility.Collapsed;
        HeaderSizeLabel.Text = "";
        StatusLabel.Text = "Brak rysunków — kliknij ＋ aby dodać nowy";
    }

    static void UpdateTabLabel(TabEntry entry, string text)
    {
        if (entry.Tab.Child is StackPanel sp && sp.Children[0] is TextBlock lbl)
            lbl.Text = text;
    }

    // ── Pomocnik: pokaż popup obok przycisku bez skoku pozycji ───────────────
    static void ShowPickerPopup(Window popup, Button anchorBtn)
    {
        // Pobierz skalę DPI (fizyczne px / logiczne px)
        var src = PresentationSource.FromVisual(anchorBtn);
        double dpiScaleX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiScaleY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // PointToScreen → fizyczne px → podziel → logiczne jednostki WPF
        var physBL = anchorBtn.PointToScreen(new System.Windows.Point(0, anchorBtn.ActualHeight));
        double logLeft = physBL.X / dpiScaleX;
        double logTop  = physBL.Y / dpiScaleY;

        // SystemParameters.WorkArea jest już w LOGICZNYCH px — nie dzielimy przez DPI
        double workLeft   = SystemParameters.WorkArea.Left;
        double workTop    = SystemParameters.WorkArea.Top;
        double workRight  = SystemParameters.WorkArea.Right;
        double workBottom = SystemParameters.WorkArea.Bottom;

        popup.Opacity               = 0;
        popup.Left                  = logLeft;
        popup.Top                   = logTop;
        popup.WindowStartupLocation = WindowStartupLocation.Manual;
        popup.Show();

        popup.ContentRendered += (_, _) =>
        {
            double left = logLeft;
            double top  = logTop;

            // Nie mieści się w dół — pokaż nad przyciskiem
            if (top + popup.ActualHeight > workBottom)
            {
                var physTL = anchorBtn.PointToScreen(new System.Windows.Point(0, 0));
                top = physTL.Y / dpiScaleY - popup.ActualHeight;
            }

            left = Math.Max(workLeft, Math.Min(left, workRight  - popup.ActualWidth));
            top  = Math.Max(workTop,  Math.Min(top,  workBottom - popup.ActualHeight));

            popup.Left    = left;
            popup.Top     = top;
            popup.Opacity = 1;
        };
    }

    private void EyePickerBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new EyePickerPopup();
        popup.TemplateSelected += t => ActivateEyeStamp(t);
        ShowPickerPopup(popup, (Button)sender);
    }

    private void MouthPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new MouthPickerPopup();
        popup.TemplateSelected += t => ActivateMouthStamp(t);
        ShowPickerPopup(popup, (Button)sender);
    }

    private void OtherPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new OtherPickerPopup();
        popup.TemplateSelected += t => ActivateOtherStamp(t);
        ShowPickerPopup(popup, (Button)sender);
    }

    private void BitmapPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new BitmapPickerPopup();
        popup.TemplateSelected += t => ActivateBitmapStamp(t);
        ShowPickerPopup(popup, (Button)sender);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WYBÓR NARZĘDZIA
    // ════════════════════════════════════════════════════════════════════════

    // ── Pomocnik: otwórz ContextMenu na RadioButton ───────────────────────
    private static void OpenToolContextMenu(object sender)
    {
        if (sender is RadioButton rb && rb.ContextMenu != null)
        {
            rb.ContextMenu.PlacementTarget = rb;
            rb.ContextMenu.IsOpen = true;
        }
    }

    // ── Pomocnik: aktywuj narzędzie wybrane z menu + sync + hint ─────────
    private void ApplyToolFromMenu(DrawTool tool, RadioButton btn,
        string icon, double fontSize, string tooltip, string hint)
    {
        _tool = tool;
        btn.Tag     = tool.ToString();
        btn.ToolTip = tooltip;
        if (btn.Content is TextBlock tb) { tb.Text = icon; tb.FontSize = fontSize; }
        btn.IsChecked = true;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var t = _tabs[_activeTabIndex];
            if (t.Control     != null) t.Control.ActiveTool     = _tool;
            else if (t.AnimControl != null) t.AnimControl.ActiveTool = _tool;
        }
        UpdateToolOptionsPanel();
        if (_ready) ToolHintLabel.Text = hint;
    }

    // ── LINIA ─────────────────────────────────────────────────────────────
    private void LineToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void LineMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool switch
        {
            DrawTool.HLine => ("—", 14d, "Linia pozioma · PPM = więcej opcji", "— Linia pozioma"),
            DrawTool.VLine => ("│", 14d, "Linia pionowa · PPM = więcej opcji",  "│ Linia pionowa"),
            _              => ("╱", (double)FindResource("IconSizeLine"), "Linia prosta · PPM = więcej opcji", "╱ Linia prosta"),
        };
        ApplyToolFromMenu(tool, LineToolBtn, icon, fs, tip, hint);
    }

    // ── PROSTOKĄT ─────────────────────────────────────────────────────────
    private void RectToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void RectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool == DrawTool.FilledRect
            ? ("▬", (double)FindResource("IconSizeFilledRect"), "Prostokąt (wypełniony) · PPM = więcej opcji", "▬ Prostokąt — wypełniony")
            : ("▭", (double)FindResource("IconSizeRect"),       "Prostokąt (kontur) · PPM = więcej opcji",     "▭ Prostokąt — kontur");
        ApplyToolFromMenu(tool, RectToolBtn, icon, fs, tip, hint);
    }

    // ── ELIPSA ────────────────────────────────────────────────────────────
    private void EllipseToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void EllipseMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool == DrawTool.FilledEllipse
            ? ("●", (double)FindResource("IconSizeFilledEllipse"), "Elipsa (wypełniona) · PPM = więcej opcji", "● Elipsa — wypełniona")
            : ("○", (double)FindResource("IconSizeEllipse"),       "Elipsa (kontur) · PPM = więcej opcji",     "○ Elipsa — kontur");
        ApplyToolFromMenu(tool, EllipseToolBtn, icon, fs, tip, hint);
    }

    // ── TRÓJKĄT ────────────────────────────────────────────────────────────────
    private void TriangleToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void TriangleMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool == DrawTool.FilledTriangle
            ? ("▲", 15d, "Trójkąt (wypełniony) · PPM = więcej opcji", "▲ Trójkąt — wypełniony")
            : ("△", 15d, "Trójkąt (kontur) · PPM = więcej opcji",     "△ Trójkąt — kontur");
        ApplyToolFromMenu(tool, TriangleToolBtn, icon, fs, tip, hint);
    }

    // ── ŁUKI ─────────────────────────────────────────────────────────────────────
    // ── KRZYŻ / GWIAZDA ───────────────────────────────────────────────────────────────
    private void CrossToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void CrossMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool == DrawTool.Star
            ? ("\u2726", 14d, "Gwiazdka \u00b7 PPM = wi\u0119cej opcji",    "\u2726 Gwiazdka")
            : ("\u271a", 14d, "Krzy\u017c / plus \u00b7 PPM = wi\u0119cej opcji", "\u271a Krzy\u017c / plus");
        ApplyToolFromMenu(tool, CrossToolBtn, icon, fs, tip, hint);
    }

    // \u2500\u2500 WYPE\u0141NIENIE / SZUM \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    private void FillToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void FillMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool == DrawTool.Scatter
            ? ("\u2744", 14d, "Szum \u2014 losowe piksele \u00b7 PPM = wi\u0119cej opcji", "\u2744 Szum \u2014 losowe piksele")
            : ("\ud83e\udea3", (double)FindResource("IconSizeFill"), "Wype\u0142nienie \u00b7 PPM = szum", "\ud83e\udea3 Wype\u0142nienie");
        ApplyToolFromMenu(tool, FillToolBtn, icon, fs, tip, hint);
    }

    // \u2500\u2500 \u0141UKI \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    private void ArcToolBtn_RightClick(object sender, MouseButtonEventArgs e)
    { OpenToolContextMenu(sender); e.Handled = true; }

    private void ArcMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<DrawTool>(tag, out var tool)) return;
        (string icon, double fs, string tip, string hint) = tool switch
        {
            DrawTool.ArcUp  => ("⌢", 16d, "Łuk górny · PPM = więcej opcji",           "⌢ Łuk górny"),
            DrawTool.Tongue => ("∪", 16d, "Język / usta otwarte · PPM = więcej opcji", "∪ Język / usta otwarte"),
            _               => ("⌣", 16d, "Łuk dolny · PPM = więcej opcji",            "⌣ Łuk dolny"),
        };
        ApplyToolFromMenu(tool, ArcToolBtn, icon, fs, tip, hint);
    }

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            if (Enum.TryParse<DrawTool>(tag, out var t))
                _tool = t;

        // ── Auto-wybór / przywrócenie szablonu stempla ──
        if (_tool == DrawTool.EyeStamp)
        {
            var t = _activeEyeTemplate ?? EyeLibrary.Instance.Templates.FirstOrDefault();
            if (t != null) { ActivateEyeStamp(t); return; }
        }
        else if (_tool == DrawTool.MouthStamp)
        {
            var t = _activeMouthTemplate ?? MouthLibrary.Instance.Templates.FirstOrDefault();
            if (t != null) { ActivateMouthStamp(t); return; }
        }
        else if (_tool == DrawTool.OtherStamp)
        {
            var t = _activeOtherTemplate ?? OtherLibrary.Instance.Templates.FirstOrDefault();
            if (t != null) { ActivateOtherStamp(t); return; }
        }
        else if (_tool == DrawTool.BitmapStamp)
        {
            var t = _activeBitmapTemplate ?? BitmapLibrary.Instance.Templates.FirstOrDefault();
            if (t != null) { ActivateBitmapStamp(t); return; }
        }

        // Sync narzędzia do aktywnej zakładki edytora
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var activeTab = _tabs[_activeTabIndex];
            if (activeTab.Control != null)
                activeTab.Control.ActiveTool = _tool;
            else if (activeTab.AnimControl != null)
                activeTab.AnimControl.ActiveTool = _tool;
        }

        // ── Panel opcji narzędzia — zawsze aktualizuj, niezależnie od _ready ──
        if (TextToolOptions == null) return; // kontrolki XAML jeszcze niezainicjowane
        UpdateToolOptionsPanel();

        if (!_ready) return;

        ToolHintLabel.Text = _tool switch
        {
            DrawTool.Select        => "⬚ Zaznaczanie — zaznacz prostokąt LPM, przeciągnij przenieś · PPM / klik poza = scal · Esc = anuluj",
            DrawTool.Pencil        => "✏ Ołówek — LPM rysuj | PPM gumka",
            DrawTool.Eraser        => "◻ Gumka — LPM czyść | PPM rysuj",
            DrawTool.FloodFill     => "◉ Wypełnienie — kliknij obszar",
            DrawTool.Line          => "╱ Linia — przeciągnij od A do B",
            DrawTool.Rect          => "▭ Prostokąt — kontur",
            DrawTool.FilledRect    => "▬ Prostokąt — wypełniony",
            DrawTool.Ellipse       => "○ Elipsa — kontur",
            DrawTool.FilledEllipse => "● Elipsa — wypełniona",
            DrawTool.ArcDown       => "⌣ Łuk dolny",
            DrawTool.ArcUp         => "⌢ Łuk górny",
            DrawTool.Tongue        => "∪ Język / usta otwarte",
            DrawTool.Triangle      => "△ Trójkąt",
            DrawTool.FilledTriangle => "▲ Trójkąt (wypełniony)",
            DrawTool.Cross         => "✚ Krzyż / plus",
            DrawTool.Eye           => "👁 Oko — oval + źrenica",
            DrawTool.EyeStamp      => $"👁 Stempel: {_activePixelTemplate?.Name ?? "brak"} — przeciągnij aby wkleić",
            DrawTool.MouthStamp    => $"😊 Usta: {_activePixelTemplate?.Name ?? "brak"} — przeciągnij aby wkleić",
            DrawTool.OtherStamp    => $"★ Inne: {_activePixelTemplate?.Name ?? "brak"} — przeciągnij aby wkleić",
            DrawTool.BitmapStamp   => $"🖼 Bitmap: {_activePixelTemplate?.Name ?? "brak"} — przeciągnij aby wkleić",
            DrawTool.Star          => "✦ Gwiazdka",
            DrawTool.Scatter       => "❄ Szum — losowe piksele",
            DrawTool.Text          => "T Tekst — kliknij na kanwę, wpisz tekst, Enter lub klik poza = zatwierdź",
            DrawTool.HLine         => "— Linia pozioma — kliknij aby narysować przez całą szerokość",
            DrawTool.VLine         => "│ Linia pionowa — kliknij aby narysować przez całą wysokość",
            _ => "Gotowy"
        };

        StatusLabel.Text = $"Narzędzie: {_tool}";

        // Przekaż aktualne opcje tekstu do aktywnego canvasu
        if (_tool == DrawTool.Text) SyncTextOptionsToCanvas();

        // Przekaż aktualny rozmiar i kształt pędzla z ustawień aktywnej karty
        if (_tool == DrawTool.Pencil || _tool == DrawTool.Eraser)
            SyncBrushToActiveTab();
    }

    void SyncBrushToActiveTab()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var s    = _tabs[_activeTabIndex].Settings;
        var ctrl = _tabs[_activeTabIndex].Control;
        var anim = _tabs[_activeTabIndex].AnimControl;
        ctrl?.SetBrushSize(s.BrushSize);
        ctrl?.SetBrushShape(s.BrushShape);
        anim?.SetBrushSize(s.BrushSize);
        anim?.SetBrushShape(s.BrushShape);
        // Zaktualizuj UI suwaka i wyboru kształtu
        if (_tool == DrawTool.Pencil)
        {
            PencilSizeSlider.Value         = s.BrushSize;
            BrushShapeSquare.IsChecked     = s.BrushShape == BrushShape.Square;
            BrushShapeCircle.IsChecked     = s.BrushShape == BrushShape.Circle;
        }
        else
        {
            EraserSizeSlider.Value         = s.BrushSize;
            EraserShapeSquare.IsChecked    = s.BrushShape == BrushShape.Square;
            EraserShapeCircle.IsChecked    = s.BrushShape == BrushShape.Circle;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  OŚ SYMETRII + ODBICIA
    // ════════════════════════════════════════════════════════════════════════

    PixelCanvasControl? ActiveCanvas =>
        (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
            ? _tabs[_activeTabIndex].Control
            : null;

    private void SymV_Checked(object sender, RoutedEventArgs e)
    {
        if (ActiveCanvas != null) ActiveCanvas.SymmetryV = true;
        else ActiveAnim?.SetSymmetry(true,  FindName("SymHToggle") is System.Windows.Controls.Primitives.ToggleButton th && th.IsChecked == true);
    }
    private void SymV_Unchecked(object sender, RoutedEventArgs e)
    {
        if (ActiveCanvas != null) ActiveCanvas.SymmetryV = false;
        else ActiveAnim?.SetSymmetry(false, FindName("SymHToggle") is System.Windows.Controls.Primitives.ToggleButton th && th.IsChecked == true);
    }
    private void SymH_Checked(object sender, RoutedEventArgs e)
    {
        if (ActiveCanvas != null) ActiveCanvas.SymmetryH = true;
        else ActiveAnim?.SetSymmetry(FindName("SymVToggle") is System.Windows.Controls.Primitives.ToggleButton tv && tv.IsChecked == true, true);
    }
    private void SymH_Unchecked(object sender, RoutedEventArgs e)
    {
        if (ActiveCanvas != null) ActiveCanvas.SymmetryH = false;
        else ActiveAnim?.SetSymmetry(FindName("SymVToggle") is System.Windows.Controls.Primitives.ToggleButton tv && tv.IsChecked == true, false);
    }

    private void FlipH_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
        if (tab?.Control != null) tab.Control.ApplyFlipH();
        else tab?.AnimControl?.FlipH();
    }
    private void FlipV_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
        if (tab?.Control != null) tab.Control.ApplyFlipV();
        else tab?.AnimControl?.FlipV();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  OPCJE NARZĘDZI
    // ════════════════════════════════════════════════════════════════════════
    void UpdateToolOptionsPanel()
    {
        bool isAnim = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
                      && _tabs[_activeTabIndex].Kind == TabKind.Animation;
        bool isSelect = _tool == DrawTool.Select;

        bool anyToolOptions = _tool is DrawTool.Text or DrawTool.Pencil or DrawTool.Eraser
                              || (isSelect && isAnim);
        TextToolOptions.Visibility      = _tool == DrawTool.Text    ? Visibility.Visible : Visibility.Collapsed;
        PencilToolOptions.Visibility    = _tool == DrawTool.Pencil  ? Visibility.Visible : Visibility.Collapsed;
        EraserToolOptions.Visibility    = _tool == DrawTool.Eraser  ? Visibility.Visible : Visibility.Collapsed;
        SelectionToolOptions.Visibility = (isSelect && isAnim)      ? Visibility.Visible : Visibility.Collapsed;
        ToolOptionsSection.Visibility   = anyToolOptions ? Visibility.Visible : Visibility.Collapsed;
        if (anyToolOptions)
            ToolOptionsSectionTitle.Text = _tool switch
            {
                DrawTool.Text   => "T  OPCJE TEKSTU",
                DrawTool.Pencil => "✏  OPCJE OŁÓWKA",
                DrawTool.Eraser => "◻  OPCJE GUMKI",
                DrawTool.Select => "⬚  OPCJE ZAZNACZANIA",
                _               => "OPCJE NARZĘDZIA"
            };
    }

    private void SyncSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _tabs == null || _activeTabIndex < 0) return;
        var anim = _tabs[_activeTabIndex].AnimControl;
        if (anim != null)
            anim.SyncFrames = SyncSelectionChk.IsChecked == true;
    }

    private void TextOption_Changed(object sender, RoutedEventArgs e)
    {
        SyncTextOptionsToCanvas();
    }

    private void SyncTextOptionsToCanvas()
    {
        if (!_ready || _tabs == null) return;
        string font   = (FontFamilyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Consolas";
        double size   = double.TryParse((FontSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out double s) ? s : 8;
        bool   bold   = BoldBtn.IsChecked == true;
        bool   italic = ItalicBtn.IsChecked == true;
        bool   white  = ColorWhite.IsChecked == true;

        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].Control?.SetTextOptions(font, size, bold, italic, white);
            _tabs[_activeTabIndex].AnimControl?.SetTextOptions(font, size, bold, italic, white);
        }
    }

    private void PencilSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PencilSizeLabel == null) return;
        int size = (int)e.NewValue;
        PencilSizeLabel.Text = $"{size} px";
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].Settings.BrushSize = size;
            _tabs[_activeTabIndex].Control?.SetBrushSize(size);
            _tabs[_activeTabIndex].AnimControl?.SetBrushSize(size);
        }
    }

    private void EraserSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EraserSizeLabel == null) return;
        int size = (int)e.NewValue;
        EraserSizeLabel.Text = $"{size} px";
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].Settings.BrushSize = size;
            _tabs[_activeTabIndex].Control?.SetBrushSize(size);
            _tabs[_activeTabIndex].AnimControl?.SetBrushSize(size);
        }
    }

    private void BrushShape_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        BrushShape shape;
        if (_tool == DrawTool.Eraser)
            shape = (EraserShapeSquare?.IsChecked == true) ? BrushShape.Square : BrushShape.Circle;
        else
            shape = (BrushShapeSquare?.IsChecked == true) ? BrushShape.Square : BrushShape.Circle;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            _tabs[_activeTabIndex].Settings.BrushShape = shape;
            _tabs[_activeTabIndex].Control?.SetBrushShape(shape);
            _tabs[_activeTabIndex].AnimControl?.SetBrushShape(shape);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SIATKA
    // ════════════════════════════════════════════════════════════════════════
    private void Grid_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _showGrid  = ShowGridChk.IsChecked  == true;
        _showMinor = ShowMinorChk.IsChecked == true;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            if (_tabs[_activeTabIndex].Control is PixelCanvasControl gc)
                gc.SetGrid(_showGrid, _showMinor);
            else
                _tabs[_activeTabIndex].AnimControl?.SetGrid(_showGrid, _showMinor);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  KLAWIATURA
    // ════════════════════════════════════════════════════════════════════════
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // ── Zaznaczenie: Delete / Ctrl+C / Ctrl+V ──────────────────────────
        PixelCanvasControl? activeCanvas = null;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            if (tab.Control is PixelCanvasControl ec2) activeCanvas = ec2;
            else if (tab.AnimControl?.ActiveEditorControl is PixelCanvasControl aec) activeCanvas = aec;
        }

        if (activeCanvas != null && activeCanvas.HasActiveSelection)
        {
            if (e.Key == Key.Delete)
            {
                activeCanvas.DeleteSelection();
                StatusLabel.Text = "Zaznaczenie usunięte";
                e.Handled = true; return;
            }
            if (ctrl && e.Key == Key.C)
            {
                activeCanvas.CopySelection();
                // zapisz też do globalnego schowka (dostępny między zakładkami)
                if (activeCanvas.HasActiveSelection)
                    _globalClipboard = activeCanvas.GetSelectionPixels();
                StatusLabel.Text = "Skopiowano zaznaczenie";
                e.Handled = true; return;
            }
        }
        if (ctrl && e.Key == Key.V && activeCanvas != null)
        {
            if (_globalClipboard != null)
                activeCanvas.PasteExternal(_globalClipboard);
            else
                activeCanvas.PasteFromClipboard();
            StatusLabel.Text = "Wklejono zaznaczenie";
            e.Handled = true; return;
        }

        // ── Standardowe skróty ──────────────────────────────────────────────
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.T) { NewDrawingTab_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape && activeCanvas != null && activeCanvas.HasActiveSelection)
            { activeCanvas.CancelSelection(); e.Handled = true; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  UNDO / REDO
    // ════════════════════════════════════════════════════════════════════════
    void SaveUndo()
    {
        var snap = new bool[H, W];
        Array.Copy(_pixels, snap, _pixels.Length);
        _undo.Push(snap);
        _redo.Clear();
    }

    void Undo()
    {
        var tab = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
        if (tab?.Control is PixelCanvasControl ec)   { ec.Undo();   StatusLabel.Text = "Cofnięto"; return; }
        if (tab?.AnimControl is AnimationEditorControl anim) { anim.Undo(); StatusLabel.Text = "Cofnięto"; return; }
        StatusLabel.Text = "Nic do cofnięcia";
    }

    void Redo()
    {
        var tab = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
        if (tab?.Control is PixelCanvasControl ec)   { ec.Redo();   StatusLabel.Text = "Ponowiono"; return; }
        if (tab?.AnimControl is AnimationEditorControl anim) { anim.Redo(); StatusLabel.Text = "Ponowiono"; return; }
        StatusLabel.Text = "Nic do ponowienia";
    }

    private void Undo_Click(object  sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object  sender, RoutedEventArgs e) => Redo();

    // ════════════════════════════════════════════════════════════════════════
    //  WYCZYŚĆ / ODWRÓĆ
    // ════════════════════════════════════════════════════════════════════════
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
        if (tab?.Control is PixelCanvasControl ec)   { ec.Clear();   StatusLabel.Text = "Canvas wyczyszczony"; return; }
        if (tab?.AnimControl is AnimationEditorControl anim) { anim.Clear(); StatusLabel.Text = "Canvas wyczyszczony"; return; }
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTabIndex >= 0 && _activeTabIndex < _tabs.Count ? _tabs[_activeTabIndex] : null;
        if (tab?.Control is PixelCanvasControl ec)   { ec.Invert();   StatusLabel.Text = "Piksele odwrócone"; return; }
        if (tab?.AnimControl is AnimationEditorControl anim) { anim.Invert(); StatusLabel.Text = "Piksele odwrócone"; return; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ANIMACJA — PANEL PRAWY
    // ════════════════════════════════════════════════════════════════════════

    AnimationEditorControl? ActiveAnim =>
        (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count &&
         _tabs[_activeTabIndex].Kind == TabKind.Animation)
            ? _tabs[_activeTabIndex].AnimControl
            : null;

    private void AnimOledCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var anim = ActiveAnim;
        if (anim == null) return;
        var item = AnimOledCombo.SelectedItem as ComboBoxItem;
        if (item?.Tag?.ToString() is not string tag) return;

        // Własny rozmiar — pokaż panel i zakończ
        if (tag == "custom")
        {
            AnimCustomSizePanel.Visibility = Visibility.Visible;
            AnimCustomWidthBox.Text  = anim.FrameWidth.ToString();
            AnimCustomHeightBox.Text = anim.FrameHeight.ToString();
            return;
        }

        AnimCustomSizePanel.Visibility = Visibility.Collapsed;

        var parts = tag.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int nw) || !int.TryParse(parts[1], out int nh)) return;
        ApplyAnimResize(anim, nw, nh, tag);
    }

    private void AnimCustomSize_Apply_Click(object sender, RoutedEventArgs e)
    {
        var anim = ActiveAnim;
        if (anim == null) return;
        if (!int.TryParse(AnimCustomWidthBox.Text.Trim(),  out int nw) || nw < 8 || nw > 512)
            { StatusLabel.Text = "Nieprawidłowa szerokość (8–512)"; return; }
        if (!int.TryParse(AnimCustomHeightBox.Text.Trim(), out int nh) || nh < 8 || nh > 512)
            { StatusLabel.Text = "Nieprawidłowa wysokość (8–512)"; return; }
        ApplyAnimResize(anim, nw, nh, $"{nw}x{nh}");
    }

    void ApplyAnimResize(AnimationEditorControl anim, int nw, int nh, string tag)
    {
        if (nw == anim.FrameWidth && nh == anim.FrameHeight) return;

        var res = MessageBox.Show(
            $"Zmiana rozmiaru ekranu z {anim.FrameWidth}×{anim.FrameHeight} na {nw}×{nh}\n\n" +
            "Piksele we wszystkich klatkach zostaną przycięte lub uzupełnione zerami.\n\n" +
            "Czy na pewno chcesz zmienić rozmiar?",
            "Zmiana rozmiaru animacji",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes)
        {
            // Cofnij wybór combo
            _ready = false;
            string oldTag = $"{anim.FrameWidth}x{anim.FrameHeight}";
            bool found = false;
            foreach (ComboBoxItem ci in AnimOledCombo.Items)
                if (ci.Tag?.ToString() == oldTag) { AnimOledCombo.SelectedItem = ci; found = true; break; }
            if (!found)
                foreach (ComboBoxItem ci in AnimOledCombo.Items)
                    if (ci.Tag?.ToString() == "custom") { AnimOledCombo.SelectedItem = ci; break; }
            _ready = true;
            return;
        }

        anim.ResizeAllFrames(nw, nh);
        var tab = _tabs[_activeTabIndex];
        tab.Settings.W = nw;
        tab.Settings.H = nh;
        tab.Settings.OledTag = tag;
        HeaderSizeLabel.Text = $" · {nw}×{nh} px";
        StatusLabel.Text = $"🎬 Zmieniono rozmiar animacji na {nw}×{nh}";
    }

    private void AnimName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var anim = ActiveAnim;
        if (anim == null) return;
        string nm = AnimNameBox.Text.Trim();
        if (nm.Length == 0) return;
        var tab = _tabs[_activeTabIndex];
        tab.Settings.VarName = nm;
        anim.SetBaseName(nm);
        UpdateTabLabel(tab, $"🎬 {nm}");
    }

    private void AnimExportFromPanel_Click(object sender, RoutedEventArgs e)
    {
        ActiveAnim?.TriggerExport();
    }

    // ── Sprite handlers ──────────────────────────────────────────────────────

    private void SpriteName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var sp = ActiveSprite;
        if (sp == null) return;
        sp.MotionSettings.SpriteName = SpriteNameBox.Text.Trim();
        var tab = _tabs[_activeTabIndex];
        tab.Settings.VarName = sp.MotionSettings.SpriteName;
        VarNameBox.Text = tab.Settings.VarName;
        StatusLabel.Text = tab.Settings.VarName;
    }

    private void SpriteMotion_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var sp = ActiveSprite;
        if (sp == null) return;
        var ms = sp.MotionSettings;
        if (int.TryParse(SpriteStartXBox.Text, out int sx)) ms.StartX = sx;
        if (int.TryParse(SpriteStartYBox.Text, out int sy)) ms.StartY = sy;
        if (int.TryParse(SpriteEndXBox.Text,   out int ex)) ms.EndX   = ex;
        if (int.TryParse(SpriteEndYBox.Text,   out int ey)) ms.EndY   = ey;
        if (int.TryParse(SpriteDelayBox.Text,  out int d))  ms.DelayMs = d;
    }

    private void SpriteLoop_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var sp = ActiveSprite;
        if (sp == null) return;
        if (SpriteLoopOnce.IsChecked  == true) sp.MotionSettings.LoopMode = Models.SpriteLoopMode.Once;
        else if (SpriteLoopLoop.IsChecked  == true) sp.MotionSettings.LoopMode = Models.SpriteLoopMode.Loop;
        else if (SpriteLoopBounce.IsChecked == true) sp.MotionSettings.LoopMode = Models.SpriteLoopMode.Bounce;
    }

    private void SpriteExportFromPanel_Click(object sender, RoutedEventArgs e)
    {
        ActiveSprite?.TriggerExport();
    }

    // ── Tween handler ────────────────────────────────────────────────────────

    private void TweenGenerate_Click(object sender, RoutedEventArgs e)
    {
        var anim = ActiveAnim;
        if (anim == null) return;
        if (!int.TryParse(TweenFramesBox.Text, out int count) || count < 1) return;

        int fromIdx = TweenFromCombo.SelectedIndex;
        int toIdx   = TweenToCombo.SelectedIndex;
        if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;

        double EasingFn(double t)
        {
            if (TweenEaseIn.IsChecked   == true) return t * t;
            if (TweenEaseOut.IsChecked  == true) return t * (2 - t);
            if (TweenEaseBoth.IsChecked == true) return t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
            return t; // linear
        }

        TweenMorphMode morphMode = TweenMorphMode.TranslateScale;
        if      (TweenMorphDissolve.IsChecked   == true) morphMode = TweenMorphMode.Dissolve;
        else if (TweenMorphTranslate.IsChecked  == true) morphMode = TweenMorphMode.Translate;
        else if (TweenMorphScale.IsChecked      == true) morphMode = TweenMorphMode.Scale;
        else if (TweenMorphTransScale.IsChecked == true) morphMode = TweenMorphMode.TranslateScale;

        anim.InsertTweenFrames(fromIdx, toIdx, count, EasingFn, morphMode);

        // Combo odświeżane automatycznie przez FrameCountChanged
    }

    void PopulateTweenCombos(AnimationEditorControl anim)
    {
        int n = anim.FrameCount;

        // Nie przebudowuj gdy liczba klatek się nie zmieniła
        if (TweenFromCombo.Items.Count == n && TweenToCombo.Items.Count == n)
            return;

        int prevFrom = TweenFromCombo.SelectedIndex;
        int prevTo   = TweenToCombo.SelectedIndex;

        // BeginInit tłumi SelectionChanged podczas Clear()/Add()
        TweenFromCombo.BeginInit();
        TweenToCombo.BeginInit();

        TweenFromCombo.Items.Clear();
        TweenToCombo.Items.Clear();
        for (int i = 0; i < n; i++)
        {
            TweenFromCombo.Items.Add(new ComboBoxItem { Content = $"Klatka {i + 1}" });
            TweenToCombo.Items.Add(new ComboBoxItem   { Content = $"Klatka {i + 1}" });
        }

        TweenFromCombo.EndInit();
        TweenToCombo.EndInit();

        TweenFromCombo.SelectedIndex = prevFrom < 0 ? 0                : Math.Min(prevFrom, n - 1);
        TweenToCombo.SelectedIndex   = prevTo   < 0 ? Math.Min(1, n-1) : Math.Min(prevTo,   n - 1);
    }

    void Anim_FramePreview(bool[,] pixels, int w, int h) { /* podgląd obsługiwany wewnątrz AnimationEditorControl */ }
    void Anim_PlaybackStopped(int frameIdx)              { /* SelectFrame wywoływane wewnątrz AnimationEditorControl */ }

    // ════════════════════════════════════════════════════════════════════════
    //  GENEROWANIE KODU C  (MSB-first, PROGMEM)
    // ════════════════════════════════════════════════════════════════════════
    void RefreshCode()
    {
        string nm = VarNameBox.Text.Trim();
        if (nm.Length == 0) nm = "myBitmap";

        // Wybierz piksele z aktywnej karty
        bool[,] pixels;
        int cw, ch;
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Control is PixelCanvasControl ctrl)
        {
            pixels = ctrl.CurrentPixels;
            cw = ctrl.Template.Width;
            ch = ctrl.Template.Height;
        }
        else
        {
            return; // brak aktywnej karty z canvasem
        }

        int bytesPerRow = (cw + 7) / 8;
        int totalBytes  = bytesPerRow * ch;
        var sb = new StringBuilder(totalBytes * 6 + 512);
        sb.AppendLine($"// OLED {cw}x{ch} — wygenerowane przez OLED Paint Pro");
        sb.AppendLine($"// Format: MSB-first, {bytesPerRow} bajtów/wiersz × {ch} wiersze = {totalBytes} bajty");
        sb.AppendLine($"// Użycie: u8g2.drawBitmap(0, 0, {bytesPerRow}, {ch}, {nm});");
        sb.AppendLine($"//         u8g2.drawXBMP(0, 0, {cw}, {ch}, {nm}); // LSB — odwrócone bity");
        sb.AppendLine();
        sb.AppendLine($"const unsigned char PROGMEM {nm}[] = {{");

        for (int row = 0; row < ch; row++)
        {
            sb.Append("  ");
            for (int byteIdx = 0; byteIdx < bytesPerRow; byteIdx++)
            {
                byte val = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int col = byteIdx * 8 + bit;
                    if (col < cw && pixels[row, col])
                        val |= (byte)(0x80 >> bit);  // MSB-first
                }
                sb.Append($"0x{val:X2}");
                bool isLast = (row == ch - 1) && (byteIdx == bytesPerRow - 1);
                if (!isLast) sb.Append(", ");
            }
            sb.AppendLine();
        }
        sb.Append("};");
        CodeOutput.Text = sb.ToString();
    }

    private void VarName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        // Zaktualizuj nazwę aktywnej karty (jeśli nie Main)
        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count)
        {
            var tab = _tabs[_activeTabIndex];
            tab.Settings.VarName = VarNameBox.Text;
            if (tab.Kind != TabKind.Animation)
            {
                if (tab.Template != null) tab.Template.Name = VarNameBox.Text;
                UpdateTabLabel(tab, VarNameBox.Text);
            }
        }
        RefreshCode();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  KOPIUJ / ZAPISZ
    // ════════════════════════════════════════════════════════════════════════
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeOutput.Text);
            StatusLabel.Text = "✔ Skopiowano kod C do schowka!";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Błąd schowka: {ex.Message}";
        }
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        string nm = VarNameBox.Text.Trim();
        if (nm.Length == 0) nm = "myBitmap";

        var dlg = new SaveFileDialog
        {
            Title    = "Zapisz bitmapę jako plik nagłówkowy C",
            Filter   = "Nagłówek C (*.h)|*.h|Plik tekstowy (*.txt)|*.txt|Wszystkie|*.*",
            FileName = nm,
        };

        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, CodeOutput.Text, Encoding.UTF8);
            StatusLabel.Text = $"✔ Zapisano: {dlg.FileName}";
        }
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabs.Count) return;
        var tab = _tabs[_activeTabIndex];
        if (tab.Control == null) return;
        // Aktualizuj piksele szablonu z aktualnego canvasu
        tab.Control.Template.Pixels = tab.Control.CurrentPixels;

        var dlgRes = ShowSaveCategoryDialog(tab.Control.Template.Name);
        if (dlgRes == null) return; // anulowano

        string cat = dlgRes.Category;
        bool saveAsNew = dlgRes.SaveAsNew;
        string finalName = dlgRes.NewName;

        if (cat == "eye")
        {
            if (saveAsNew)
            {
                // Zapisz jako nowy szablon — nowy Id
                var src = tab.Control.Template;
                var nw = new Models.PixelTemplate
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(finalName) ? src.Name : finalName,
                    Width = src.Width,
                    Height = src.Height,
                    Pixels = (bool[,])src.Pixels.Clone()
                };
                EyeLibrary.Instance.Save(nw);
                StatusLabel.Text = $"Zapisano jako nowe Oko: {nw.Name}";
            }
            else
            {
                EyeLibrary.Instance.Save(tab.Control.Template);
                StatusLabel.Text = $"Zapisano w Oczach: {tab.Control.Template.Name}";
            }
        }
        else if (cat == "mouth")
        {
            if (saveAsNew)
            {
                var src = tab.Control.Template;
                var nw = new Models.PixelTemplate
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(finalName) ? src.Name : finalName,
                    Width = src.Width,
                    Height = src.Height,
                    Pixels = (bool[,])src.Pixels.Clone()
                };
                MouthLibrary.Instance.Save(nw);
                StatusLabel.Text = $"Zapisano jako nowe Usta: {nw.Name}";
            }
            else
            {
                MouthLibrary.Instance.Save(tab.Control.Template);
                StatusLabel.Text = $"Zapisano w Ustach: {tab.Control.Template.Name}";
            }
        }
        else if (cat == "other")
        {
            if (saveAsNew)
            {
                var src = tab.Control.Template;
                var nw = new Models.PixelTemplate
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(finalName) ? src.Name : finalName,
                    Width = src.Width,
                    Height = src.Height,
                    Pixels = (bool[,])src.Pixels.Clone()
                };
                OtherLibrary.Instance.Save(nw);
                StatusLabel.Text = $"Zapisano jako nowe: {nw.Name}";
            }
            else
            {
                OtherLibrary.Instance.Save(tab.Control.Template);
                StatusLabel.Text = $"Zapisano w Inne: {tab.Control.Template.Name}";
            }
        }
        else if (cat == "bitmap")
        {
            if (saveAsNew)
            {
                var src = tab.Control.Template;
                var nw = new Models.PixelTemplate
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(finalName) ? src.Name : finalName,
                    Width = src.Width,
                    Height = src.Height,
                    Pixels = (bool[,])src.Pixels.Clone()
                };
                BitmapLibrary.Instance.Save(nw);
                StatusLabel.Text = $"Zapisano jako nową bitmapę: {nw.Name}";
            }
            else
            {
                tab.Control.Template.Pixels = tab.Control.CurrentPixels;
                BitmapLibrary.Instance.Save(tab.Control.Template);
                StatusLabel.Text = $"Zapisano bitmapę: {tab.Control.Template.Name}";
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ZAZNACZANIE (Select tool)
    // ════════════════════════════════════════════════════════════════════════
    void LiftSelection()
    {
        SaveUndo();
        if (!_sel.Lift(_pixels, W, H))
        {
            _undo.Pop();
            return;
        }
        _useWork = false;
        StatusLabel.Text = $"Zaznaczono {_sel.W}×{_sel.H} px — przeciągnij aby przenieść · Esc = anuluj";
    }

    void CommitSelection()
    {
        if (!_sel.IsActive) return;
        _sel.CommitTo(_pixels, W, H);
        _useWork = false;
        CanvasImage.Cursor = Cursors.Arrow;
        RenderCanvas();
        RefreshCode();
        StatusLabel.Text = "Zaznaczenie scalone";
    }

    void CancelSelection()
    {
        if (!_sel.IsActive) return;
        var snap = _sel.CancelAndGetSnapshot();
        if (snap != null && snap.GetLength(0) == H && snap.GetLength(1) == W)
            Array.Copy(snap, _pixels, _pixels.Length);
        _useWork = false;
        CanvasImage.Cursor = Cursors.Arrow;
        RenderCanvas();
        RefreshCode();
        StatusLabel.Text = "Zaznaczenie anulowane";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WYSUW / CHOWAJ PRAWY PANEL
    // ════════════════════════════════════════════════════════════════════════
    private void ToggleRightPanel_Click(object sender, RoutedEventArgs e)
    {
        _rightPanelCollapsed = !_rightPanelCollapsed;

        var arrow = ToggleRightPanelBtn.Template.FindName("Arrow", ToggleRightPanelBtn) as TextBlock;

        if (_rightPanelCollapsed)
        {
            RightPanelCol.Width         = new GridLength(0);
            RightPanelBorder.Visibility = Visibility.Collapsed;
            if (arrow != null) arrow.Text = "◀";
            ToggleRightPanelBtn.ToolTip  = "Rozwiń panel ustawień";
        }
        else
        {
            RightPanelCol.Width         = new GridLength(360);
            RightPanelBorder.Visibility = Visibility.Visible;
            if (arrow != null) arrow.Text = "▶";
            ToggleRightPanelBtn.ToolTip  = "Zwiń panel ustawień";
        }
    }

    private void CustomSize_Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CustomWidthBox.Text.Trim(),  out int nw) || nw < 8 || nw > 512) { StatusLabel.Text = "Nieprawidłowa szerokość (8–512)"; return; }
        if (!int.TryParse(CustomHeightBox.Text.Trim(), out int nh) || nh < 8 || nh > 512) { StatusLabel.Text = "Nieprawidłowa wysokość (8–512)"; return; }

        if (_activeTabIndex >= 0 && _activeTabIndex < _tabs.Count
            && _tabs[_activeTabIndex].Control is PixelCanvasControl ctrl)
        {
            ctrl.ResizeCanvas(nw, nh);
            _tabs[_activeTabIndex].Settings.W       = nw;
            _tabs[_activeTabIndex].Settings.H       = nh;
            _tabs[_activeTabIndex].Settings.OledTag = $"{nw}x{nh}";
            HeaderSizeLabel.Text = $" · {nw}×{nh} px";
            StatusLabel.Text = $"Rozmiar karty zmieniony na {nw}×{nh} px";
            return;
        }

        // Brak aktywnej karty z canvasem
    }

    // ════════════════════════════════════════════════════════════════════════
    //  WYBÓR KATEGORII SZABLONU
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Wynik dialogu zapisu: kategoria i czy zapisać jako nowy szablon.</summary>
    record SaveDialogResult(string Category, bool SaveAsNew, string NewName);

    SaveDialogResult? ShowSaveCategoryDialog(string currentName)
    {
        SaveDialogResult? result = null;
        bool saveAsNew = false;
        string newName = currentName;

        var win = new Window
        {
            Title                 = "Zapisz szablon",
            SizeToContent         = SizeToContent.WidthAndHeight,
            WindowStyle           = WindowStyle.ToolWindow,
            ResizeMode            = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Owner                 = Application.Current.MainWindow,
        };

        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int captionColor = ToCOLORREF(0x2A, 0x2A, 0x2A);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
        };

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };

        root.Children.Add(new TextBlock
        {
            Text       = "Gdzie zapisać szablon?",
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            FontSize   = 11,
            Margin     = new Thickness(0, 0, 0, 12),
        });

        // ── Toggle: zapisz jako nowy (ładniejszy wizualnie) ──────────────────
        var nameBox = new TextBox
        {
            Text            = currentName,
            Width           = 236,
            Height          = 28,
            FontSize        = 12,
            Padding         = new Thickness(6, 3, 6, 3),
            Background      = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            IsEnabled       = false,
            Margin          = new Thickness(0, 6, 0, 12),
        };
        nameBox.SelectAll();
        // Przy wyłączonym polu pokaż lekko przygaszony tekst
        nameBox.IsEnabledChanged += (_, _) =>
        {
            nameBox.Opacity = nameBox.IsEnabled ? 1.0 : 0.6;
            nameBox.Foreground = nameBox.IsEnabled ? new SolidColorBrush(Color.FromRgb(0xE0,0xE0,0xE0)) : new SolidColorBrush(Color.FromRgb(0x9A,0x9A,0x9A));
        };

        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = new TextBlock
            {
                Text = "Zapisz jako nowy szablon",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 236,
            Height = 28,
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 6)
        };

        // Visu: when checked, apply subtle accent border and darker bg so inner icon/text remains visible
        toggle.Checked += (_, _) =>
        {
            saveAsNew = true;
            nameBox.IsEnabled = true;
            toggle.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x2A, 0x23));
            toggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0xD0, 0x9A));
            ((TextBlock)toggle.Content).Foreground = new SolidColorBrush(Color.FromRgb(0xE8,0xFF,0xF0));
            nameBox.Focus(); nameBox.SelectAll();
        };
        toggle.Unchecked += (_, _) =>
        {
            saveAsNew = false;
            nameBox.IsEnabled = false;
            toggle.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            toggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            ((TextBlock)toggle.Content).Foreground = new SolidColorBrush(Color.FromRgb(0xD0,0xD0,0xD0));
        };
        nameBox.TextChanged += (_, _) => newName = nameBox.Text;

        root.Children.Add(toggle);
        root.Children.Add(nameBox);

        // ── 3 przyciski kategorii ─────────────────────────────────────────────
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        void AddBtn(string emoji, string label, string cat, Color accent)
        {
            var panel = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            panel.Children.Add(new TextBlock
            {
                Text                = emoji,
                FontSize            = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 4),
            });
            panel.Children.Add(new TextBlock
            {
                Text                = label,
                FontSize            = 11,
                Foreground          = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var btn = new Button
            {
                Content         = panel,
                Width           = 80,
                Height          = 64,
                Margin          = new Thickness(4, 0, 4, 0),
                Padding         = new Thickness(6),
                Background      = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Foreground      = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
            };
            btn.MouseEnter += (_, _) =>
            {
                btn.BorderBrush = new SolidColorBrush(accent);
                // subtle tint of accent for background so content remains readable
                btn.Background = new SolidColorBrush(Color.FromArgb(0x20, accent.R, accent.G, accent.B));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            };
            btn.MouseLeave += (_, _) =>
            {
                btn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B));
                btn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            };
            btn.Click += (_, _) =>
            {
                string finalName = saveAsNew ? newName.Trim() : currentName;
                if (string.IsNullOrWhiteSpace(finalName)) finalName = currentName;
                result = new SaveDialogResult(cat, saveAsNew, finalName);
                win.Close();
            };
            row.Children.Add(btn);
        }

        AddBtn("👁",  "Oczy",   "eye",    Color.FromRgb(0x28, 0xD0, 0x9A));
        AddBtn("😊", "Usta",   "mouth",  Color.FromRgb(0xFF, 0x88, 0x44));
        AddBtn("★",  "Inne",   "other",  Color.FromRgb(0xCC, 0x88, 0xFF));
        AddBtn("🖼",  "Bitmap", "bitmap", Color.FromRgb(0x44, 0xAA, 0xFF));

        root.Children.Add(row);
        win.Content = root;
        win.ShowDialog();
        return result;
    }
}
