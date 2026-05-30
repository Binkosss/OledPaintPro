using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OledPaintPro;

/// <summary>Tryb generowania klatek pośrednich tween.</summary>
public enum TweenMorphMode
{
    /// <summary>Dissolve — dithering progowy (Bayer 4×4). Dobre dla losowych wzorów.</summary>
    Dissolve,
    /// <summary>Translate — przesuwa obiekty z klatki A do B bez zmiany rozmiaru.</summary>
    Translate,
    /// <summary>Scale — skaluje obiekty z klatki A do rozmiaru B bez przesunięcia centroid.</summary>
    Scale,
    /// <summary>Translate + Scale — jednoczesne przesunięcie i skalowanie (zalecane).</summary>
    TranslateScale,
}

public partial class AnimationEditorControl : UserControl
{
    // ── Klatki animacji ──────────────────────────────────────────────────────
    readonly List<PixelTemplate> _frames = new();
    int  _currentFrame = -1;
    bool _loopPlay     = true;

    // ── Player ───────────────────────────────────────────────────────────────
    // Wyższy priorytet = timer nie jest blokowany przez operacje tła w Dispatcherze
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    bool _playing = false;

    // ── Pre-rendered frames cache ─────────────────────────────────────────────
    // Klatki renderowane raz przy StartPlay() — Timer_Tick tylko podmienia Source (zero alokacji)
    WriteableBitmap[]? _playBitmaps;
    int                _playBitmapZoom = 0;
    readonly PixelRenderer _playRenderer = new();

    // ── Wymiary animacji (mutowalne przez ResizeAllFrames) ───────────────────
    int _awMutable, _ahMutable;
    int _aw => _awMutable;
    int _ah => _ahMutable;

    // ── Podgląd OLED ─────────────────────────────────────────────────────────
    readonly PixelRenderer _previewRenderer = new();

    // ── Nazwa bazowa ─────────────────────────────────────────────────────────
    readonly string _baseName;
    string? _baseNameOverride;

    // ── Aktywne narzędzie rysowania ──────────────────────────────────────────
    DrawTool _activeTool = DrawTool.Pencil;
    int _brushSize = 1;
    BrushShape _brushShape = BrushShape.Circle;

    // ── Opcje synchronizowane z MainWindow ───────────────────────────────────
    bool _symV = false;
    bool _symH = false;
    bool _showGrid  = true;
    bool _showMinor = true;
    PixelTemplate? _stampTemplate = null;

    /// <summary>
    /// Odpala się podczas odtwarzania (każda klatka) — przekazuje piksele do wyświetlenia na głównym canvasie.
    /// Parametry: pixels, width, height.
    /// </summary>
    public event Action<bool[,], int, int>? FramePreview;

    /// <summary>
    /// Odpala się gdy odtwarzanie się zatrzymuje — przekazuje indeks klatki do wczytania do edytora.
    /// </summary>
    public event Action<int>? PlaybackStopped;

    /// <summary>
    /// Odpala się gdy kursor jest nad aktywnym edytorem klatki — przekazuje tekst koordynatów.
    /// </summary>
    public event Action<string>? CoordChanged;

    /// <summary>
    /// Odpala się gdy zoom aktywnego edytora klatki się zmieni — przekazuje nową wartość zoom.
    /// </summary>
    public event Action<int>? ZoomChanged;

    /// <summary>
    /// Odpala się za każdym razem gdy liczba klatek się zmienia (dodanie, usunięcie, załadowanie).
    /// </summary>
    public event Action? FrameCountChanged;

    public DrawTool ActiveTool
    {
        get => _activeTool;
        set
        {
            _activeTool = value;
            if (CurrentEditor != null) CurrentEditor.ActiveTool = value;
        }
    }

    /// <summary>Przekazuje opcje tekstu do aktywnego edytora klatki.</summary>
    public void SetTextOptions(string font, double size, bool bold, bool italic, bool white)
        => CurrentEditor?.SetTextOptions(font, size, bold, italic, white);

    /// <summary>Ustawia rozmiar pędzla w aktywnym edytorze klatki.</summary>
    public void SetBrushSize(int size) { _brushSize = size; CurrentEditor?.SetBrushSize(size); }

    /// <summary>Ustawia kształt pędzla w aktywnym edytorze klatki.</summary>
    public void SetBrushShape(BrushShape shape) { _brushShape = shape; CurrentEditor?.SetBrushShape(shape); }

    /// <summary>Ustawia osie symetrii w aktywnym edytorze klatki.</summary>
    public void SetSymmetry(bool symV, bool symH)
    {
        _symV = symV;
        _symH = symH;
        if (CurrentEditor != null) { CurrentEditor.SymmetryV = symV; CurrentEditor.SymmetryH = symH; }
    }

    /// <summary>Ustawia widoczność siatki w aktywnym edytorze klatki.</summary>
    public void SetGrid(bool showGrid, bool showMinor)
    {
        _showGrid  = showGrid;
        _showMinor = showMinor;
        CurrentEditor?.SetGrid(showGrid, showMinor);
    }

    /// <summary>Ustawia aktywny szablon stempla w aktywnym edytorze klatki.</summary>
    public void SetStampTemplate(PixelTemplate? tmpl)
    {
        _stampTemplate = tmpl;
        if (CurrentEditor != null) CurrentEditor.ActiveStampTemplate = tmpl;
    }

    // ════════════════════════════════════════════════════════════════════════

    public AnimationEditorControl(string baseName, int width, int height)
    {
        _baseName  = baseName;
        _awMutable = width;
        _ahMutable = height;

        InitializeComponent();

        _timer.Tick += Timer_Tick;
        SetFps(10);
        FpsSlider.Value = 10;

        // Gdy okno zmienia rozmiar podczas odtwarzania — przerenderuj ze nowym zoom
        AnimPlayPreviewBorder.SizeChanged += (_, _) => { if (_playing) InvalidatePlayCache(); };

        // Pierwsza klatka pusta
        AddNewFrame();
    }

    // ── Liczba klatek (dostępne zewnętrznie) ─────────────────────────────────
    public int FrameCount   => _frames.Count;
    public int FrameWidth   => _aw;
    public int FrameHeight  => _ah;

    /// <summary>Dodaje nową klatkę z gotową tablicą pikseli (np. wczytaną z .h).</summary>
    public void AddFrameFromPixels(bool[,] pixels)
    {
        AddNewFrame(pixels);
    }

    /// <summary>Zmienia nazwę bazową (używaną w eksporcie i labelach klatek).</summary>
    public void SetBaseName(string name)
    {
        // _baseName jest readonly — przechowuj przez field wrapper
        _baseNameOverride = name;
    }

    string EffectiveName => _baseNameOverride ?? _baseName;

    /// <summary>Zmienia rozmiar wszystkich klatek — przycina lub uzupełnia zerami.</summary>
    public void ResizeAllFrames(int nw, int nh)
    {
        // Zapisz bieżący edytor
        if (_currentFrame >= 0 && _currentFrame < _frames.Count &&
            EditorContainer.Child is PixelCanvasControl curCtrl)
        {
            var px = curCtrl.CurrentPixels;
            var fr = _frames[_currentFrame];
            if (px.GetLength(0) == fr.Height && px.GetLength(1) == fr.Width)
                Array.Copy(px, fr.Pixels, px.Length);
        }

        for (int i = 0; i < _frames.Count; i++)
        {
            var old = _frames[i];
            var newPixels = new bool[nh, nw];
            for (int r = 0; r < Math.Min(old.Height, nh); r++)
                for (int c = 0; c < Math.Min(old.Width, nw); c++)
                    newPixels[r, c] = old.Pixels[r, c];
            old.Width  = nw;
            old.Height = nh;
            old.Pixels = newPixels;
        }

        // Pole _aw/_ah są readonly — użyj backing fields
        _awMutable = nw;
        _ahMutable = nh;

        // Przebuduj filmstrip i edytor
        int sel = Math.Max(0, Math.Min(_currentFrame, _frames.Count - 1));
        _currentFrame = -1;
        RebuildFilmstrip();
        SelectFrame(sel);
        UpdateStatusLabel();
    }

    /// <summary>Wywołuje eksport (jak kliknięcie przycisku Eksport w animacji).</summary>
    public void TriggerExport() => Export_Click(this, new RoutedEventArgs());

    /// <summary>
    /// Zastępuje całą animację podanymi klatkami. Używane przy wczytywaniu pliku .h.
    /// </summary>
    public void LoadFrames(List<bool[,]> frames, int width, int height, string? baseName = null, int fps = 10)
    {
        if (frames.Count == 0) return;

        // Zatrzymaj odtwarzanie
        if (_playing) StopPlay();

        // Wyczyść obecny stan
        _frames.Clear();
        FilmstripPanel.Children.Clear();
        _currentFrame = -1;
        EditorContainer.Child = null;

        _awMutable = width;
        _ahMutable = height;
        if (baseName != null) _baseNameOverride = baseName;

        // Załaduj klatki
        for (int fi = 0; fi < frames.Count; fi++)
        {
            var src = frames[fi];
            var tmpl = new PixelTemplate
            {
                Name   = $"{EffectiveName}_f{fi + 1:D3}",
                Width  = width,
                Height = height,
                Pixels = new bool[height, width],
            };
            int rowsToCopy = Math.Min(height, src.GetLength(0));
            int colsToCopy = Math.Min(width,  src.GetLength(1));
            for (int r = 0; r < rowsToCopy; r++)
                for (int c = 0; c < colsToCopy; c++)
                    tmpl.Pixels[r, c] = src[r, c];
            _frames.Add(tmpl);
        }

        // Ustaw FPS
        FpsSlider.Value = Math.Clamp(fps, 1, 60);
        SetFps(fps);

        RebuildFilmstrip();
        SelectFrame(0);
        UpdateStatusLabel();
        FrameCountChanged?.Invoke();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  KLATKI
    // ════════════════════════════════════════════════════════════════════════

    void AddNewFrame(bool[,]? pixels = null)
    {
        int n = _frames.Count + 1;
        var tmpl = new PixelTemplate
        {
            Name   = $"{_baseName}_f{n:D3}",
            Width  = _aw,
            Height = _ah,
            Pixels = new bool[_ah, _aw],   // zawsze jawna alokacja!
        };
        if (pixels != null)
        {
            int rowsToCopy = Math.Min(_ah, pixels.GetLength(0));
            int colsToCopy = Math.Min(_aw, pixels.GetLength(1));
            for (int r = 0; r < rowsToCopy; r++)
                for (int c = 0; c < colsToCopy; c++)
                    tmpl.Pixels[r, c] = pixels[r, c];
        }

        _frames.Add(tmpl);
        AddFilmstripItem(_frames.Count - 1);
        SelectFrame(_frames.Count - 1);
        UpdateFrameCountLabel();
        FrameCountChanged?.Invoke();
    }

    void SelectFrame(int idx)
    {
        if (idx < 0 || idx >= _frames.Count) return;

        // Zatrzymaj odtwarzanie
        if (_playing) StopPlay();

        // ── Zapisz piksele z bieżącego edytora z powrotem do klatki ────────
        if (_currentFrame >= 0 && _currentFrame < _frames.Count
            && EditorContainer.Child is PixelCanvasControl prevCtrl)
        {
            // Zatwierdź aktywne zaznaczenie zanim opuścimy klatkę
            if (prevCtrl.HasActiveSelection)
                prevCtrl.CommitSelection();

            var px = prevCtrl.CurrentPixels;
            var fr = _frames[_currentFrame];
            if (px.GetLength(0) == fr.Height && px.GetLength(1) == fr.Width)
                Array.Copy(px, fr.Pixels, px.Length);
            prevCtrl.FlushUndoToTemplate();
            UpdateFilmstripThumb(_currentFrame);
        }

        _currentFrame = idx;
        RefreshFilmstripHighlight();

        // ── Reużyj istniejącego edytora lub stwórz nowy (tylko raz) ─────────
        if (EditorContainer.Child is PixelCanvasControl existing)
        {
            // Reużycie: tylko podmiana danych — bez migotania
            existing.SwitchTemplate(_frames[idx]);
        }
        else
        {
            // Pierwsze uruchomienie — stwórz kontrolkę i zarejestruj eventy
            var ctrl = new PixelCanvasControl(_frames[idx]);
            ctrl.PixelsChanged += ec =>
            {
                var px2 = ec.CurrentPixels;
                var fr2 = ec.Template;
                if (px2.GetLength(0) == fr2.Height && px2.GetLength(1) == fr2.Width)
                    Array.Copy(px2, fr2.Pixels, px2.Length);
                int fi = _frames.IndexOf(fr2);
                if (fi >= 0) UpdateFilmstripThumb(fi);
                UpdateOledPreview(px2);
                InvalidatePlayCache();
            };
            ctrl.SelectionCommitted += (ec, args) =>
            {
                if (_syncFrames) ApplySyncCommitToAllFrames(ec, args);
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
        }

        // Po każdej zmianie klatki — synchronizuj PixelsChanged przez aktualny ctrl
        if (EditorContainer.Child is PixelCanvasControl activeCtrl)
        {
            // Przepnij subskrypcję PixelsChanged żeby wskazywała bieżącą klatkę
            // (eventy zarejestrowane raz przy tworzeniu są poprawne — Template zmieniony przez SwitchTemplate)
            activeCtrl.ActiveTool          = _activeTool;
            activeCtrl.SetBrushSize(_brushSize);
            activeCtrl.SetBrushShape(_brushShape);
            activeCtrl.SymmetryV           = _symV;
            activeCtrl.SymmetryH           = _symH;
            activeCtrl.SetGrid(_showGrid, _showMinor);
            activeCtrl.ActiveStampTemplate = _stampTemplate;
        }

        UpdateOledPreview(_frames[idx].Pixels);
        UpdateStatusLabel();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  FILMSTRIP UI
    // ════════════════════════════════════════════════════════════════════════

    void AddFilmstripItem(int idx)
    {
        var tmpl = _frames[idx];
        var thumb = RenderThumb(tmpl.Pixels);

        var img = new Image
        {
            Source  = thumb,
            Width   = 128,
            Height  = 64,
            Stretch = Stretch.Uniform,
            Tag     = idx,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

        var numLabel = new TextBlock
        {
            Text       = $"{idx + 1}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0xFF, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 9,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin     = new Thickness(4, 0, 0, 2),
        };

        var border = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Margin          = new Thickness(0, 0, 0, 6),
            Padding         = new Thickness(4),
            Cursor          = System.Windows.Input.Cursors.Hand,
            Tag             = idx,
        };

        var vstack = new StackPanel { Orientation = Orientation.Vertical };
        vstack.Children.Add(img);
        vstack.Children.Add(numLabel);
        border.Child = vstack;

        // PreviewMouseLeftButtonDown zapobiega problemowi z ScrollViewerem,
        // który przechwytuje MouseLeftButtonUp podczas drag-scroll.
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            int fi = (int)border.Tag;
            SelectFrame(fi);
            e.Handled = false;
        };

        FilmstripPanel.Children.Add(border);
    }

    void UpdateFilmstripThumb(int idx)
    {
        if (idx < 0 || idx >= FilmstripPanel.Children.Count) return;
        if (FilmstripPanel.Children[idx] is Border b &&
            b.Child is StackPanel sp &&
            sp.Children[0] is Image img)
        {
            img.Source = RenderThumb(_frames[idx].Pixels);
        }
    }

    // Zamrożone (Frozen) brushe — tworzone raz, reużywane zamiast new SolidColorBrush każde wywołanie
    static readonly SolidColorBrush _bActive   = MakeFrozen(Color.FromRgb(0x1C, 0x28, 0x20));
    static readonly SolidColorBrush _bNormal   = MakeFrozen(Color.FromRgb(0x18, 0x18, 0x18));
    static readonly SolidColorBrush _brActive  = MakeFrozen(Color.FromRgb(0x44, 0xFF, 0x88));
    static readonly SolidColorBrush _brNormal  = MakeFrozen(Color.FromRgb(0x33, 0x33, 0x33));
    static readonly Thickness _th1 = new(1), _th2 = new(2);
    static SolidColorBrush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void RefreshFilmstripHighlight()
    {
        for (int i = 0; i < FilmstripPanel.Children.Count; i++)
        {
            if (FilmstripPanel.Children[i] is Border b)
            {
                bool active       = i == _currentFrame;
                b.Background      = active ? _bActive  : _bNormal;
                b.BorderBrush     = active ? _brActive : _brNormal;
                b.BorderThickness = active ? _th2      : _th1;
            }
        }
    }

    void RebuildFilmstrip()
    {
        FilmstripPanel.Children.Clear();
        for (int i = 0; i < _frames.Count; i++)
            AddFilmstripItem(i);
        // Popraw tagi i numery (po przetasowaniu)
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
        InvalidatePlayCache();
    }

    void UpdateFrameCountLabel()
    {
        FrameCountLabel.Text = $"Klatki: {_frames.Count}";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PODGLĄD OLED
    // ════════════════════════════════════════════════════════════════════════

    void UpdateOledPreview(bool[,] pixels)
    {
        // Małe "OLED" podgląd 128×64 (1:1) bez siatki
        var bmp = _previewRenderer.Render(pixels, _aw, _ah, 1, showGrid1px: false, showGrid8px: false);
        OledPreviewImage.Source = bmp;
    }

    WriteableBitmap RenderThumb(bool[,] pixels)
    {
        // Każde wywołanie tworzy nowy renderer = nową niezależną bitmapę
        // (nie wolno reużywać _previewRenderer — wszystkie img.Source wskazywałyby na tę samą bitmapę)
        return new PixelRenderer().Render(pixels, _aw, _ah, 1, showGrid1px: false, showGrid8px: false);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PLAYER
    // ════════════════════════════════════════════════════════════════════════

    void SetFps(double fps)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        if (FpsLabel != null) FpsLabel.Text = ((int)fps).ToString();
    }

    void StartPlay()
    {
        if (_frames.Count < 2) return;
        _playing = true;
        PlayIcon.Text  = "⏹";
        PlayLabel.Text = "Stop";
        // Pokaż podgląd odtwarzania, ukryj edytor klatki
        EditorContainer.Visibility       = Visibility.Collapsed;
        AnimPlayPreviewBorder.Visibility = Visibility.Visible;

        // Pre-renderuj klatki raz (nie co tick)
        PreRenderPlayFrames();

        _timer.Start();
    }

    void StopPlay()
    {
        _playing = false;
        _timer.Stop();
        PlayIcon.Text  = "▶";
        PlayLabel.Text = "Play";
        // Ukryj podgląd, pokaż edytor — i wczytaj bieżącą klatkę
        AnimPlayPreviewBorder.Visibility = Visibility.Collapsed;
        EditorContainer.Visibility       = Visibility.Visible;
        RefreshFilmstripHighlight();
        PlaybackStopped?.Invoke(_currentFrame);
        // Załaduj klatkę do edytora (wczytaj zatrzymaną klatkę)
        int stoppedAt = _currentFrame;
        _currentFrame = -1;   // wymuś przeładowanie
        SelectFrame(stoppedAt);
    }

    /// <summary>
    /// Wstępnie renderuje wszystkie klatki do tablicy WriteableBitmap.
    /// Każda klatka ma własną bitmapbitmapmap (nie można reuseć jednej dla wszystkich,
    /// bo WPF trzyma referencję do Source na wyświetlanej klatce).
    /// </summary>
    void PreRenderPlayFrames()
    {
        if (AnimPlayPreviewBorder.ActualWidth <= 0 || AnimPlayPreviewBorder.ActualHeight <= 0) return;

        int zoomW = Math.Max(1, (int)((AnimPlayPreviewBorder.ActualWidth  - 40) / _aw));
        int zoomH = Math.Max(1, (int)((AnimPlayPreviewBorder.ActualHeight - 40) / _ah));
        int zoom  = Math.Max(1, Math.Min(zoomW, zoomH));

        if (_playBitmaps != null && _playBitmaps.Length == _frames.Count && _playBitmapZoom == zoom)
            return;  // cache nadal aktualny

        _playBitmapZoom = zoom;
        int bw = _aw * zoom, bh = _ah * zoom;
        _playBitmaps = new WriteableBitmap[_frames.Count];

        for (int fi = 0; fi < _frames.Count; fi++)
        {
            // Każda klatka = oddzielna WriteableBitmap (WPF Source musi być unikalny)
            var renderer = new PixelRenderer();
            var bmp = renderer.Render(_frames[fi].Pixels, _aw, _ah, zoom,
                showGrid1px: false, showGrid8px: false);
            // Zamroź bitmapbę — WPF może ją wyskoczyć na GPU i trzymać tam
            bmp.Freeze();
            _playBitmaps[fi] = bmp;
        }
    }

    /// <summary>Unieważnia cache pre-renderowanych klatek (np. po edycji klatki).</summary>
    void InvalidatePlayCache() => _playBitmaps = null;

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // Przejdź do następnej klatki
        int next = (_currentFrame + 1) % _frames.Count;
        if (!_loopPlay && next == 0) { StopPlay(); return; }
        _currentFrame = next;

        // ── Duży podgląd: podmiana Source (pre-rendered, ZERO alokacji) ────────────────
        if (_playBitmaps != null && _currentFrame < _playBitmaps.Length)
        {
            AnimPlayImage.Source = _playBitmaps[_currentFrame];
        }
        else if (AnimPlayPreviewBorder.ActualWidth > 0)
        {
            // Fallback: rerender (np. po zmianie rozmiaru okna)
            PreRenderPlayFrames();
            if (_playBitmaps != null && _currentFrame < _playBitmaps.Length)
                AnimPlayImage.Source = _playBitmaps[_currentFrame];
        }

        // ── Mały podgląd OLED (reuse renderer, bez alokacji) ──────────────────────
        var px = _frames[_currentFrame].Pixels;
        var previewBmp = _previewRenderer.Render(px, _aw, _ah, 1, showGrid1px: false, showGrid8px: false);
        OledPreviewImage.Source = previewBmp;

        FramePreview?.Invoke(px, _aw, _ah);
        AnimStatusLabel.Text = $"Klatka {_currentFrame + 1}/{_frames.Count}";
        // Nie odwieżamy filmstrip podczas odtwarzania — oszczędza setki alokacji SolidColorBrush/s
    }

    void UpdateStatusLabel()
    {
        if (AnimStatusLabel == null) return;
        double fps = FpsSlider?.Value > 0 ? FpsSlider.Value : 10;
        AnimStatusLabel.Text = $"Klatka {_currentFrame + 1}/{_frames.Count}";
        AnimSizeLabel.Text   = $"  {_aw}×{_ah}px  ·  {(int)fps} FPS  ·  ~{_frames.Count * 1000.0 / fps:F0} ms total";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  EKSPORT .h
    // ════════════════════════════════════════════════════════════════════════

    string GenerateAnimationH()
    {
        var sb = new StringBuilder();
        string nm   = EffectiveName;
        int bpr     = (_aw + 7) / 8;
        int total   = bpr * _ah;
        int fps     = (int)FpsSlider.Value;

        sb.AppendLine($"// Animacja OLED '{nm}' — wygenerowane przez OLED Paint Pro");
        sb.AppendLine($"// Rozmiar klatki: {_aw}×{_ah}px  |  {_frames.Count} klatek  |  {fps} FPS");
        sb.AppendLine($"// Format: MSB-first, {bpr} bajtów/wiersz × {_ah} wiersze = {total} bajtów/klatka");
        sb.AppendLine($"// Użycie: u8g2.drawBitmap(0, 0, {bpr}, {_ah}, {nm}_frames[frame]);");
        sb.AppendLine();
        sb.AppendLine($"#pragma once");
        sb.AppendLine();
        sb.AppendLine($"#define {nm.ToUpper()}_FRAME_COUNT  {_frames.Count}");
        sb.AppendLine($"#define {nm.ToUpper()}_FRAME_DELAY  {1000 / fps}   // ms");
        sb.AppendLine($"#define {nm.ToUpper()}_WIDTH        {_aw}");
        sb.AppendLine($"#define {nm.ToUpper()}_HEIGHT       {_ah}");
        sb.AppendLine();

        // Każda klatka jako osobna const array
        for (int fi = 0; fi < _frames.Count; fi++)
        {
            var px = _frames[fi].Pixels;
            sb.AppendLine($"// Klatka {fi + 1}/{_frames.Count}");
            sb.AppendLine($"const unsigned char PROGMEM {nm}_f{fi + 1:D3}[] = {{");
            for (int row = 0; row < _ah; row++)
            {
                sb.Append("  ");
                for (int b = 0; b < bpr; b++)
                {
                    byte byteVal = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int col = b * 8 + bit;
                        if (col < _aw && px[row, col])
                            byteVal |= (byte)(1 << (7 - bit));
                    }
                    bool last = (row == _ah - 1) && (b == bpr - 1);
                    sb.Append($"0x{byteVal:X2}{(last ? "" : ",")} ");
                }
                sb.AppendLine();
            }
            sb.AppendLine($"}};");
            sb.AppendLine();
        }

        // Tablica wskaźników
        sb.AppendLine($"const unsigned char* const {nm}_frames[{nm.ToUpper()}_FRAME_COUNT] PROGMEM = {{");
        for (int fi = 0; fi < _frames.Count; fi++)
        {
            bool last = fi == _frames.Count - 1;
            sb.AppendLine($"  {nm}_f{fi + 1:D3}{(last ? "" : ",")}");
        }
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine($"// ── Przykład użycia na ESP32 / Arduino: ────────────────────");
        sb.AppendLine($"// static uint8_t frame = 0;");
        sb.AppendLine($"// static uint32_t lastT = 0;");
        sb.AppendLine($"// void loop() {{");
        sb.AppendLine($"//   if (millis() - lastT >= {nm.ToUpper()}_FRAME_DELAY) {{");
        sb.AppendLine($"//     lastT = millis();");
        sb.AppendLine($"//     u8g2.clearBuffer();");
        sb.AppendLine($"//     u8g2.drawBitmap(0, 0, {bpr}, {_ah},");
        sb.AppendLine($"//       (const uint8_t*)pgm_read_ptr(&{nm}_frames[frame]));");
        sb.AppendLine($"//     u8g2.sendBuffer();");
        sb.AppendLine($"//     frame = (frame + 1) % {nm.ToUpper()}_FRAME_COUNT;");
        sb.AppendLine($"//   }}");
        sb.AppendLine($"// }}");

        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  HANDLERY PRZYCISKÓW
    // ════════════════════════════════════════════════════════════════════════

    private void AddFrame_Click(object sender, RoutedEventArgs e)
    {
        AddNewFrame();
    }

    private void DupFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame < 0 || _currentFrame >= _frames.Count) return;
        // Najpierw zapisz bieżący edytor do klatki
        if (EditorContainer.Child is PixelCanvasControl curCtrl)
        {
            var px = curCtrl.CurrentPixels;
            var fr = _frames[_currentFrame];
            if (px.GetLength(0) == fr.Height && px.GetLength(1) == fr.Width)
                Array.Copy(px, fr.Pixels, px.Length);
        }
        var src = _frames[_currentFrame].Pixels;
        var copy = new bool[_ah, _aw];
        Array.Copy(src, copy, src.Length);
        // Wstaw duplikat za bieżącą klatką
        int newIdx = _currentFrame + 1;
        int n      = _frames.Count + 1;
        var tmpl   = new PixelTemplate
        {
            Name   = $"{_baseName}_f{n:D3}",
            Width  = _aw,
            Height = _ah,
            Pixels = new bool[_ah, _aw],   // jawna alokacja
        };
        for (int r = 0; r < _ah; r++)
            for (int c = 0; c < _aw; c++)
                tmpl.Pixels[r, c] = copy[r, c];

        _frames.Insert(newIdx, tmpl);
        RebuildFilmstrip();
        SelectFrame(newIdx);
        UpdateFrameCountLabel();
        FrameCountChanged?.Invoke();
    }

    private void DelFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count <= 1) return;
        _frames.RemoveAt(_currentFrame);
        int sel = Math.Min(_currentFrame, _frames.Count - 1);
        RebuildFilmstrip();
        _currentFrame = -1;
        SelectFrame(sel);
        FrameCountChanged?.Invoke();
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame <= 0) return;
        (_frames[_currentFrame], _frames[_currentFrame - 1]) =
            (_frames[_currentFrame - 1], _frames[_currentFrame]);
        int sel = _currentFrame - 1;
        RebuildFilmstrip();
        _currentFrame = -1;
        SelectFrame(sel);
        FrameCountChanged?.Invoke();
    }

    private void MoveRight_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFrame >= _frames.Count - 1) return;
        (_frames[_currentFrame], _frames[_currentFrame + 1]) =
            (_frames[_currentFrame + 1], _frames[_currentFrame]);
        int sel = _currentFrame + 1;
        RebuildFilmstrip();
        _currentFrame = -1;
        SelectFrame(sel);
        FrameCountChanged?.Invoke();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) StopPlay();
        else          StartPlay();
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
        if (_playing)
        {
            _timer.Stop();
            _timer.Start();
        }
        UpdateStatusLabel();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        // Najpierw zatwierdź aktywny edytor
        if (EditorContainer.Child is PixelCanvasControl activeCtrl)
            activeCtrl.CommitSelection();

        var dlg = new SaveFileDialog
        {
            Title      = "Eksportuj animację dla ESP32",
            Filter     = "Arduino Header (*.h)|*.h|Wszystkie pliki (*.*)|*.*",
            FileName   = $"{_baseName}_anim.h",
            DefaultExt = ".h",
        };
        if (dlg.ShowDialog() != true) return;

        string code = GenerateAnimationH();
        File.WriteAllText(dlg.FileName, code, Encoding.UTF8);

        // Pokaż podgląd kodu w okienku
        ShowCodePreviewWindow(code, dlg.FileName);
    }

    void ShowCodePreviewWindow(string code, string savedPath)
    {
        var win = new Window
        {
            Title  = $"Eksport: {System.IO.Path.GetFileName(savedPath)}",
            Width  = 680,
            Height = 540,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
            WindowStyle = WindowStyle.ToolWindow,
            Owner = Window.GetWindow(this),
        };
        // Dark title bar
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
            Text             = code,
            IsReadOnly       = true,
            Background       = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            Foreground       = new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0xCC)),
            FontFamily       = new FontFamily("Consolas"),
            FontSize         = 11,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping     = TextWrapping.NoWrap,
            BorderThickness  = new Thickness(0),
            Padding          = new Thickness(10),
            AcceptsReturn    = true,
        };
        Grid.SetRow(tb, 0);
        root.Children.Add(tb);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 8, 10, 10),
        };
        Grid.SetRow(btnRow, 1);

        var copyBtn = new Button
        {
            Content    = "📋 Kopiuj do schowka",
            Padding    = new Thickness(12, 6, 12, 6),
            Margin     = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xFF, 0xCC)),
            BorderBrush= new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0x66)),
            BorderThickness = new Thickness(1),
        };
        copyBtn.Click += (_, _) => Clipboard.SetText(code);
        btnRow.Children.Add(copyBtn);

        var closeBtn = new Button
        {
            Content = "Zamknij",
            Padding = new Thickness(12, 6, 12, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush= new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
        };
        closeBtn.Click += (_, _) => win.Close();
        btnRow.Children.Add(closeBtn);

        root.Children.Add(btnRow);
        win.Content = root;
        win.Show();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  POMOCNICZE
    // ════════════════════════════════════════════════════════════════════════

    PixelCanvasControl? CurrentEditor =>
        EditorContainer.Child as PixelCanvasControl;

    public PixelCanvasControl? ActiveEditorControl => CurrentEditor;

    public void Undo()
    {
        if (_syncFrames)
        {
            // Cofnij aktywną klatkę przez kontrolkę (obsługa selekcji, RenderCanvas itp.)
            CurrentEditor?.Undo();
            // Cofnij pozostałe klatki bezpośrednio ze stosu undo w szablonie
            var activeTemplate = CurrentEditor?.Template;
            for (int i = 0; i < _frames.Count; i++)
            {
                var frame = _frames[i];
                if (frame == activeTemplate) continue;
                if (frame.UndoStack.Count == 0) continue;
                var redoSnap = (bool[,])frame.Pixels.Clone();
                frame.RedoStack.Push(redoSnap);
                Array.Copy(frame.UndoStack.Pop(), frame.Pixels, frame.Pixels.Length);
                UpdateFilmstripThumb(i);
            }
            InvalidatePlayCache();
        }
        else
        {
            CurrentEditor?.Undo();
        }
    }

    public void Redo()
    {
        if (_syncFrames)
        {
            CurrentEditor?.Redo();
            var activeTemplate = CurrentEditor?.Template;
            for (int i = 0; i < _frames.Count; i++)
            {
                var frame = _frames[i];
                if (frame == activeTemplate) continue;
                if (frame.RedoStack.Count == 0) continue;
                var undoSnap = (bool[,])frame.Pixels.Clone();
                frame.UndoStack.Push(undoSnap);
                Array.Copy(frame.RedoStack.Pop(), frame.Pixels, frame.Pixels.Length);
                UpdateFilmstripThumb(i);
            }
            InvalidatePlayCache();
        }
        else
        {
            CurrentEditor?.Redo();
        }
    }
    public void Clear()  => CurrentEditor?.Clear();
    public void Invert() => CurrentEditor?.Invert();
    public void FlipH()  => CurrentEditor?.ApplyFlipH();
    public void FlipV()  => CurrentEditor?.ApplyFlipV();

    /// <summary>
    /// Generuje klatki pośrednie (tween) między klatką fromIdx a toIdx.
    /// Wstawia <paramref name="count"/> klatek zaraz za fromIdx.
    /// easingFn: double(double t) gdzie t in [0..1]
    /// </summary>
    public void InsertTweenFrames(int fromIdx, int toIdx, int count,
        Func<double, double> easingFn, TweenMorphMode morphMode = TweenMorphMode.TranslateScale)
    {
        if (fromIdx < 0 || fromIdx >= _frames.Count) return;
        if (toIdx   < 0 || toIdx   >= _frames.Count) return;
        if (count < 1) return;

        // Zatwierdź bieżący edytor przed odczytem pikseli
        if (EditorContainer.Child is PixelCanvasControl ec)
        {
            if (ec.HasActiveSelection) ec.CommitSelection();
            var px2 = ec.CurrentPixels;
            var fr2 = _frames[_currentFrame];
            if (px2.GetLength(0) == fr2.Height && px2.GetLength(1) == fr2.Width)
                Array.Copy(px2, fr2.Pixels, px2.Length);
        }

        var srcPixels = _frames[fromIdx].Pixels;
        var dstPixels = _frames[toIdx].Pixels;

        var newFrames = new List<PixelTemplate>();
        for (int i = 1; i <= count; i++)
        {
            double t  = (double)i / (count + 1);
            double et = easingFn(t);

            var tmpl = new PixelTemplate
            {
                Name   = $"{EffectiveName}_tween_{i:D3}",
                Width  = _aw,
                Height = _ah,
                Pixels = new bool[_ah, _aw],
            };

            tmpl.Pixels = morphMode switch
            {
                TweenMorphMode.Dissolve        => TweenDissolve(srcPixels, dstPixels, et),
                TweenMorphMode.Translate       => TweenGeometry(srcPixels, dstPixels, et, scale: false),
                TweenMorphMode.Scale           => TweenGeometry(srcPixels, dstPixels, et, scale: true, translateOnly: false),
                TweenMorphMode.TranslateScale  => TweenGeometry(srcPixels, dstPixels, et, scale: true),
                _                              => TweenDissolve(srcPixels, dstPixels, et),
            };

            newFrames.Add(tmpl);
        }

        // Wstaw za fromIdx
        int insertAt = fromIdx + 1;
        _frames.InsertRange(insertAt, newFrames);

        RebuildFilmstrip();
        SelectFrame(fromIdx);
        FrameCountChanged?.Invoke();
    }

    // ── Algorytmy morfingu ───────────────────────────────────────────────────

    /// <summary>Dissolve — progowa interpolacja binarnych pikseli (Bayer ordered dithering).</summary>
    static bool[,] TweenDissolve(bool[,] src, bool[,] dst, double t)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var result = new bool[h, w];
        // Macierz Bayera 4×4 — daje lepszy rezultat niż zwykłe threshold
        double[,] bayer =
        {
            {  0/16.0,  8/16.0,  2/16.0, 10/16.0 },
            { 12/16.0,  4/16.0, 14/16.0,  6/16.0 },
            {  3/16.0, 11/16.0,  1/16.0,  9/16.0 },
            { 15/16.0,  7/16.0, 13/16.0,  5/16.0 },
        };
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                double a = src[r, c] ? 1.0 : 0.0;
                double b = dst[r, c] ? 1.0 : 0.0;
                double blended = a + (b - a) * t;
                double threshold = bayer[r % 4, c % 4];
                result[r, c] = blended > threshold;
            }
        return result;
    }

    /// <summary>
    /// Geometry morph — przesuwa i/lub skaluje bounding box pikseli ON z src do dst.
    /// Dual-pass: forward (src→t) + backward (dst→1-t), suma obu.
    /// </summary>
    static bool[,] TweenGeometry(bool[,] src, bool[,] dst, double t,
        bool scale = true, bool translateOnly = true)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var result = new bool[h, w];

        // Oblicz bounding boxy
        bool srcHasPixels = BBox(src, h, w, out double srcMinR, out double srcMaxR, out double srcMinC, out double srcMaxC);
        bool dstHasPixels = BBox(dst, h, w, out double dstMinR, out double dstMaxR, out double dstMinC, out double dstMaxC);

        if (!srcHasPixels && !dstHasPixels) return result;

        // Centrum i rozmiar
        double srcCY = (srcMinR + srcMaxR) * 0.5, srcCX = (srcMinC + srcMaxC) * 0.5;
        double dstCY = (dstMinR + dstMaxR) * 0.5, dstCX = (dstMinC + dstMaxC) * 0.5;
        double srcH  = Math.Max(1, srcMaxR - srcMinR);
        double srcW  = Math.Max(1, srcMaxC - srcMinC);
        double dstH  = Math.Max(1, dstMaxR - dstMinR);
        double dstW  = Math.Max(1, dstMaxC - dstMinC);

        // Interpolowane centrum i rozmiar
        double tCY = srcCY + (dstCY - srcCY) * t;
        double tCX = srcCX + (dstCX - srcCX) * t;
        double tH  = scale ? srcH + (dstH - srcH) * t : srcH;
        double tW  = scale ? srcW + (dstW - srcW) * t : srcW;

        // Forward pass: transformuj piksele src → pozycja pośrednia przy t
        if (srcHasPixels)
            TransformPixels(src, h, w, srcCY, srcCX, srcH, srcW, tCY, tCX, tH, tW, result, scale);

        // Backward pass: transformuj piksele dst ← pozycja pośrednia przy (1-t)
        if (dstHasPixels)
        {
            double bCY = dstCY + (srcCY - dstCY) * (1.0 - t);
            double bCX = dstCX + (srcCX - dstCX) * (1.0 - t);
            double bH  = scale ? dstH + (srcH - dstH) * (1.0 - t) : dstH;
            double bW  = scale ? dstW + (srcW - dstW) * (1.0 - t) : dstW;
            TransformPixels(dst, h, w, dstCY, dstCX, dstH, dstW, bCY, bCX, bH, bW, result, scale);
        }

        return result;
    }

    /// <summary>
    /// Transformuje piksele ON z <paramref name="src"/> z bbox (srcCY,srcCX,srcH,srcW)
    /// do docelowego bbox (tCY,tCX,tH,tW) i zapisuje wynik w <paramref name="dst"/>.
    /// </summary>
    static void TransformPixels(bool[,] src, int h, int w,
        double srcCY, double srcCX, double srcH, double srcW,
        double tCY,   double tCX,   double tH,   double tW,
        bool[,] dst,  bool scale)
    {
        double scaleY = scale ? tH / srcH : 1.0;
        double scaleX = scale ? tW / srcW : 1.0;

        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                if (!src[r, c]) continue;
                double dr = (r - srcCY) * scaleY;
                double dc = (c - srcCX) * scaleX;
                int nr = (int)Math.Round(tCY + dr);
                int nc = (int)Math.Round(tCX + dc);
                if (nr >= 0 && nr < h && nc >= 0 && nc < w)
                    dst[nr, nc] = true;
            }
    }

    /// <summary>Oblicza bounding box pikseli ON. Zwraca false gdy brak pikseli.</summary>
    static bool BBox(bool[,] px, int h, int w,
        out double minR, out double maxR, out double minC, out double maxC)
    {
        minR = h; maxR = 0; minC = w; maxC = 0;
        bool found = false;
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                if (px[r, c])
                {
                    found = true;
                    if (r < minR) minR = r;
                    if (r > maxR) maxR = r;
                    if (c < minC) minC = c;
                    if (c > maxC) maxC = c;
                }
        return found;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  SYNC ZAZNACZENIA NA WSZYSTKIE KLATKI
    // ════════════════════════════════════════════════════════════════════════

    bool _syncFrames = false;

    /// <summary>Steruje synchronizacją zaznaczenia na wszystkie klatki.</summary>
    public bool SyncFrames
    {
        get => _syncFrames;
        set => _syncFrames = value;
    }

    void ApplySyncCommitToAllFrames(PixelCanvasControl sourceCtrl, SelectionCommitArgs args)
    {
        var sourceTemplate = sourceCtrl.Template;
        int canW = sourceTemplate.Width;
        int canH = sourceTemplate.Height;

        for (int i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            if (frame == sourceTemplate) continue;  // pomijamy klatkę źródłową
            if (frame.Width != canW || frame.Height != canH) continue;

            var px = (bool[,])frame.Pixels.Clone();

            // 1. Wyczyść obszar źródłowy (skąd zaznaczenie było uniesione)
            int sx0 = Math.Max(0, args.SrcX), sy0 = Math.Max(0, args.SrcY);
            int sx1 = Math.Min(canW - 1, args.SrcX + args.SrcW - 1);
            int sy1 = Math.Min(canH - 1, args.SrcY + args.SrcH - 1);
            for (int r = sy0; r <= sy1; r++)
            for (int c = sx0; c <= sx1; c++)
                px[r, c] = false;

            // 2. Weź piksele z tej klatki (obszar SrcX,SrcY,SrcW,SrcH)
            int srcH = Math.Min(args.SrcH, canH - Math.Max(0, args.SrcY));
            int srcW = Math.Min(args.SrcW, canW - Math.Max(0, args.SrcX));
            if (srcH <= 0 || srcW <= 0) continue;

            var lifted = new bool[srcH, srcW];
            for (int r = 0; r < srcH; r++)
            for (int c = 0; c < srcW; c++)
            {
                int fr = Math.Max(0, args.SrcY) + r;
                int fc = Math.Max(0, args.SrcX) + c;
                if ((uint)fr < (uint)canH && (uint)fc < (uint)canW)
                    lifted[r, c] = frame.Pixels[fr, fc];
            }

            // 3. Zastosuj flip
            if (args.FlipH)
            {
                var tmp = new bool[srcH, srcW];
                for (int r = 0; r < srcH; r++)
                for (int c = 0; c < srcW; c++)
                    tmp[r, c] = lifted[r, srcW - 1 - c];
                lifted = tmp;
            }
            if (args.FlipV)
            {
                var tmp = new bool[srcH, srcW];
                for (int r = 0; r < srcH; r++)
                for (int c = 0; c < srcW; c++)
                    tmp[r, c] = lifted[srcH - 1 - r, c];
                lifted = tmp;
            }

            // 4. Zastosuj obrót
            if (Math.Abs(args.RotationAngle) > 0.5)
            {
                var (rotated, rw, rh) = SelectionState.RotatePixels(lifted, args.RotationAngle);
                lifted = rotated;
                srcW = rw; srcH = rh;
            }

            // 5. Skaluj do rozmiaru docelowego (DstW x DstH)
            int dstW = Math.Clamp(args.DstW, 1, canW);
            int dstH = Math.Clamp(args.DstH, 1, canH);
            if (lifted.GetLength(0) != dstH || lifted.GetLength(1) != dstW)
                lifted = ScalePixelBlock(lifted, dstH, dstW);

            // 6. Nałóż w miejscu docelowym (DstX, DstY)
            int dx = args.DstX, dy = args.DstY;
            for (int r = 0; r < dstH; r++)
            for (int c = 0; c < dstW; c++)
            {
                int tr = dy + r, tc = dx + c;
                if ((uint)tr < (uint)canH && (uint)tc < (uint)canW)
                    px[tr, tc] = lifted[r, c];
            }

            // Zapisz stan klatki na stos undo PRZED nadpisaniem (żeby Ctrl+Z działał na wszystkich klatkach)
            var undoSnap = (bool[,])frame.Pixels.Clone();
            frame.UndoStack.Push(undoSnap);
            frame.RedoStack.Clear();

            Array.Copy(px, frame.Pixels, px.Length);
            UpdateFilmstripThumb(i);
        }
        InvalidatePlayCache();
    }

    static bool[,] ScalePixelBlock(bool[,] src, int dstH, int dstW)
    {
        int srcH = src.GetLength(0), srcW = src.GetLength(1);
        var dst = new bool[dstH, dstW];
        for (int r = 0; r < dstH; r++)
        for (int c = 0; c < dstW; c++)
            dst[r, c] = src[r * srcH / dstH, c * srcW / dstW];
        return dst;
    }
}
