namespace OledPaintPro.Drawing;

/// <summary>
/// Opis geometrycznej transformacji zaznaczenia — używany do powielenia tej samej
/// operacji na wszystkich klatkach animacji (każda klatka "liftuje" własne piksele
/// i stosuje identyczną transformację).
/// </summary>
public sealed class SelectionCommitArgs
{
    /// <summary>Prostokąt źródłowy — skąd zostały uniesione piksele (canvas-px).</summary>
    public int SrcX { get; init; }
    public int SrcY { get; init; }
    public int SrcW { get; init; }
    public int SrcH { get; init; }

    /// <summary>Prostokąt docelowy — gdzie złożono (canvas-px).</summary>
    public int DstX { get; init; }
    public int DstY { get; init; }
    public int DstW { get; init; }
    public int DstH { get; init; }

    /// <summary>Kąt obrotu w stopniach (0 = brak obrotu).</summary>
    public double RotationAngle { get; init; }

    /// <summary>Czy zastosowano odbicie poziome.</summary>
    public bool FlipH { get; init; }

    /// <summary>Czy zastosowano odbicie pionowe.</summary>
    public bool FlipV { get; init; }
}

/// <summary>
/// Zarządza stanem narzędzia Zaznaczanie.
/// Fazy: None → Defining → Moving → Scaling / Rotating → Moving → (commit) → None
/// </summary>
public sealed class SelectionState
{
    public enum Phase { None, Defining, Moving, Scaling, Rotating }

    /// <summary>Rodzaj uchwytu (narożniki, środki boków, obracanie).</summary>
    public enum HandleKind { None, TL, T, TR, R, BR, B, BL, L, Rotate }

    public Phase    Current     { get; private set; } = Phase.None;
    public int      X           { get; private set; }
    public int      Y           { get; private set; }
    public int      W           { get; private set; }
    public int      H           { get; private set; }

    /// <summary>Uniesione piksele zaznaczenia (null gdy faza == None/Defining).</summary>
    public bool[,]? FloatPixels { get; private set; }

    public bool IsActive => Current != Phase.None;
    public bool HasFloat => FloatPixels != null;

    /// <summary>Kąt obrotu zaznaczenia w stopniach.</summary>
    public double RotationAngle { get; private set; } = 0.0;

    /// <summary>Czy zaznaczenie zostało odbite poziomo (parzystość = efektywny stan).</summary>
    public bool FlipH { get; private set; } = false;
    /// <summary>Czy zaznaczenie zostało odbite pionowo.</summary>
    public bool FlipV { get; private set; } = false;

    private int      _anchorX,  _anchorY;
    private int      _moveOffX, _moveOffY;
    private bool[,]? _baseSnapshot;   // kopia pixels przed uniesieniem (do Cancel)

    /// <summary>Oryginalna pozycja i rozmiar zaznaczenia w chwili Lift (do czyszczenia starych odbić).</summary>
    public int LiftX { get; private set; }
    public int LiftY { get; private set; }
    public int LiftW { get; private set; }
    public int LiftH { get; private set; }

    // ── Skalowanie ───────────────────────────────────────────────────────────
    private HandleKind _scaleHandle;
    private int        _scaleStartPx, _scaleStartPy;
    private int        _scaleOrigX, _scaleOrigY, _scaleOrigW, _scaleOrigH;
    private bool[,]?   _scaleOrigFloat;

    // ── Obrót ────────────────────────────────────────────────────────────────
    private bool[,]?  _sourceForRotation;   // oryginalne piksele przed obrotem
    private double    _rotOrigCenterX;       // centrum zaznaczenia przy starcie obrotu (canvas px)
    private double    _rotOrigCenterY;
    private double    _rotStartAngle;        // kąt myszy przy starcie (radiany)
    private double    _rotBaseAngle;         // RotationAngle przy starcie obrotu

    // ── Faza 1: Definiowanie prostokąta ─────────────────────────────────────

    public void BeginDefine(int anchorX, int anchorY)
    {
        Current  = Phase.Defining;
        _anchorX = anchorX;
        _anchorY = anchorY;
        X = anchorX; Y = anchorY; W = 1; H = 1;
        FloatPixels        = null;
        _baseSnapshot      = null;
        _sourceForRotation = null;
        RotationAngle      = 0.0;
        FlipH = false;
        FlipV = false;
    }

    public void UpdateDefine(int curX, int curY)
    {
        X = Math.Min(_anchorX, curX);
        Y = Math.Min(_anchorY, curY);
        W = Math.Abs(curX - _anchorX) + 1;
        H = Math.Abs(curY - _anchorY) + 1;
    }

    // ── Unoszenie pikseli z canvasu ──────────────────────────────────────────

    /// <summary>
    /// Kopiuje zaznaczony obszar z <paramref name="pixels"/>, czyści go i przechodzi do fazy Moving.
    /// Zwraca false gdy zaznaczenie jest puste.
    /// </summary>
    public bool Lift(bool[,] pixels, int canW, int canH)
    {
        int x0 = Math.Max(0, X), y0 = Math.Max(0, Y);
        int x1 = Math.Min(canW - 1, X + W - 1);
        int y1 = Math.Min(canH - 1, Y + H - 1);
        if (x0 > x1 || y0 > y1) { Current = Phase.None; return false; }

        X = x0; Y = y0; W = x1 - x0 + 1; H = y1 - y0 + 1;
        LiftX = X; LiftY = Y; LiftW = W; LiftH = H;

        _baseSnapshot = (bool[,])pixels.Clone();

        FloatPixels = new bool[H, W];
        for (int row = 0; row < H; row++)
        for (int col = 0; col < W; col++)
        {
            FloatPixels[row, col]     = pixels[Y + row, X + col];
            pixels[Y + row, X + col] = false;
        }

        _sourceForRotation = (bool[,])FloatPixels.Clone();
        RotationAngle = 0.0;
        FlipH = false;
        FlipV = false;

        Current = Phase.Moving;
        return true;
    }

    /// <summary>
    /// Wstawia zewnętrzne piksele (np. ze schowka) jako nowe floating-selection.
    /// Nie modyfikuje canvasu — float piksele są wklejane na wierzch.
    /// </summary>
    public void LiftExternal(bool[,] sourcePixels, int placeX, int placeY, int canW, int canH)
    {
        int pw = sourcePixels.GetLength(1), ph = sourcePixels.GetLength(0);
        X = Math.Clamp(placeX, 0, Math.Max(0, canW - pw));
        Y = Math.Clamp(placeY, 0, Math.Max(0, canH - ph));
        W = pw; H = ph;
        LiftX = X; LiftY = Y; LiftW = W; LiftH = H;

        FloatPixels        = (bool[,])sourcePixels.Clone();
        _sourceForRotation = (bool[,])FloatPixels.Clone();
        _baseSnapshot      = null;   // brak snapshotu — wklejanie nie cofa zmian na canvasie
        RotationAngle      = 0.0;
        FlipH = false;
        FlipV = false;
        Current            = Phase.Moving;
    }

    // ── Faza 2: Przesuwanie ──────────────────────────────────────────────────

    public void BeginMove(int clickX, int clickY)
    {
        _moveOffX = clickX - X;
        _moveOffY = clickY - Y;
    }

    public void UpdateMove(int curX, int curY, int canW, int canH)
    {
        X = Math.Clamp(curX - _moveOffX, 0, Math.Max(0, canW - W));
        Y = Math.Clamp(curY - _moveOffY, 0, Math.Max(0, canH - H));
    }

    public bool HitTest(int px, int py) =>
        (Current == Phase.Moving || Current == Phase.Scaling || Current == Phase.Rotating) &&
        px >= X && px < X + W && py >= Y && py < Y + H;

    // ── Faza 3: Skalowanie przez uchwyty ────────────────────────────────────

    /// <summary>
    /// Zwraca pozycję uchwytu rotacji w canvas-pikselach (środek dołu + offset ekranowy).
    /// </summary>
    public (int cx, int cy) GetRotateHandleCanvasPos(int zoom)
    {
        int cx = X + W / 2;
        // Stały offset 16 px ekranowych przeliczony na canvas-px
        int offsetCanvas = Math.Max(3, 16 / Math.Max(1, zoom));
        int cy = Y + H + offsetCanvas;
        return (cx, cy);
    }

    /// <summary>Zwraca uchwyt pod kursorem (canvas-px), lub None.</summary>
    public HandleKind GetHandleAt(int px, int py, int hitRadiusPx, int zoom = 1)
    {
        if (Current != Phase.Moving) return HandleKind.None;

        // Uchwyt rotacji (poza bbox)
        var (rhx, rhy) = GetRotateHandleCanvasPos(zoom);
        int rotRadius = Math.Max(2, 10 / Math.Max(1, zoom));
        if (Math.Abs(px - rhx) <= rotRadius && Math.Abs(py - rhy) <= rotRadius)
            return HandleKind.Rotate;

        int cx = X + W / 2, cy = Y + H / 2;
        int x1 = X + W - 1, y1 = Y + H - 1;

        (int hx, int hy, HandleKind k)[] handles =
        {
            (X,  Y,  HandleKind.TL), (cx, Y,  HandleKind.T),  (x1, Y,  HandleKind.TR),
            (x1, cy, HandleKind.R),
            (x1, y1, HandleKind.BR), (cx, y1, HandleKind.B),  (X,  y1, HandleKind.BL),
            (X,  cy, HandleKind.L),
        };
        foreach (var (hx, hy, k) in handles)
            if (Math.Abs(px - hx) <= hitRadiusPx && Math.Abs(py - hy) <= hitRadiusPx)
                return k;
        return HandleKind.None;
    }

    public void BeginScale(HandleKind handle, int px, int py)
    {
        _scaleHandle    = handle;
        _scaleStartPx   = px;
        _scaleStartPy   = py;
        _scaleOrigX     = X;
        _scaleOrigY     = Y;
        _scaleOrigW     = W;
        _scaleOrigH     = H;
        _scaleOrigFloat = FloatPixels != null ? (bool[,])FloatPixels.Clone() : null;
        Current         = Phase.Scaling;
    }

    /// <summary>
    /// Aktualizuje skalowanie.
    /// <paramref name="lockAspect"/> = true przy Shift+narożnik → proporcjonalne skalowanie.
    /// </summary>
    public void UpdateScale(int px, int py, int canW, int canH, bool lockAspect = false)
    {
        if (Current != Phase.Scaling || _scaleOrigFloat == null) return;

        int dx = px - _scaleStartPx;
        int dy = py - _scaleStartPy;

        int nx = _scaleOrigX, ny = _scaleOrigY, nw = _scaleOrigW, nh = _scaleOrigH;

        switch (_scaleHandle)
        {
            case HandleKind.TL: nx = _scaleOrigX + dx; ny = _scaleOrigY + dy;
                                nw = _scaleOrigW - dx; nh = _scaleOrigH - dy; break;
            case HandleKind.T:  ny = _scaleOrigY + dy; nh = _scaleOrigH - dy; break;
            case HandleKind.TR: nw = _scaleOrigW + dx; ny = _scaleOrigY + dy; nh = _scaleOrigH - dy; break;
            case HandleKind.R:  nw = _scaleOrigW + dx; break;
            case HandleKind.BR: nw = _scaleOrigW + dx; nh = _scaleOrigH + dy; break;
            case HandleKind.B:  nh = _scaleOrigH + dy; break;
            case HandleKind.BL: nx = _scaleOrigX + dx; nw = _scaleOrigW - dx; nh = _scaleOrigH + dy; break;
            case HandleKind.L:  nx = _scaleOrigX + dx; nw = _scaleOrigW - dx; break;
        }

        // Shift + narożnik = proporcjonalne skalowanie (aspect-ratio locked)
        if (lockAspect && IsCornerHandle(_scaleHandle) && _scaleOrigW > 0 && _scaleOrigH > 0)
        {
            double aspect = (double)_scaleOrigW / _scaleOrigH;
            bool useWidth = Math.Abs(nw - _scaleOrigW) >= Math.Abs(nh - _scaleOrigH);
            if (useWidth)
            {
                nh = Math.Max(1, (int)Math.Round(nw / aspect));
                if (_scaleHandle == HandleKind.TL || _scaleHandle == HandleKind.TR)
                    ny = _scaleOrigY + _scaleOrigH - nh;
            }
            else
            {
                nw = Math.Max(1, (int)Math.Round(nh * aspect));
                if (_scaleHandle == HandleKind.TL || _scaleHandle == HandleKind.BL)
                    nx = _scaleOrigX + _scaleOrigW - nw;
            }
        }

        nw = Math.Max(1, nw);
        nh = Math.Max(1, nh);
        if (nx < 0)  { nw += nx; nw = Math.Max(1, nw); nx = 0; }
        if (ny < 0)  { nh += ny; nh = Math.Max(1, nh); ny = 0; }
        if (nx + nw > canW) nw = canW - nx;
        if (ny + nh > canH) nh = canH - ny;

        X = nx; Y = ny; W = nw; H = nh;
        FloatPixels = ScaleFloatPixels(
            _scaleOrigFloat,
            _scaleOrigFloat.GetLength(0), _scaleOrigFloat.GetLength(1), nh, nw);
    }

    public void EndScale()
    {
        if (Current != Phase.Scaling) return;
        // Aktualizuj źródło rotacji po zmianie skali
        if (FloatPixels != null)
            _sourceForRotation = (bool[,])FloatPixels.Clone();
        RotationAngle = 0.0;
        Current = Phase.Moving;
    }

    private static bool IsCornerHandle(HandleKind k) =>
        k is HandleKind.TL or HandleKind.TR or HandleKind.BR or HandleKind.BL;

    // ── Faza 4: Obracanie ────────────────────────────────────────────────────

    public void BeginRotate(int clickCanvasPx, int clickCanvasPy)
    {
        Current = Phase.Rotating;
        _rotOrigCenterX = X + W / 2.0;
        _rotOrigCenterY = Y + H / 2.0;
        _rotStartAngle  = Math.Atan2(clickCanvasPy - _rotOrigCenterY,
                                     clickCanvasPx - _rotOrigCenterX);
        _rotBaseAngle   = RotationAngle;
    }

    public void UpdateRotate(int clickCanvasPx, int clickCanvasPy, int canW, int canH)
    {
        if (_sourceForRotation == null) return;

        double curAngle = Math.Atan2(clickCanvasPy - _rotOrigCenterY,
                                     clickCanvasPx - _rotOrigCenterX);
        double deltaDeg = (curAngle - _rotStartAngle) * 180.0 / Math.PI;
        RotationAngle = _rotBaseAngle + deltaDeg;

        var (rotated, rw, rh) = RotatePixels(_sourceForRotation, RotationAngle);
        FloatPixels = rotated;

        // Utrzymaj centrum zaznaczenia w miejscu
        int newX = (int)Math.Round(_rotOrigCenterX - rw / 2.0);
        int newY = (int)Math.Round(_rotOrigCenterY - rh / 2.0);
        X = Math.Clamp(newX, 0, Math.Max(0, canW - rw));
        Y = Math.Clamp(newY, 0, Math.Max(0, canH - rh));
        W = rw; H = rh;
    }

    public void EndRotate()
    {
        if (Current != Phase.Rotating) return;
        if (FloatPixels != null)
            _sourceForRotation = (bool[,])FloatPixels.Clone();
        Current = Phase.Moving;
    }

    // ── Statyczne algorytmy ──────────────────────────────────────────────────

    /// <summary>
    /// Obraca tablicę bool[,] o <paramref name="angleDeg"/> stopni wokół jej centrum.
    /// Zwraca nową tablicę + nowy rozmiar (może być większy od oryginału).
    /// </summary>
    public static (bool[,] pixels, int newW, int newH) RotatePixels(bool[,] src, double angleDeg)
    {
        int srcH = src.GetLength(0), srcW = src.GetLength(1);
        double cx = (srcW - 1) / 2.0, cy = (srcH - 1) / 2.0;

        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        // Nowy bounding box
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double[] cornX = { 0, srcW - 1.0, srcW - 1.0, 0 };
        double[] cornY = { 0, 0,           srcH - 1.0, srcH - 1.0 };
        for (int i = 0; i < 4; i++)
        {
            double dx = cornX[i] - cx, dy = cornY[i] - cy;
            double rx = cos * dx - sin * dy + cx;
            double ry = sin * dx + cos * dy + cy;
            if (rx < minX) minX = rx; if (rx > maxX) maxX = rx;
            if (ry < minY) minY = ry; if (ry > maxY) maxY = ry;
        }

        int dstW = Math.Max(1, (int)Math.Round(maxX - minX) + 1);
        int dstH = Math.Max(1, (int)Math.Round(maxY - minY) + 1);
        double ncx = (dstW - 1) / 2.0, ncy = (dstH - 1) / 2.0;

        double cosInv = Math.Cos(-rad), sinInv = Math.Sin(-rad);
        var dst = new bool[dstH, dstW];

        for (int r = 0; r < dstH; r++)
        for (int c = 0; c < dstW; c++)
        {
            double dx = c - ncx, dy = r - ncy;
            double sx = cosInv * dx - sinInv * dy + cx;
            double sy = sinInv * dx + cosInv * dy + cy;
            int isx = (int)Math.Round(sx), isy = (int)Math.Round(sy);
            if ((uint)isx < (uint)srcW && (uint)isy < (uint)srcH)
                dst[r, c] = src[isy, isx];
        }

        return (dst, dstW, dstH);
    }

    private static bool[,] ScaleFloatPixels(bool[,] src, int srcH, int srcW, int dstH, int dstW)
    {
        var dst = new bool[dstH, dstW];
        for (int r = 0; r < dstH; r++)
        for (int c = 0; c < dstW; c++)
        {
            int sr = r * srcH / dstH;
            int sc = c * srcW / dstW;
            dst[r, c] = src[Math.Clamp(sr, 0, srcH - 1), Math.Clamp(sc, 0, srcW - 1)];
        }
        return dst;
    }

    /// <summary>Podmienia float piksele (np. po flip/rotate z zewnątrz) bez zmiany pozycji bbox.</summary>
    /// <param name="isFlipH">Czy ta podmiana to odbicie poziome (przełącza flagę FlipH).</param>
    /// <param name="isFlipV">Czy ta podmiana to odbicie pionowe (przełącza flagę FlipV).</param>
    public void SetFloatPixels(bool[,] pixels, bool isFlipH = false, bool isFlipV = false)
    {
        FloatPixels        = pixels;
        _sourceForRotation = (bool[,])pixels.Clone();
        RotationAngle      = 0.0;
        if (isFlipH) FlipH = !FlipH;
        if (isFlipV) FlipV = !FlipV;
    }

    // ── Commit / Cancel / Reset ──────────────────────────────────────────────

    /// <summary>Scala uniesione piksele z powrotem na canvas i resetuje stan.</summary>
    public void CommitTo(bool[,] pixels, int canW, int canH)
    {
        if (FloatPixels != null)
        {
            for (int row = 0; row < H; row++)
            for (int col = 0; col < W; col++)
            {
                int px = X + col, py = Y + row;
                if ((uint)px < (uint)canW && (uint)py < (uint)canH)
                    pixels[py, px] = FloatPixels[row, col];
            }
        }
        Reset();
    }

    /// <summary>Anuluje zaznaczenie i zwraca snapshot do przywrócenia (lub null).</summary>
    public bool[,]? CancelAndGetSnapshot()
    {
        var snap = _baseSnapshot;
        Reset();
        return snap;
    }

    public void Reset()
    {
        Current            = Phase.None;
        X = Y = W = H      = 0;
        FloatPixels        = null;
        _baseSnapshot      = null;
        _scaleOrigFloat    = null;
        _sourceForRotation = null;
        RotationAngle      = 0.0;
        FlipH = false;
        FlipV = false;
    }
}
