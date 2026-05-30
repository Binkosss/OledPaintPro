using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OledPaintPro.Drawing;

namespace OledPaintPro;

public partial class PixelCanvasControl : UserControl
{
    // ── Stan szablonu ────────────────────────────────────────────────────────
    public PixelTemplate Template { get; private set; }

    bool[,]  _pixels;
    bool[,]? _work;
    // Stosy undo/redo są przechowywane w Template.UndoStack / Template.RedoStack
    Stack<bool[,]> _undo => Template.UndoStack;
    Stack<bool[,]> _redo => Template.RedoStack;

    int W => Template.Width;
    int H => Template.Height;

    // ── Narzędzie — sync z MainWindow ────────────────────────────────────────
    DrawTool _activeTool = DrawTool.Pencil;
    public DrawTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == DrawTool.Select && value != DrawTool.Select && _sel.IsActive)
                CommitSelection();
            if (_activeTool == DrawTool.Text && value != DrawTool.Text)
                CommitTextOverlay();
            _activeTool = value;
            if (value == DrawTool.Text)
                CanvasImage.Cursor = Cursors.IBeam;
            else if (value != DrawTool.Select)
                CanvasImage.Cursor = Cursors.Arrow;
        }
    }

    // ── Oś symetrii (toggle, niezależne od aktywnego narzędzia) ─────────────
    bool _symV = false;
    bool _symH = false;

    public bool SymmetryV
    {
        get => _symV;
        set { _symV = value; RenderCanvas(); }
    }
    public bool SymmetryH
    {
        get => _symH;
        set { _symH = value; RenderCanvas(); }
    }

    // ── Zaznaczenie ──────────────────────────────────────────────────────────
    readonly SelectionState _sel = new();
    public bool HasActiveSelection => _sel.IsActive;

    /// <summary>Wewnętrzny schowek zaznaczenia (Ctrl+C/V).</summary>
    bool[,]? _clipboard;

    bool _dragging   = false;
    bool _useWork    = false;
    bool _paintValue = true;
    int  _startX, _startY, _prevX, _prevY, _scatterSeed;

    int _zoom = 8;
    const int ZoomMin = 2, ZoomMax = 24;

    // ── Rozmiar i kształt pędzla/gumki ──────────────────────────────────────
    int        _brushSize  = 1;
    BrushShape _brushShape = BrushShape.Square;

    public int BrushSize
    {
        get => _brushSize;
        set => _brushSize = Math.Clamp(value, 1, 20);
    }
    public BrushShape BrushShape
    {
        get => _brushShape;
        set => _brushShape = value;
    }

    readonly PixelRenderer _renderer = new();
    bool _ready = false;
    double _dpiX = 96.0, _dpiY = 96.0;

    public event Action<PixelCanvasControl>? SaveRequested;
    public event Action<PixelCanvasControl>? PixelsChanged;
    public event Action<string>? CoordChanged;

    /// <summary>Wyemitowane po zatwierdzeniu zaznaczenia — niesie geometrię transformacji.</summary>
    public event Action<PixelCanvasControl, SelectionCommitArgs>? SelectionCommitted;

    public bool[,] CurrentPixels => _pixels;

    // ════════════════════════════════════════════════════════════════════════

    public PixelCanvasControl(PixelTemplate template)
    {
        Template = template;
        _pixels  = CopyPixels(template.Pixels, template.Height, template.Width);
        // Synchronizuj piksele z template po wczytaniu stosów
        // (template.Pixels może być aktualny — nie czyścimy stosów)

        InitializeComponent();

        CanvasImage.MouseDown  += Canvas_MouseDown;
        CanvasImage.MouseMove  += Canvas_MouseMove;
        CanvasImage.MouseUp    += Canvas_MouseUp;
        CanvasImage.MouseLeave += (_, _) => { CoordLabel.Text = "—"; CoordChanged?.Invoke("—"); };
        CanvasImage.MouseWheel += Canvas_MouseWheel;

        Loaded += (_, _) =>
        {
            // Pobierz DPI ekranu
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                _dpiX = 96.0 * src.CompositionTarget.TransformToDevice.M11;
                _dpiY = 96.0 * src.CompositionTarget.TransformToDevice.M22;
            }
            // Blokuj Size_Changed podczas inicjalizacji pól tekstowych
            _ready         = false;
            WidthBox.Text  = W.ToString();
            HeightBox.Text = H.ToString();
            _ready         = true;
            RenderCanvas();
        };
    }

    static bool[,] CopyPixels(bool[,] src, int h, int w)
    {
        var dst = new bool[h, w];
        if (src.GetLength(0) >= h && src.GetLength(1) >= w)
            Array.Copy(src, dst, dst.Length);
        return dst;
    }

    /// <summary>
    /// Przełącza canvas na inny szablon bez niszczenia i tworzenia kontrolki od nowa.
    /// Eliminuje migotanie przy zmianie klatki animacji.
    /// </summary>
    public void SwitchTemplate(PixelTemplate template)
    {
        // Anuluj aktywne zaznaczenie / tekst
        if (_sel.IsActive) CancelSelection();
        if (_activeTool == DrawTool.Text) CommitTextOverlay();

        Template = template;
        _pixels  = CopyPixels(template.Pixels, template.Height, template.Width);
        _work    = null;
        _useWork = false;
        CanvasImage.Cursor = Cursors.Arrow;

        // Zaktualizuj pola rozmiaru bez triggera Size_Changed
        _ready = false;
        WidthBox.Text  = W.ToString();
        HeightBox.Text = H.ToString();
        _ready = true;

        RenderCanvas();
    }

    bool _showGrid8px  = true;
    bool _showGrid1px  = true;

    public void SetGrid(bool showGrid8px, bool showGrid1px)
    {
        _showGrid8px = showGrid8px;
        _showGrid1px = showGrid1px;
        RenderCanvas();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  RENDEROWANIE
    // ════════════════════════════════════════════════════════════════════════
    public void RenderCanvas()
    {
        var src = (_useWork && _work != null) ? _work : _pixels;
        bool showSel = _activeTool == DrawTool.Select && _sel.IsActive;
        bool showRot = showSel && _sel.Current == SelectionState.Phase.Moving;
        var (rhcx, rhcy) = showRot ? _sel.GetRotateHandleCanvasPos(_zoom) : (0, 0);
        var bmp = _renderer.Render(src, W, H, _zoom, showGrid1px: _showGrid1px, showGrid8px: _showGrid8px,
            floatPixels: _sel.FloatPixels, floatX: _sel.X, floatY: _sel.Y, floatW: _sel.W, floatH: _sel.H,
            showSelBorder: showSel, selBX: _sel.X, selBY: _sel.Y, selBW: _sel.W, selBH: _sel.H,
            showSymV: _symV, showSymH: _symH,
            showRotHandle: showRot, rotHandleCanvasX: rhcx, rotHandleCanvasY: rhcy,
            dpiX: _dpiX, dpiY: _dpiY);
        // Logiczny rozmiar = fizyczne piksele / skala DPI → brak skalowania przez WPF
        CanvasImage.Width  = bmp.PixelWidth  / (_dpiX / 96.0);
        CanvasImage.Height = bmp.PixelHeight / (_dpiY / 96.0);
        CanvasImage.Source = bmp;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MYSZ
    // ════════════════════════════════════════════════════════════════════════
    (int px, int py) PixelAt(MouseEventArgs e)
    {
        var pt = e.GetPosition(CanvasImage);
        // CanvasImage.Width = W * _zoom / dpiScale → logical px per OLED pixel = _zoom / dpiScale
        double logZoomX = _zoom / (_dpiX / 96.0);
        double logZoomY = _zoom / (_dpiY / 96.0);
        return (Math.Clamp((int)(pt.X / logZoomX), 0, W - 1),
                Math.Clamp((int)(pt.Y / logZoomY), 0, H - 1));
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        bool leftBtn  = e.LeftButton  == MouseButtonState.Pressed;
        bool rightBtn = e.RightButton == MouseButtonState.Pressed;
        if (!leftBtn && !rightBtn) return;

        var (px, py) = PixelAt(e);

        // ── Tekst — NIE przechwytuj myszy, TextBox potrzebuje fokusa ─────────
        if (_activeTool == DrawTool.Text)
        {
            if (!leftBtn) return;
            CommitTextOverlay();   // zatwierdź poprzedni (jeśli był)
            ShowTextBox(px, py);
            return;
        }

        CanvasImage.CaptureMouse();

        // ── Zaznaczenie ──────────────────────────────────────────────────────
        if (_activeTool == DrawTool.Select)
        {
            if (!leftBtn) { CommitSelection(); CanvasImage.ReleaseMouseCapture(); return; }

            if (_sel.Current == SelectionState.Phase.Moving)
            {
                var handle = _sel.GetHandleAt(px, py, hitRadiusPx: Math.Max(1, 4 / _zoom), zoom: _zoom);
                if (handle == SelectionState.HandleKind.Rotate)
                {
                    _sel.BeginRotate(px, py);
                    _dragging = true;
                    RenderCanvas();
                    return;
                }
                if (handle != SelectionState.HandleKind.None)
                {
                    _sel.BeginScale(handle, px, py);
                    _dragging = true;
                    RenderCanvas();
                    return;
                }
                if (_sel.HitTest(px, py))
                {
                    _sel.BeginMove(px, py);
                    _dragging = true;
                    RenderCanvas();
                    return;
                }
            }

            if (_sel.IsActive) CommitSelection();
            _sel.BeginDefine(px, py);
            _dragging = true;
            RenderCanvas();
            return;
        }

        if (_dragging) { CanvasImage.ReleaseMouseCapture(); return; }
        _dragging = true;
        _startX = px; _startY = py; _prevX = px; _prevY = py;
        _scatterSeed = Environment.TickCount;
        _paintValue  = (_activeTool == DrawTool.Eraser) ? !leftBtn : leftBtn;

        SaveUndo();

        if (_activeTool == DrawTool.FloodFill)
        {
            PixelDraw.FloodFill(_pixels, px, py, _paintValue, W, H);
            _useWork = false; _dragging = false;
            CanvasImage.ReleaseMouseCapture();
            RenderCanvas();
            PixelsChanged?.Invoke(this);
        }
        else if (PixelDraw.IsShapeTool(_activeTool))
        {
            _work = new bool[H, W];
            Array.Copy(_pixels, _work, _pixels.Length);
            PixelDraw.ApplyShape(_work, _activeTool, px, py, px, py, _paintValue, W, H, _scatterSeed, ActiveStampTemplate);
            _useWork = true;
            RenderCanvas();
        }
        else
        {
            PixelDraw.DrawBrush(_pixels, px, py, _brushSize, _brushShape, _paintValue, W, H);
            DrawSymmetryMirror(px, py);
            _useWork = false;
            RenderCanvas();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var (px, py) = PixelAt(e);
        CoordLabel.Text = $"x={px}  y={py}";
        CoordChanged?.Invoke($"x={px,3}  y={py,2}  \u2502  byte[{px / 8}] bit{7 - px % 8}");

        if (_activeTool == DrawTool.Select)
        {
            if (_sel.Current == SelectionState.Phase.Moving && !_dragging)
            {
                var handle = _sel.GetHandleAt(px, py, hitRadiusPx: Math.Max(1, 4 / _zoom), zoom: _zoom);
                CanvasImage.Cursor = handle switch
                {
                    SelectionState.HandleKind.Rotate                               => Cursors.Hand,
                    SelectionState.HandleKind.TL or SelectionState.HandleKind.BR   => Cursors.SizeNWSE,
                    SelectionState.HandleKind.TR or SelectionState.HandleKind.BL   => Cursors.SizeNESW,
                    SelectionState.HandleKind.T  or SelectionState.HandleKind.B    => Cursors.SizeNS,
                    SelectionState.HandleKind.L  or SelectionState.HandleKind.R    => Cursors.SizeWE,
                    _ => _sel.HitTest(px, py) ? Cursors.SizeAll : Cursors.Cross,
                };
            }
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
            else if (_sel.Current == SelectionState.Phase.Scaling)
            {
                bool lockAspect = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                _sel.UpdateScale(px, py, W, H, lockAspect);
                RenderCanvas();
            }
            else if (_sel.Current == SelectionState.Phase.Rotating)
            {
                _sel.UpdateRotate(px, py, W, H);
                RenderCanvas();
            }
            return;
        }

        if (!_dragging) return;

        if (_activeTool == DrawTool.Pencil || _activeTool == DrawTool.Eraser)
        {
            PixelDraw.DrawThickLine(_pixels, _prevX, _prevY, px, py, _brushSize, _brushShape, _paintValue, W, H);
            DrawSymmetryMirrorLine(_prevX, _prevY, px, py);
            _prevX = px; _prevY = py;
            RenderCanvas();
            PixelsChanged?.Invoke(this);
        }
        else if (PixelDraw.IsShapeTool(_activeTool))
        {
            _work = new bool[H, W];
            Array.Copy(_pixels, _work, _pixels.Length);
            int ex = px, ey = py;
            // Shift + stempel → zachowaj proporcje oryginalnego szablonu
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                && ActiveStampTemplate != null
                && _activeTool is DrawTool.EyeStamp or DrawTool.MouthStamp
                                or DrawTool.OtherStamp or DrawTool.BitmapStamp)
            {
                int dx = ex - _startX, dy = ey - _startY;
                double ratio = ActiveStampTemplate.Width > 0
                    ? (double)ActiveStampTemplate.Height / ActiveStampTemplate.Width
                    : 1.0;
                int adx = Math.Abs(dx), ady = Math.Abs(dy);
                if (adx * ratio >= ady)
                    ey = _startY + (int)Math.Round(adx * ratio) * Math.Sign(dy == 0 ? 1 : dy);
                else
                    ex = _startX + (int)Math.Round(ady / ratio) * Math.Sign(dx == 0 ? 1 : dx);
            }
            PixelDraw.ApplyShape(_work, _activeTool, _startX, _startY, ex, ey, _paintValue, W, H, _scatterSeed, ActiveStampTemplate);
            RenderCanvas();
        }
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeTool == DrawTool.Select)
        {
            if (!_dragging) return;
            _dragging = false;
            CanvasImage.ReleaseMouseCapture();
            var (px2, py2) = PixelAt(e);
            if (_sel.Current == SelectionState.Phase.Defining)
            {
                _sel.UpdateDefine(px2, py2);
                if (_sel.W > 1 || _sel.H > 1)
                {
                    SaveUndo();
                    if (!_sel.Lift(_pixels, W, H)) _undo.Pop();
                }
                else
                    _sel.Reset();
            }
            else if (_sel.Current == SelectionState.Phase.Scaling)
            {
                _sel.EndScale();
            }
            else if (_sel.Current == SelectionState.Phase.Rotating)
            {
                _sel.EndRotate();
            }
            RenderCanvas();
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        CanvasImage.ReleaseMouseCapture();
        if (PixelDraw.IsShapeTool(_activeTool) && _work != null)
        {
            // Przy Shift + stempel: przelicz ostateczny prostokąt z zachowaniem proporcji
            var (px2, py2) = PixelAt(e);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                && ActiveStampTemplate != null
                && _activeTool is DrawTool.EyeStamp or DrawTool.MouthStamp
                                or DrawTool.OtherStamp or DrawTool.BitmapStamp)
            {
                int dx = px2 - _startX, dy = py2 - _startY;
                double ratio = ActiveStampTemplate.Width > 0
                    ? (double)ActiveStampTemplate.Height / ActiveStampTemplate.Width
                    : 1.0;
                int adx = Math.Abs(dx), ady = Math.Abs(dy);
                if (adx * ratio >= ady)
                    py2 = _startY + (int)Math.Round(adx * ratio) * Math.Sign(dy == 0 ? 1 : dy);
                else
                    px2 = _startX + (int)Math.Round(ady / ratio) * Math.Sign(dx == 0 ? 1 : dx);
                _work = new bool[H, W];
                Array.Copy(_pixels, _work, _pixels.Length);
                PixelDraw.ApplyShape(_work, _activeTool, _startX, _startY, px2, py2, _paintValue, W, H, _scatterSeed, ActiveStampTemplate);
            }
            // Oblicz różnicę (co zostało narysowane) i odbij ją symetrycznie
            if (_symV || _symH)
            {
                var diff = new bool[H, W];
                for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    diff[r, c] = _work[r, c] && !_pixels[r, c]; // piksele dodane przez kształt
                Array.Copy(_work, _pixels, _pixels.Length);
                ApplySymmetryDiff(diff);
            }
            else
            {
                Array.Copy(_work, _pixels, _pixels.Length);
            }
            _useWork = false;
        }
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    // ── Symetria i odbicia ───────────────────────────────────────────────────

    /// <summary>Rysuje lustrzany punkt przy aktywnej osi symetrii (pojedynczy piksel/pędzel).</summary>
    void DrawSymmetryMirror(int px, int py)
    {
        if (_symV)
            PixelDraw.DrawBrush(_pixels, W - 1 - px, py, _brushSize, _brushShape, _paintValue, W, H);
        if (_symH)
            PixelDraw.DrawBrush(_pixels, px, H - 1 - py, _brushSize, _brushShape, _paintValue, W, H);
        if (_symV && _symH)
            PixelDraw.DrawBrush(_pixels, W - 1 - px, H - 1 - py, _brushSize, _brushShape, _paintValue, W, H);
    }

    /// <summary>Rysuje lustrzaną linię ciągłą przy aktywnej osi symetrii.</summary>
    void DrawSymmetryMirrorLine(int x0, int y0, int x1, int y1)
    {
        if (_symV)
            PixelDraw.DrawThickLine(_pixels, W - 1 - x0, y0, W - 1 - x1, y1, _brushSize, _brushShape, _paintValue, W, H);
        if (_symH)
            PixelDraw.DrawThickLine(_pixels, x0, H - 1 - y0, x1, H - 1 - y1, _brushSize, _brushShape, _paintValue, W, H);
        if (_symV && _symH)
            PixelDraw.DrawThickLine(_pixels, W - 1 - x0, H - 1 - y0, W - 1 - x1, H - 1 - y1, _brushSize, _brushShape, _paintValue, W, H);
    }

    /// <summary>Nakłada odbicia pikseli z diff (dodane przez kształt) na _pixels.</summary>
    void ApplySymmetryDiff(bool[,] diff)
    {
        for (int r = 0; r < H; r++)
        for (int c = 0; c < W; c++)
        {
            if (!diff[r, c]) continue;
            if (_symV)  _pixels[r, W - 1 - c] = true;
            if (_symH)  _pixels[H - 1 - r, c] = true;
            if (_symV && _symH) _pixels[H - 1 - r, W - 1 - c] = true;
        }
    }

    /// <summary>
    /// Czyści piksele w lustrzanych prostokątach starej pozycji zaznaczenia,
    /// żeby przesunięcie z symetrią nie zostawiało duplikatów odbić.
    /// </summary>
    void ClearOldSymmetryMirrors(int lx, int ly, int lw, int lh)
    {
        // Odbicie V (lustro pionowe) — prostokąt symetryczny względem X
        int mirrorLxV = W - lx - lw;
        // Odbicie H (lustro poziome) — prostokąt symetryczny względem Y
        int mirrorLyH = H - ly - lh;

        for (int row = 0; row < lh; row++)
        for (int col = 0; col < lw; col++)
        {
            if (_symV)
            {
                int nx = mirrorLxV + col, ny = ly + row;
                if ((uint)nx < (uint)W && (uint)ny < (uint)H)
                    _pixels[ny, nx] = false;
            }
            if (_symH)
            {
                int nx = lx + col, ny = mirrorLyH + row;
                if ((uint)nx < (uint)W && (uint)ny < (uint)H)
                    _pixels[ny, nx] = false;
            }
            if (_symV && _symH)
            {
                int nx = mirrorLxV + col, ny = mirrorLyH + row;
                if ((uint)nx < (uint)W && (uint)ny < (uint)H)
                    _pixels[ny, nx] = false;
            }
        }
    }

    /// <summary>
    /// Wkleja lustrzone kopie uniesionych pikseli zaznaczenia na canvas.
    /// Odbicie V: X → (W-1 - (fx + fw-1)) + (col) = W - fx - fw + col → X = W - fx - fw, col taki sam
    /// Odbicie H: Y → analogicznie
    /// </summary>
    void ApplySelectionSymmetry(bool[,] floatPx, int fx, int fy, int fw, int fh)
    {
        // Odbicie V (lustro pionowe): nowy X startowy
        int mirrorFxV = W - fx - fw;
        // Odbicie H (lustro poziome): nowy Y startowy
        int mirrorFyH = H - fy - fh;

        for (int row = 0; row < fh; row++)
        for (int col = 0; col < fw; col++)
        {
            bool pix = floatPx[row, col];

            if (_symV)
            {
                int nx = mirrorFxV + (fw - 1 - col);
                int ny = fy + row;
                if ((uint)nx < (uint)W && (uint)ny < (uint)H)
                    _pixels[ny, nx] = pix;
            }
            if (_symH)
            {
                int nx = fx + col;
                int ny = mirrorFyH + (fh - 1 - row);
                if ((uint)nx < (uint)W && (uint)ny < (uint)H)
                    _pixels[ny, nx] = pix;
            }
            if (_symV && _symH)
            {
                int nx = mirrorFxV + (fw - 1 - col);
                int ny = mirrorFyH + (fh - 1 - row);
                if ((uint)nx < (uint)W && (uint)ny < (uint)H)
                    _pixels[ny, nx] = pix;
            }
        }
    }

    public void ApplyFlipH()
    {
        SaveUndo();
        var dst = new bool[H, W];
        for (int r = 0; r < H; r++)
        for (int c = 0; c < W; c++)
            dst[r, W - 1 - c] = _pixels[r, c];
        Array.Copy(dst, _pixels, _pixels.Length);
        _useWork = false;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    public void ApplyFlipV()
    {
        SaveUndo();
        var dst = new bool[H, W];
        for (int r = 0; r < H; r++)
        for (int c = 0; c < W; c++)
            dst[H - 1 - r, c] = _pixels[r, c];
        Array.Copy(dst, _pixels, _pixels.Length);
        _useWork = false;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    // ── Zaznaczenie ──────────────────────────────────────────────────────────

    /// <summary>Usuwa zaznaczony obszar (float piksele są odrzucane — canvas już wyczyszczony pod nimi).</summary>
    public void DeleteSelection()
    {
        if (!_sel.IsActive) return;
        _sel.Reset();
        CanvasImage.Cursor = Cursors.Arrow;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    /// <summary>Zwraca kopię pikseli aktywnego zaznaczenia (lub null gdy brak).</summary>
    public bool[,]? GetSelectionPixels() =>
        _sel.FloatPixels != null ? (bool[,])_sel.FloatPixels.Clone() : null;

    /// <summary>Kopiuje zaznaczony obszar do wewnętrznego schowka.</summary>
    public void CopySelection()
    {
        if (_sel.FloatPixels == null) return;
        _clipboard = (bool[,])_sel.FloatPixels.Clone();
    }

    /// <summary>Wkleja zewnętrzny bufor jako floating-selection (schowek globalny między zakładkami).</summary>
    public void PasteExternal(bool[,] data)
    {
        if (_sel.IsActive) CommitSelection();
        int pw = data.GetLength(1), ph = data.GetLength(0);
        int placeX = Math.Max(0, (W - pw) / 2);
        int placeY = Math.Max(0, (H - ph) / 2);
        _sel.LiftExternal(data, placeX, placeY, W, H);
        CanvasImage.Cursor = Cursors.SizeAll;
        RenderCanvas();
    }

    /// <summary>Wkleja zawartość schowka jako nowe floating-selection (możliwość przesunięcia/skalowania).</summary>
    public void PasteFromClipboard()
    {
        if (_clipboard == null) return;
        // Zatwierdź bieżące zaznaczenie
        if (_sel.IsActive) CommitSelection();
        // Ustaw nowe floating-selection ze schowka — wyśrodkowane na canvasie
        int pw = _clipboard.GetLength(1), ph = _clipboard.GetLength(0);
        int placeX = Math.Max(0, (W - pw) / 2);
        int placeY = Math.Max(0, (H - ph) / 2);
        _sel.LiftExternal(_clipboard, placeX, placeY, W, H);
        CanvasImage.Cursor = Cursors.SizeAll;
        RenderCanvas();
    }

    public void CommitSelection()
    {
        if (!_sel.IsActive) return;

        // Zapamiętaj dane przed commitem (potrzebne do odbić symetrii)
        var floatPx = _sel.FloatPixels;
        int fx = _sel.X, fy = _sel.Y, fw = _sel.W, fh = _sel.H;

        // Zbuduj args PRZED commitem (żeby mieć stan przed wyczyszczeniem)
        var args = new SelectionCommitArgs
        {
            SrcX  = _sel.LiftX,  SrcY = _sel.LiftY,
            SrcW  = _sel.LiftW,  SrcH = _sel.LiftH,
            DstX  = _sel.X,      DstY = _sel.Y,
            DstW  = _sel.W,      DstH = _sel.H,
            RotationAngle = _sel.RotationAngle,
            FlipH = _sel.FlipH,
            FlipV = _sel.FlipV,
        };

        // Jeśli aktywna symetria — wyczyść stare odbicia (z miejsca przed przesunięciem)
        if (floatPx != null && (_symV || _symH))
            ClearOldSymmetryMirrors(_sel.LiftX, _sel.LiftY, _sel.LiftW, _sel.LiftH);

        _sel.CommitTo(_pixels, W, H);

        // Wklej nowe odbicia w nowym miejscu
        if (floatPx != null && (_symV || _symH))
            ApplySelectionSymmetry(floatPx, fx, fy, fw, fh);

        _useWork = false;
        CanvasImage.Cursor = Cursors.Arrow;
        RenderCanvas();
        PixelsChanged?.Invoke(this);

        // Emit sync event (gdy zaznaczenie było aktywne i przeniesione/przekształcone)
        if (floatPx != null)
            SelectionCommitted?.Invoke(this, args);
    }

    public void CancelSelection()
    {
        if (!_sel.IsActive) return;
        var snap = _sel.CancelAndGetSnapshot();
        if (snap != null && snap.GetLength(0) == H && snap.GetLength(1) == W)
            Array.Copy(snap, _pixels, _pixels.Length);
        _useWork = false;
        CanvasImage.Cursor = Cursors.Arrow;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    // ── Aktywny szablon stempla (sync z MainWindow) ───────────────────────────
    public PixelTemplate? ActiveStampTemplate { get; set; }

    // ════════════════════════════════════════════════════════════════════════
    //  ZOOM / UNDO / CLEAR
    // ════════════════════════════════════════════════════════════════════════
    void ChangeZoom(int delta)
    {
        _zoom = Math.Clamp(_zoom + delta, ZoomMin, ZoomMax);
        _renderer.Invalidate();
        RenderCanvas();
        ZoomChanged?.Invoke(_zoom);
    }

    public event Action<int>? ZoomChanged;

    public int CurrentZoom => _zoom;

    public void ZoomBy(int delta) => ChangeZoom(delta);

    public void SetBrushSize(int size)  => BrushSize  = size;
    public void SetBrushShape(BrushShape shape) => BrushShape = shape;

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            // Shift+scroll = przesuwanie poziome
            CanvasScroll.ScrollToHorizontalOffset(
                CanvasScroll.HorizontalOffset - e.Delta * 0.5);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Ctrl+scroll = przesuwanie pionowe (alternatywa)
            CanvasScroll.ScrollToVerticalOffset(
                CanvasScroll.VerticalOffset - e.Delta * 0.5);
        }
        else
        {
            ChangeZoom(e.Delta > 0 ? 1 : -1);
        }
        e.Handled = true;
    }

    void SaveUndo()
    {
        var snap = new bool[H, W];
        Array.Copy(_pixels, snap, _pixels.Length);
        _undo.Push(snap);
        _redo.Clear();
    }

    /// <summary>Zapisuje bieżące stosy undo/redo z powrotem do szablonu (wywoływane przy zmianie klatki).</summary>
    public void FlushUndoToTemplate()
    {
        // Stosy są już przechowywane bezpośrednio w Template — nic do zrobienia.
        // Metoda istnieje jako hook dla potencjalnych przyszłych rozszerzeń.
    }

    public void Undo()
    {
        _sel.Reset(); CanvasImage.Cursor = Cursors.Arrow;
        if (_undo.Count == 0) return;
        var snap = new bool[H, W];
        Array.Copy(_pixels, snap, _pixels.Length);
        _redo.Push(snap);
        Array.Copy(_undo.Pop(), _pixels, _pixels.Length);
        _useWork = false;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    public void Redo()
    {
        _sel.Reset(); CanvasImage.Cursor = Cursors.Arrow;
        if (_redo.Count == 0) return;
        var snap = new bool[H, W];
        Array.Copy(_pixels, snap, _pixels.Length);
        _undo.Push(snap);
        Array.Copy(_redo.Pop(), _pixels, _pixels.Length);
        _useWork = false;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    public void Clear()
    {
        SaveUndo();
        Array.Clear(_pixels);
        _useWork = false;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    public void Invert()
    {
        SaveUndo();
        for (int r = 0; r < H; r++)
        for (int c = 0; c < W; c++)
            _pixels[r, c] = !_pixels[r, c];
        _useWork = false;
        RenderCanvas();
        PixelsChanged?.Invoke(this);
    }

    private void Undo_Click(object s, RoutedEventArgs e) => Undo();

    private void Clear_Click(object s, RoutedEventArgs e) => Clear();

    // ════════════════════════════════════════════════════════════════════════
    //  ROZMIAR
    // ════════════════════════════════════════════════════════════════════════
    private void Size_Changed(object sender, TextChangedEventArgs e)    {
        if (!_ready) return;
        if (!int.TryParse(WidthBox.Text,  out int nw) || nw < 4 || nw > 256) return;
        if (!int.TryParse(HeightBox.Text, out int nh) || nh < 4 || nh > 128) return;
        if (nw == W && nh == H) return;
        ResizeCanvas(nw, nh);
    }

    public void ResizeCanvas(int nw, int nh)
    {
        if (nw < 4 || nw > 256 || nh < 4 || nh > 128) return;
        if (nw == W && nh == H) return;
        var resized = new bool[nh, nw];
        for (int r = 0; r < Math.Min(H, nh); r++)
        for (int c = 0; c < Math.Min(W, nw); c++)
            resized[r, c] = _pixels[r, c];

        Template.Width  = nw;
        Template.Height = nh;
        _pixels = resized;
        _undo.Clear();
        _renderer.Invalidate();
        if (_ready)
        {
            WidthBox.Text  = nw.ToString();
            HeightBox.Text = nh.ToString();
        }
        RenderCanvas();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  NARZĘDZIE TEKSTOWE
    // ════════════════════════════════════════════════════════════════════════

    TextBox?   _textBox;        // aktywny TextBox na nakładce
    int        _textPixelX;     // pozycja tekstury w pikselach kanwy
    int        _textPixelY;
    bool       _textDragging;
    bool       _textCommitting; // guard przed podwójnym commitem
    System.Windows.Point _textDragStart;

    // Bieżące opcje tekstu — ustawiane z MainWindow przez SetTextOptions()
    string _textFont   = "Consolas";
    double _textSize   = 8;
    bool   _textBold   = false;
    bool   _textItalic = false;
    bool   _textWhite  = true;

    string GetTextFont()   => _textFont;
    double GetTextSize()   => _textSize;
    bool   GetTextBold()   => _textBold;
    bool   GetTextItalic() => _textItalic;
    bool   GetTextWhite()  => _textWhite;

    public void SetTextOptions(string font, double size, bool bold, bool italic, bool white)
    {
        _textFont = font; _textSize = size; _textBold = bold; _textItalic = italic; _textWhite = white;
        if (_textBox == null) return;
        _textBox.FontFamily = new FontFamily(_textFont);
        _textBox.FontSize   = _textSize * _zoom;
        _textBox.FontWeight = _textBold   ? FontWeights.Bold   : FontWeights.Normal;
        _textBox.FontStyle  = _textItalic ? FontStyles.Italic  : FontStyles.Normal;
        _textBox.Foreground = _textWhite  ? Brushes.White      : Brushes.Black;
    }

    Border? _textContainer; // kontener z uchwytem do przeciągania

    void ShowTextBox(int pixelX, int pixelY)
    {
        _textPixelX = pixelX;
        _textPixelY = pixelY;

        var tb = new TextBox
        {
            AcceptsReturn       = true,
            TextWrapping        = TextWrapping.NoWrap,
            Text                = "text",
            Background          = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x11, 0x22)),
            Foreground          = GetTextWhite() ? Brushes.White : Brushes.Black,
            CaretBrush          = Brushes.LimeGreen,
            BorderThickness     = new Thickness(0),
            FontFamily          = new FontFamily(GetTextFont()),
            FontSize            = GetTextSize() * _zoom,
            FontWeight          = GetTextBold()   ? FontWeights.Bold   : FontWeights.Normal,
            FontStyle           = GetTextItalic() ? FontStyles.Italic  : FontStyles.Normal,
            Padding             = new Thickness(3, 2, 3, 2),
            MinWidth            = 20,
            MinHeight           = 10,
        };

        // Nagłówek-uchwyt do przeciągania
        var header = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x22, 0x55, 0x33)),
            Height          = 12,
            Cursor          = Cursors.SizeAll,
            Child           = new TextBlock
            {
                Text       = "⠿ przesuń",
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0xFF, 0x88)),
                FontSize   = 8,
                Margin     = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var vstack = new StackPanel { Orientation = Orientation.Vertical };
        vstack.Children.Add(header);
        vstack.Children.Add(tb);

        var container = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0xFF, 0x88)),
            BorderThickness = new Thickness(1),
            Background      = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x11, 0x22)),
            Child           = vstack,
        };

        Canvas.SetLeft(container, pixelX * _zoom);
        Canvas.SetTop(container,  pixelY * _zoom);

        // Przeciąganie za nagłówek
        header.MouseLeftButtonDown += TextBox_DragStart;
        header.MouseMove           += TextBox_DragMove;
        header.MouseLeftButtonUp   += TextBox_DragEnd;

        tb.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                CancelTextOverlay();
                ev.Handled = true;
            }
            else if (ev.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CommitTextOverlay();
                ev.Handled = true;
            }
        };

        TextOverlay.IsHitTestVisible = true;
        TextOverlay.Children.Clear();
        TextOverlay.Children.Add(container);
        _textBox       = tb;
        _textContainer = container;

        tb.Loaded += (_, _) =>
        {
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        };
        if (tb.IsLoaded)
        {
            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        }
    }

    private void TextBox_DragStart(object sender, MouseButtonEventArgs e)
    {
        _textDragging  = true;
        _textDragStart = e.GetPosition(TextOverlay);
        (sender as UIElement)?.CaptureMouse();
        e.Handled = true;
    }

    private void TextBox_DragMove(object sender, MouseEventArgs e)
    {
        if (!_textDragging || _textContainer == null) return;
        var pos     = e.GetPosition(TextOverlay);
        double newL = Canvas.GetLeft(_textContainer) + (pos.X - _textDragStart.X);
        double newT = Canvas.GetTop(_textContainer)  + (pos.Y - _textDragStart.Y);
        Canvas.SetLeft(_textContainer, Math.Max(0, newL));
        Canvas.SetTop(_textContainer,  Math.Max(0, newT));
        _textDragStart = pos;
        _textPixelX = (int)(Canvas.GetLeft(_textContainer) / _zoom);
        _textPixelY = (int)(Canvas.GetTop(_textContainer)  / _zoom);
        e.Handled = true;
    }

    private void TextBox_DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_textDragging)
        {
            _textDragging = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            _textBox?.Focus();
        }
    }

    private void TextCommitOnClickOutside(object sender, MouseButtonEventArgs e)
    {
        // Nieużywane — commit przez LostFocus
    }

    void CancelTextOverlay()
    {
        TextOverlay.IsHitTestVisible = false;
        TextOverlay.Children.Clear();
        _textBox       = null;
        _textContainer = null;
    }

    public void CommitTextOverlay()
    {
        if (_textCommitting || _textBox == null) return;
        _textCommitting = true;
        string text = _textBox.Text.Trim('\r', '\n');
        if (text.Length > 0)
        {
            SaveUndo();
            BurnTextToPixels(text, _textPixelX, _textPixelY,
                             GetTextFont(), GetTextSize(), GetTextBold(), GetTextItalic(), GetTextWhite());
        }
        CancelTextOverlay();
        RenderCanvas();
        PixelsChanged?.Invoke(this);
        _textCommitting = false;
    }

    void BurnTextToPixels(string text, int startX, int startY,
                          string fontName, double emSize, bool bold, bool italic, bool white)
    {
        // Renderuj tekst do DrawingVisual, skopiuj piksele do _pixels
        var weight = bold   ? FontWeights.Bold   : FontWeights.Normal;
        var style  = italic ? FontStyles.Italic  : FontStyles.Normal;
        var tf = new Typeface(new FontFamily(fontName), style, weight, FontStretches.Normal);

        // Rozmiar w pt przeliczony tak, żeby 1px kanwy = 1px fizyczny (zoom=1)
        double dpi = 96.0;

        var ft = new FormattedText(
            text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, emSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        ft.MaxTextWidth = 3500000;

        int tw = (int)Math.Ceiling(ft.Width);
        int th = (int)Math.Ceiling(ft.Height);
        if (tw <= 0 || th <= 0) return;

        // Renderuj na małą WriteableBitmap 1px/piksel
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawText(ft, new System.Windows.Point(0, 0));
        }

        var rtb = new RenderTargetBitmap(tw, th, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);

        int stride = tw * 4;
        var buf    = new byte[th * stride];
        rtb.CopyPixels(buf, stride, 0);

        // Przepisz jasne piksele jako białe/czarne na bool[,]
        for (int row = 0; row < th; row++)
        {
            int py = startY + row;
            if (py < 0 || py >= H) continue;
            for (int col = 0; col < tw; col++)
            {
                int px = startX + col;
                if (px < 0 || px >= W) continue;
                int offset = row * stride + col * 4;
                // A kanał (alfa) decyduje czy piksel jest zajęty
                byte a = buf[offset + 3];
                if (a > 64)
                    _pixels[py, px] = white;  // true = biały, false = czarny
            }
        }
    }

    // TextOption_Changed obsługiwane teraz przez MainWindow (kontrolki przeniesione do prawego panelu)
}
