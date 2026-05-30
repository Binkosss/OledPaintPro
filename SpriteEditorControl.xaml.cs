using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OledPaintPro.Models;

namespace OledPaintPro;

public partial class SpriteEditorControl : UserControl
{
    // ── Klatki sprite'a ──────────────────────────────────────────────────────
    readonly List<PixelTemplate> _frames = new();
    int  _currentFrame = -1;
    bool _loopPlay     = true;

    // ── Player ───────────────────────────────────────────────────────────────
    readonly DispatcherTimer _timer = new();
    bool _playing = false;

    // ── Wymiary klatki ───────────────────────────────────────────────────────
    int _awMutable, _ahMutable;
    int _aw => _awMutable;
    int _ah => _ahMutable;

    // ── Nazwa bazowa ─────────────────────────────────────────────────────────
    readonly string _baseName;
    string? _baseNameOverride;
    string EffectiveName => _baseNameOverride ?? _baseName;

    // ── Narzędzie / pędzel ───────────────────────────────────────────────────
    DrawTool   _activeTool  = DrawTool.Pencil;
    int        _brushSize   = 1;
    BrushShape _brushShape  = BrushShape.Circle;
    bool       _symV        = false;
    bool       _symH        = false;
    bool       _showGrid    = true;
    bool       _showMinor   = true;
    PixelTemplate? _stampTemplate = null;

    // ── Ustawienia ruchu ─────────────────────────────────────────────────────
    public SpriteMotionSettings MotionSettings { get; } = new();

    // ── Renderowanie ─────────────────────────────────────────────────────────
    readonly PixelRenderer _previewRenderer = new();

    // ── Eventy do MainWindow ─────────────────────────────────────────────────
    public event Action<string>? CoordChanged;
    public event Action<int>?    ZoomChanged;

    // ── Właściwości publiczne ─────────────────────────────────────────────────
    public int FrameCount  => _frames.Count;
    public int FrameWidth  => _aw;
    public int FrameHeight => _ah;

    public PixelCanvasControl? ActiveEditorControl =>
        EditorContainer.Child as PixelCanvasControl;

    public DrawTool ActiveTool
    {
        get => _activeTool;
        set { _activeTool = value; if (ActiveEditorControl != null) ActiveEditorControl.ActiveTool = value; }
    }

    // ════════════════════════════════════════════════════════════════════════
    public SpriteEditorControl(string baseName, int width, int height)
    {
        _baseName  = baseName;
        _awMutable = width;
        _ahMutable = height;

        MotionSettings.SpriteName   = baseName;
        MotionSettings.SpriteWidth  = width;
        MotionSettings.SpriteHeight = height;
        MotionSettings.ScreenWidth  = width  >= 128 ? width  : 128;
        MotionSettings.ScreenHeight = height >= 64  ? height : 64;

        InitializeComponent();

        _timer.Tick += Timer_Tick;
        SetFps(10);
        FpsSlider.Value = 10;

        AddNewFrame();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PUBLICZNE METODY — sync z MainWindow
    // ════════════════════════════════════════════════════════════════════════

    public void SetBrushSize(int size)  { _brushSize = size;  ActiveEditorControl?.SetBrushSize(size); }
    public void SetBrushShape(BrushShape shape) { _brushShape = shape; ActiveEditorControl?.SetBrushShape(shape); }
    public void SetTextOptions(string font, double size, bool bold, bool italic, bool white)
        => ActiveEditorControl?.SetTextOptions(font, size, bold, italic, white);
    public void SetSymmetry(bool symV, bool symH)
    {
        _symV = symV; _symH = symH;
        if (ActiveEditorControl != null) { ActiveEditorControl.SymmetryV = symV; ActiveEditorControl.SymmetryH = symH; }
    }
    public void SetGrid(bool showGrid, bool showMinor)
    {
        _showGrid = showGrid; _showMinor = showMinor;
        ActiveEditorControl?.SetGrid(showGrid, showMinor);
    }
    public void SetStampTemplate(PixelTemplate? tmpl)
    {
        _stampTemplate = tmpl;
        if (ActiveEditorControl != null) ActiveEditorControl.ActiveStampTemplate = tmpl;
    }
    public void SetBaseName(string name)
    {
        _baseNameOverride = name;
        MotionSettings.SpriteName = name;
    }

    public void Undo()   => ActiveEditorControl?.Undo();
    public void Redo()   => ActiveEditorControl?.Redo();
    public void Clear()  => ActiveEditorControl?.Clear();
    public void Invert() => ActiveEditorControl?.Invert();
    public void FlipH()  => ActiveEditorControl?.ApplyFlipH();
    public void FlipV()  => ActiveEditorControl?.ApplyFlipV();

    public void ZoomBy(int delta) => ActiveEditorControl?.ZoomBy(delta);

    public void TriggerExport() => Export_Click(this, new RoutedEventArgs());

    // ════════════════════════════════════════════════════════════════════════
    //  KLATKI
    // ════════════════════════════════════════════════════════════════════════

    void AddNewFrame(bool[,]? pixels = null)
    {
        int n = _frames.Count + 1;
        var tmpl = new PixelTemplate
        {
            Name   = $"{EffectiveName}_f{n:D3}",
            Width  = _aw,
            Height = _ah,
            Pixels = new bool[_ah, _aw],
        };
        if (pixels != null)
        {
            int rows = Math.Min(_ah, pixels.GetLength(0));
            int cols = Math.Min(_aw, pixels.GetLength(1));
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                tmpl.Pixels[r, c] = pixels[r, c];
        }
        _frames.Add(tmpl);
        AddFilmstripItem(_frames.Count - 1);
        SelectFrame(_frames.Count - 1);
        UpdateFrameCountLabel();
    }

    void SelectFrame(int idx)
    {
        if (idx < 0 || idx >= _frames.Count) return;
        if (_playing) StopPlay();

        // Zapisz poprzednią klatkę
        if (_currentFrame >= 0 && _currentFrame < _frames.Count
            && EditorContainer.Child is PixelCanvasControl prevCtrl)
        {
            if (prevCtrl.HasActiveSelection) prevCtrl.CommitSelection();
            var px = prevCtrl.CurrentPixels;
            var fr = _frames[_currentFrame];
            if (px.GetLength(0) == fr.Height && px.GetLength(1) == fr.Width)
                Array.Copy(px, fr.Pixels, px.Length);
            prevCtrl.FlushUndoToTemplate();
            UpdateFilmstripThumb(_currentFrame);
        }

        _currentFrame = idx;
        RefreshFilmstripHighlight();

        var ctrl = new PixelCanvasControl(_frames[idx]);
        ctrl.PixelsChanged += ec =>
        {
            var px = ec.CurrentPixels;
            var fr = ec.Template;
            if (px.GetLength(0) == fr.Height && px.GetLength(1) == fr.Width)
                Array.Copy(px, fr.Pixels, px.Length);
            int fi = _frames.IndexOf(fr);
            if (fi >= 0) UpdateFilmstripThumb(fi);
            UpdateOledPreview(px);
        };
        ctrl.CoordChanged += text => CoordChanged?.Invoke(text);
        ctrl.ZoomChanged  += zoom => ZoomChanged?.Invoke(zoom);

        EditorContainer.Child = ctrl;
        ctrl.ActiveTool          = _activeTool;
        ctrl.SetBrushSize(_brushSize);
        ctrl.SetBrushShape(_brushShape);
        ctrl.SymmetryV           = _symV;
        ctrl.SymmetryH           = _symH;
        ctrl.SetGrid(_showGrid, _showMinor);
        ctrl.ActiveStampTemplate = _stampTemplate;

        UpdateOledPreview(_frames[idx].Pixels);
        UpdateStatusLabel();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FILMSTRIP UI
    // ════════════════════════════════════════════════════════════════════════

    void AddFilmstripItem(int idx)
    {
        var thumb = RenderThumb(_frames[idx].Pixels);
        var img = new Image
        {
            Source  = thumb, Width = 128, Height = 64,
            Stretch = Stretch.Uniform, Tag = idx,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

        var numLabel = new TextBlock
        {
            Text = $"{idx + 1}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00)),
            FontFamily = new FontFamily("Consolas"), FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 0, 2),
        };

        var vstack = new StackPanel { Orientation = Orientation.Vertical };
        vstack.Children.Add(img);
        vstack.Children.Add(numLabel);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = idx,
            Child = vstack,
        };
        border.MouseLeftButtonUp += (_, _) => SelectFrame((int)border.Tag);
        FilmstripPanel.Children.Add(border);
    }

    void UpdateFilmstripThumb(int idx)
    {
        if (idx < 0 || idx >= FilmstripPanel.Children.Count) return;
        if (FilmstripPanel.Children[idx] is Border b &&
            b.Child is StackPanel sp && sp.Children[0] is Image img)
            img.Source = RenderThumb(_frames[idx].Pixels);
    }

    void RefreshFilmstripHighlight()
    {
        var accent    = Color.FromRgb(0xFF, 0x99, 0x00);
        var normalBg  = Color.FromRgb(0x18, 0x18, 0x18);
        var normalBrd = Color.FromRgb(0x33, 0x33, 0x33);
        for (int i = 0; i < FilmstripPanel.Children.Count; i++)
        {
            if (FilmstripPanel.Children[i] is Border b)
            {
                bool active = i == _currentFrame;
                b.Background  = new SolidColorBrush(active ? Color.FromRgb(0x28, 0x1A, 0x00) : normalBg);
                b.BorderBrush = new SolidColorBrush(active ? accent : normalBrd);
                b.BorderThickness = new Thickness(active ? 2 : 1);
            }
        }
    }

    void RebuildFilmstrip()
    {
        FilmstripPanel.Children.Clear();
        for (int i = 0; i < _frames.Count; i++) AddFilmstripItem(i);
        for (int i = 0; i < FilmstripPanel.Children.Count; i++)
        {
            if (FilmstripPanel.Children[i] is Border b)
            {
                b.Tag = i;
                if (b.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock lbl)
                    lbl.Text = $"{i + 1}";
                if (b.Child is StackPanel sp2 && sp2.Children[0] is Image img)
                    img.Tag = i;
            }
        }
        RefreshFilmstripHighlight();
        UpdateFrameCountLabel();
    }

    void UpdateFrameCountLabel() => FrameCountLabel.Text = $"Klatki: {_frames.Count}";

    // ════════════════════════════════════════════════════════════════════════
    //  PODGLĄD OLED
    // ════════════════════════════════════════════════════════════════════════

    void UpdateOledPreview(bool[,] pixels)
    {
        var bmp = _previewRenderer.Render(pixels, _aw, _ah, 1, showGrid1px: false, showGrid8px: false);
        OledPreviewImage.Source = bmp;
    }

    WriteableBitmap RenderThumb(bool[,] pixels) =>
        new PixelRenderer().Render(pixels, _aw, _ah, 1, showGrid1px: false, showGrid8px: false);

    // ════════════════════════════════════════════════════════════════════════
    //  PLAYER — podgląd ruchu sprite'a
    // ════════════════════════════════════════════════════════════════════════

    void SetFps(double fps)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        if (FpsLabel != null) FpsLabel.Text = ((int)fps).ToString();
    }

    void StartPlay()
    {
        if (_frames.Count < 1) return;
        _playing = true;
        PlayIcon.Text = "⏹"; PlayLabel.Text = "Stop";
        EditorContainer.Visibility  = Visibility.Collapsed;
        PlayPreviewBorder.Visibility = Visibility.Visible;
        _timer.Start();
    }

    void StopPlay()
    {
        _playing = false;
        _timer.Stop();
        PlayIcon.Text = "▶"; PlayLabel.Text = "Play";
        PlayPreviewBorder.Visibility = Visibility.Collapsed;
        EditorContainer.Visibility   = Visibility.Visible;
        RefreshFilmstripHighlight();
        int stopped = _currentFrame;
        _currentFrame = -1;
        SelectFrame(stopped);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        int next = (_currentFrame + 1) % _frames.Count;
        if (!_loopPlay && next == 0) { StopPlay(); return; }
        _currentFrame = next;
        var px = _frames[_currentFrame].Pixels;
        UpdateOledPreview(px);

        // Renderuj podgląd ze sprite'm w ruchu
        if (PlayPreviewBorder.ActualWidth > 0 && PlayPreviewBorder.ActualHeight > 0)
        {
            int zoomW = Math.Max(1, (int)((PlayPreviewBorder.ActualWidth  - 40) / _aw));
            int zoomH = Math.Max(1, (int)((PlayPreviewBorder.ActualHeight - 40) / _ah));
            int zoom  = Math.Max(1, Math.Min(zoomW, zoomH));
            PlayPreviewImage.Source = new PixelRenderer().Render(px, _aw, _ah, zoom,
                showGrid1px: false, showGrid8px: false);
        }
        UpdateStatusLabel();
        RefreshFilmstripHighlight();
    }

    void UpdateStatusLabel()
    {
        if (StatusFrameLabel == null) return;
        double fps = FpsSlider?.Value > 0 ? FpsSlider.Value : 10;
        StatusFrameLabel.Text = $"Klatka {_currentFrame + 1}/{_frames.Count}";
        StatusInfoLabel.Text  = $"  {_aw}×{_ah}px  ·  {(int)fps} FPS  ·  Sprite: {EffectiveName}";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  EKSPORT .h
    // ════════════════════════════════════════════════════════════════════════

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (EditorContainer.Child is PixelCanvasControl activeCtrl)
            activeCtrl.CommitSelection();

        // Użyj pierwszej klatki jako bitmapy sprite'a
        if (_frames.Count == 0) return;
        // Zapisz bieżącą klatkę
        if (EditorContainer.Child is PixelCanvasControl cur)
        {
            var px2 = cur.CurrentPixels;
            var fr2 = _frames[_currentFrame];
            if (px2.GetLength(0) == fr2.Height && px2.GetLength(1) == fr2.Width)
                Array.Copy(px2, fr2.Pixels, px2.Length);
        }

        var dlg = new SaveFileDialog
        {
            Title      = "Eksportuj sprite do pliku .h (include)",
            Filter     = "Arduino Header (*.h)|*.h|Wszystkie pliki (*.*)|*.*",
            FileName   = $"{EffectiveName}_sprite.h",
            DefaultExt = ".h",
        };
        if (dlg.ShowDialog() != true) return;

        // Jeśli więcej niż 1 klatka — eksportuj jako animację sprite'ów
        string code;
        if (_frames.Count == 1)
        {
            MotionSettings.SpriteName   = EffectiveName;
            MotionSettings.SpriteWidth  = _aw;
            MotionSettings.SpriteHeight = _ah;
            code = SpriteExporter.Export(_frames[0].Pixels, MotionSettings);
        }
        else
        {
            code = SpriteExporter.ExportMultiFrame(_frames, _aw, _ah, MotionSettings);
        }

        File.WriteAllText(dlg.FileName, code, Encoding.UTF8);
        ShowCodePreviewWindow(code, dlg.FileName);
    }

    void ShowCodePreviewWindow(string code, string savedPath)
    {
        var win = new Window
        {
            Title      = $"Eksport: {System.IO.Path.GetFileName(savedPath)}",
            Width      = 680, Height = 540,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            WindowStyle = WindowStyle.ToolWindow,
            Owner      = Window.GetWindow(this),
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            int dark = 1;
            MainWindow.DwmSetWindowAttributePublic(hwnd, 20, ref dark, sizeof(int));
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tb = new TextBox
        {
            Text      = code, IsReadOnly = true,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x99)),
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10), AcceptsReturn = true,
        };
        Grid.SetRow(tb, 0);
        root.Children.Add(tb);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10),
        };
        Grid.SetRow(btnRow, 1);

        var copyBtn = new Button
        {
            Content = "📋 Kopiuj", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x55, 0x00)),
            BorderThickness = new Thickness(1),
        };
        copyBtn.Click += (_, _) => { try { Clipboard.SetText(code); } catch { } };
        btnRow.Children.Add(copyBtn);

        var closeBtn = new Button
        {
            Content = "Zamknij", Padding = new Thickness(12, 6, 12, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
        };
        closeBtn.Click += (_, _) => win.Close();
        btnRow.Children.Add(closeBtn);

        root.Children.Add(btnRow);
        win.Content = root;
        win.Show();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  HANDLERY PRZYCISKÓW TOOLBAR
    // ════════════════════════════════════════════════════════════════════════

    private void AddFrame_Click(object sender, RoutedEventArgs e) => AddNewFrame();

    private void DupFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame < 0 || _currentFrame >= _frames.Count) return;
        if (EditorContainer.Child is PixelCanvasControl curCtrl)
        {
            var px = curCtrl.CurrentPixels;
            var fr = _frames[_currentFrame];
            if (px.GetLength(0) == fr.Height && px.GetLength(1) == fr.Width)
                Array.Copy(px, fr.Pixels, px.Length);
        }
        var src  = _frames[_currentFrame].Pixels;
        var copy = new bool[_ah, _aw];
        Array.Copy(src, copy, src.Length);
        int newIdx = _currentFrame + 1;
        int n = _frames.Count + 1;
        var tmpl = new PixelTemplate
        {
            Name   = $"{EffectiveName}_f{n:D3}",
            Width  = _aw, Height = _ah,
            Pixels = new bool[_ah, _aw],
        };
        for (int r = 0; r < _ah; r++)
        for (int c = 0; c < _aw; c++)
            tmpl.Pixels[r, c] = copy[r, c];
        _frames.Insert(newIdx, tmpl);
        RebuildFilmstrip();
        SelectFrame(newIdx);
    }

    private void DelFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count <= 1) return;
        _frames.RemoveAt(_currentFrame);
        int sel = Math.Min(_currentFrame, _frames.Count - 1);
        RebuildFilmstrip();
        _currentFrame = -1;
        SelectFrame(sel);
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame <= 0) return;
        (_frames[_currentFrame], _frames[_currentFrame - 1]) =
            (_frames[_currentFrame - 1], _frames[_currentFrame]);
        int sel = _currentFrame - 1;
        RebuildFilmstrip(); _currentFrame = -1; SelectFrame(sel);
    }

    private void MoveRight_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame >= _frames.Count - 1) return;
        (_frames[_currentFrame], _frames[_currentFrame + 1]) =
            (_frames[_currentFrame + 1], _frames[_currentFrame]);
        int sel = _currentFrame + 1;
        RebuildFilmstrip(); _currentFrame = -1; SelectFrame(sel);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay(); else StartPlay();
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        _loopPlay = !_loopPlay;
        LoopIcon.Text       = _loopPlay ? "🔁" : "➡";
        LoopIcon.Foreground = _loopPlay
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    }

    private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsLabel == null) return;
        SetFps(e.NewValue);
        if (_playing) { _timer.Stop(); _timer.Start(); }
        UpdateStatusLabel();
    }
}
