namespace OledPaintPro.Drawing;

// ── Dostępne narzędzia rysowania ─────────────────────────────────────────────
public enum DrawTool
{
    // Podstawowe
    Pencil, Eraser, FloodFill,
    // Kształty
    Line, Rect, FilledRect,
    Ellipse, FilledEllipse,
    // Kształty dodatkowe
    ArcDown, ArcUp, Tongue, Triangle, FilledTriangle, Cross,
    // Oczy
    Eye, EyeStamp,
    // Usta
    MouthStamp,
    // Inne (biblioteka)
    OtherStamp,
    // Bitmapy predefiniowane (Lopaka-style)
    BitmapStamp,
    // Specjalne
    Star, Scatter,
    // Zaznaczanie
    Select,
    // Tekst
    Text,
    // Linie kierunkowe
    HLine, VLine,
    // Odbicia (jednorazowe — obsługiwane przez PixelCanvasControl)
    FlipH,
    FlipV,
}

/// <summary>Kształt końcówki pędzla/gumki.</summary>
public enum BrushShape { Square, Circle }
