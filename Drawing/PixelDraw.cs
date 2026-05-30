using OledPaintPro.Models;

namespace OledPaintPro.Drawing;

/// <summary>
/// Statyczny silnik rysowania pikseli — jedyne źródło prawdy dla wszystkich widoków.
/// Wszystkie metody są bezpieczne wątkowo (nie używają stanu globalnego).
/// </summary>
public static class PixelDraw
{
    // ════════════════════════════════════════════════════════════════════════
    //  PRYMITYWY
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Stawia pojedynczy piksel z kontrolą granic.</summary>
    public static void SetPixel(bool[,] canvas, int x, int y, bool value, int w, int h)
    {
        if ((uint)x < (uint)w && (uint)y < (uint)h)
            canvas[y, x] = value;
    }

    /// <summary>Linia Bresenhama.</summary>
    public static void DrawLine(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        for (;;)
        {
            SetPixel(canvas, x0, y0, value, w, h);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { if (x0 == x1) break; err += dy; x0 += sx; }
            if (e2 <= dx) { if (y0 == y1) break; err += dx; y0 += sy; }
        }
    }

    /// <summary>Flood fill oparty o stos (bez rekurencji).</summary>
    public static void FloodFill(bool[,] canvas, int x, int y, bool value, int w, int h)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        bool target = canvas[y, x];
        if (target == value) return;

        var stack = new Stack<(int, int)>(512);
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            if ((uint)cx >= (uint)w || (uint)cy >= (uint)h) continue;
            if (canvas[cy, cx] != target) continue;
            canvas[cy, cx] = value;
            stack.Push((cx + 1, cy)); stack.Push((cx - 1, cy));
            stack.Push((cx, cy + 1)); stack.Push((cx, cy - 1));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  KSZTAŁTY — wszystkie przyjmują bounding-box (x0,y0)→(x1,y1)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Prostokąt — kontur lub wypełniony.</summary>
    public static void DrawRect(bool[,] canvas, int x0, int y0, int x1, int y1, bool fill, bool value, int w, int h)
    {
        int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
        if (fill)
        {
            for (int row = minY; row <= maxY; row++)
            for (int col = minX; col <= maxX; col++)
                SetPixel(canvas, col, row, value, w, h);
        }
        else
        {
            for (int col = minX; col <= maxX; col++)
            {
                SetPixel(canvas, col, minY, value, w, h);
                SetPixel(canvas, col, maxY, value, w, h);
            }
            for (int row = minY; row <= maxY; row++)
            {
                SetPixel(canvas, minX, row, value, w, h);
                SetPixel(canvas, maxX, row, value, w, h);
            }
        }
    }

    /// <summary>Elipsa (kontur) — iteracja X+Y gwarantuje brak luk.</summary>
    public static void DrawEllipseOutline(bool[,] canvas, int cx, int cy, int rx, int ry, bool value, int w, int h)
    {
        if (rx <= 0 && ry <= 0) { SetPixel(canvas, cx, cy, value, w, h); return; }
        if (rx <= 0) { DrawLine(canvas, cx, cy - ry, cx, cy + ry, value, w, h); return; }
        if (ry <= 0) { DrawLine(canvas, cx - rx, cy, cx + rx, cy, value, w, h); return; }

        double rx2 = (double)rx * rx, ry2 = (double)ry * ry;
        for (int dx = -rx; dx <= rx; dx++)
        {
            double dy = ry * Math.Sqrt(1.0 - dx * dx / rx2);
            SetPixel(canvas, cx + dx, cy + (int)Math.Round(dy),  value, w, h);
            SetPixel(canvas, cx + dx, cy - (int)Math.Round(dy),  value, w, h);
        }
        for (int dy = -ry; dy <= ry; dy++)
        {
            double dx = rx * Math.Sqrt(1.0 - dy * dy / ry2);
            SetPixel(canvas, cx + (int)Math.Round(dx), cy + dy,  value, w, h);
            SetPixel(canvas, cx - (int)Math.Round(dx), cy + dy,  value, w, h);
        }
    }

    /// <summary>Elipsa (wypełniona).</summary>
    public static void DrawEllipseFill(bool[,] canvas, int cx, int cy, int rx, int ry, bool value, int w, int h)
    {
        if (rx <= 0 && ry <= 0) { SetPixel(canvas, cx, cy, value, w, h); return; }
        if (ry <= 0) { DrawLine(canvas, cx - rx, cy, cx + rx, cy, value, w, h); return; }

        double ry2 = (double)ry * ry;
        for (int dy = -ry; dy <= ry; dy++)
        {
            double dxF = rx <= 0 ? 0 : rx * Math.Sqrt(1.0 - dy * dy / ry2);
            int span = (int)Math.Round(dxF);
            for (int col = cx - span; col <= cx + span; col++)
                SetPixel(canvas, col, cy + dy, value, w, h);
        }
    }

    /// <summary>Elipsa z bounding-boxa — kontur lub wypełniona.</summary>
    public static void DrawEllipse(bool[,] canvas, int x0, int y0, int x1, int y1, bool fill, bool value, int w, int h)
    {
        int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        int rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        if (fill) DrawEllipseFill(canvas, cx, cy, rx, ry, value, w, h);
        else      DrawEllipseOutline(canvas, cx, cy, rx, ry, value, w, h);
    }


    /// <summary>Usta proste — pozioma linia przez środek bbox.</summary>
    public static void DrawMouthStraight(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int cy = (y0 + y1) / 2;
        DrawLine(canvas, x0, cy, x1, cy, value, w, h);
    }

    /// <summary>Uśmiech — dolna połowa elipsy (centrum na górze bbox).</summary>
    public static void DrawSmile(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int cx  = (x0 + x1) / 2;
        int cy  = Math.Min(y0, y1);
        int rx  = Math.Abs(x1 - x0) / 2;
        int ry  = Math.Abs(y1 - y0);
        if (rx <= 0) { DrawLine(canvas, cx, cy, cx, cy + ry, value, w, h); return; }

        double rx2 = (double)rx * rx;
        double ry2 = ry <= 0 ? 1 : (double)ry * ry;
        for (int dx = -rx; dx <= rx; dx++)
        {
            double dy = ry * Math.Sqrt(1.0 - dx * dx / rx2);
            SetPixel(canvas, cx + dx, cy + (int)Math.Round(dy), value, w, h);
        }
        for (int dy = 0; dy <= ry; dy++)
        {
            double dx = rx * Math.Sqrt(1.0 - dy * dy / ry2);
            SetPixel(canvas, cx + (int)Math.Round(dx), cy + dy, value, w, h);
            SetPixel(canvas, cx - (int)Math.Round(dx), cy + dy, value, w, h);
        }
    }

    /// <summary>Smutek — górna połowa elipsy (centrum na dole bbox).</summary>
    public static void DrawFrown(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int cx  = (x0 + x1) / 2;
        int cy  = Math.Max(y0, y1);
        int rx  = Math.Abs(x1 - x0) / 2;
        int ry  = Math.Abs(y1 - y0);
        if (rx <= 0) { DrawLine(canvas, cx, cy - ry, cx, cy, value, w, h); return; }

        double rx2 = (double)rx * rx;
        double ry2 = ry <= 0 ? 1 : (double)ry * ry;
        for (int dx = -rx; dx <= rx; dx++)
        {
            double dy = ry * Math.Sqrt(1.0 - dx * dx / rx2);
            SetPixel(canvas, cx + dx, cy - (int)Math.Round(dy), value, w, h);
        }
        for (int dy = 0; dy <= ry; dy++)
        {
            double dx = rx * Math.Sqrt(1.0 - dy * dy / ry2);
            SetPixel(canvas, cx + (int)Math.Round(dx), cy - dy, value, w, h);
            SetPixel(canvas, cx - (int)Math.Round(dx), cy - dy, value, w, h);
        }
    }

    /// <summary>Język / otwarte usta — wypełnione dolne półkole.</summary>
    public static void DrawTongue(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int cx  = (x0 + x1) / 2;
        int cy  = Math.Min(y0, y1);
        int rx  = Math.Abs(x1 - x0) / 2;
        int ry  = Math.Abs(y1 - y0);
        if (rx <= 0) { DrawLine(canvas, cx, cy, cx, cy + ry, value, w, h); return; }

        double rx2 = (double)rx * rx;
        for (int dx = -rx; dx <= rx; dx++)
        {
            double dy    = ry * Math.Sqrt(1.0 - dx * dx / rx2);
            int    dyInt = (int)Math.Round(dy);
            for (int py = 0; py <= dyInt; py++)
                SetPixel(canvas, cx + dx, cy + py, value, w, h);
        }
    }

    /// <summary>Serce — dwa kółka u góry + wypełniony trójkąt w dół.</summary>
    public static void DrawHeart(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
        int cx   = (minX + maxX) / 2;
        int bw   = maxX - minX;
        int bh   = maxY - minY;
        if (bw < 4 || bh < 4) { SetPixel(canvas, cx, (minY + maxY) / 2, value, w, h); return; }

        int halfR  = bw / 4;
        int splitY = minY + bh * 2 / 5;

        DrawEllipseFill(canvas, minX + halfR, minY + halfR, halfR, halfR, value, w, h);
        DrawEllipseFill(canvas, maxX - halfR, minY + halfR, halfR, halfR, value, w, h);

        int vHeight = maxY - splitY;
        if (vHeight < 1) vHeight = 1;
        for (int dy = 0; dy <= vHeight; dy++)
        {
            int span = (int)Math.Round((bw / 2.0) * (1.0 - (double)dy / vHeight));
            for (int col = cx - span; col <= cx + span; col++)
                SetPixel(canvas, col, splitY + dy, value, w, h);
        }
    }

    /// <summary>Gwiazdka — asterisk 8-ramienny.</summary>
    public static void DrawStar(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        int r  = Math.Max(1, Math.Min(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) / 2);
        DrawLine(canvas, cx - r, cy,     cx + r, cy,     value, w, h);
        DrawLine(canvas, cx,     cy - r, cx,     cy + r, value, w, h);
        DrawLine(canvas, cx - r, cy - r, cx + r, cy + r, value, w, h);
        DrawLine(canvas, cx + r, cy - r, cx - r, cy + r, value, w, h);
        SetPixel(canvas, cx, cy, value, w, h);
    }

    /// <summary>Trójkąt równoramienny — podstawa na dole.</summary>
    public static void DrawTriangle(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int lx = Math.Min(x0, x1), rx = Math.Max(x0, x1);
        int ty = Math.Min(y0, y1), by = Math.Max(y0, y1);
        int mx = (lx + rx) / 2;
        DrawLine(canvas, mx, ty, lx, by, value, w, h);
        DrawLine(canvas, mx, ty, rx, by, value, w, h);
        DrawLine(canvas, lx, by, rx, by, value, w, h);
    }

    /// <summary>Trójkąt wypełniony — scanline fill między lewym bokiem, prawym bokiem a podstawą.</summary>
    public static void DrawFilledTriangle(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int lx = Math.Min(x0, x1), rx = Math.Max(x0, x1);
        int ty = Math.Min(y0, y1), by = Math.Max(y0, y1);
        int mx = (lx + rx) / 2;
        // Scanline: dla każdej linii y interpoluj x między wierzchołkiem a podstawą
        for (int y = ty; y <= by; y++)
        {
            float t = (by == ty) ? 1f : (float)(y - ty) / (by - ty);
            int xl = (int)Math.Round(mx + (lx - mx) * t);
            int xr = (int)Math.Round(mx + (rx - mx) * t);
            DrawLine(canvas, xl, y, xr, y, value, w, h);
        }
    }

    /// <summary>Krzyż / plus.</summary>
    public static void DrawCross(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int lx = Math.Min(x0, x1), rx = Math.Max(x0, x1);
        int ty = Math.Min(y0, y1), by = Math.Max(y0, y1);
        int mx = (lx + rx) / 2, my = (ty + by) / 2;
        DrawLine(canvas, mx, ty, mx, by, value, w, h);
        DrawLine(canvas, lx, my, rx, my, value, w, h);
    }

    /// <summary>Scatter — losowe piksele (śnieg, łzy). Seed = deterministyczny wygląd.</summary>
    public static void DrawScatter(bool[,] canvas, int x0, int y0, int x1, int y1, bool value, int w, int h, int seed)
    {
        const double Density = 0.25;
        var rng  = new Random(seed);
        int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
        for (int row = minY; row <= maxY; row++)
        for (int col = minX; col <= maxX; col++)
            if (rng.NextDouble() < Density)
                SetPixel(canvas, col, row, value, w, h);
    }

    /// <summary>Wkleja szablon oka skalowany nearest-neighbor do bounding-boxa.</summary>
    public static void StampEye(bool[,] canvas, PixelTemplate template,
        int x0, int y0, int x1, int y1, bool value, int w, int h)
    {
        int dstX = Math.Min(x0, x1), dstY = Math.Min(y0, y1);
        int dstW = Math.Max(1, Math.Abs(x1 - x0));
        int dstH = Math.Max(1, Math.Abs(y1 - y0));
        for (int dy = 0; dy < dstH; dy++)
        {
            int sy = dy * template.Height / dstH;
            for (int dx = 0; dx < dstW; dx++)
            {
                int sx = dx * template.Width / dstW;
                if (template.Pixels[sy, sx])
                    SetPixel(canvas, dstX + dx, dstY + dy, value, w, h);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DISPATCH — zastosuj dowolne narzędzie kształtu jednym wywołaniem
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dispatch wszystkich narzędzi kształtu — używany przez MainWindow i EyeEditor.
    /// </summary>
    public static void ApplyShape(bool[,] canvas, DrawTool tool,
        int x0, int y0, int x1, int y1, bool value, int w, int h,
        int scatterSeed = 0, PixelTemplate? stampTemplate = null)
    {
        switch (tool)
        {
            case DrawTool.Line:           DrawLine(canvas, x0, y0, x1, y1, value, w, h);           break;
            case DrawTool.Rect:           DrawRect(canvas, x0, y0, x1, y1, false, value, w, h);    break;
            case DrawTool.FilledRect:     DrawRect(canvas, x0, y0, x1, y1, true,  value, w, h);    break;
            case DrawTool.Ellipse:        DrawEllipse(canvas, x0, y0, x1, y1, false, value, w, h); break;
            case DrawTool.FilledEllipse:  DrawEllipse(canvas, x0, y0, x1, y1, true,  value, w, h); break;
            case DrawTool.ArcDown:        DrawSmile(canvas, x0, y0, x1, y1, value, w, h);          break;
            case DrawTool.ArcUp:          DrawFrown(canvas, x0, y0, x1, y1, value, w, h);          break;
            case DrawTool.Tongue:         DrawTongue(canvas, x0, y0, x1, y1, value, w, h);         break;
            case DrawTool.Triangle:       DrawTriangle(canvas, x0, y0, x1, y1, value, w, h);       break;
            case DrawTool.FilledTriangle:  DrawFilledTriangle(canvas, x0, y0, x1, y1, value, w, h); break;
            case DrawTool.Cross:          DrawCross(canvas, x0, y0, x1, y1, value, w, h);          break;
            case DrawTool.Star:           DrawStar(canvas, x0, y0, x1, y1, value, w, h);           break;
            case DrawTool.Scatter:        DrawScatter(canvas, x0, y0, x1, y1, value, w, h, scatterSeed); break;
            case DrawTool.EyeStamp when stampTemplate != null:
                StampEye(canvas, stampTemplate, x0, y0, x1, y1, value, w, h);                      break;
            case DrawTool.MouthStamp when stampTemplate != null:
                StampEye(canvas, stampTemplate, x0, y0, x1, y1, value, w, h);                      break;
            case DrawTool.OtherStamp when stampTemplate != null:
                StampEye(canvas, stampTemplate, x0, y0, x1, y1, value, w, h);                      break;
            case DrawTool.BitmapStamp when stampTemplate != null:
                StampEye(canvas, stampTemplate, x0, y0, x1, y1, value, w, h);                      break;
            case DrawTool.HLine: DrawLine(canvas, 0, y0, w - 1, y0, value, w, h);                  break;
            case DrawTool.VLine: DrawLine(canvas, x0, 0, x0, h - 1, value, w, h);                  break;
        }
    }

    /// <summary>Rysuje końcówkę pędzla (kwadrat lub kółko) o zadanym rozmiarze.</summary>
    public static void DrawBrush(bool[,] canvas, int cx, int cy, int size, BrushShape shape, bool value, int w, int h)
    {
        if (size <= 1) { SetPixel(canvas, cx, cy, value, w, h); return; }
        int r = size / 2;
        if (shape == BrushShape.Circle)
            DrawEllipseFill(canvas, cx, cy, r, r, value, w, h);
        else
            DrawRect(canvas, cx - r, cy - r, cx + r, cy + r, true, value, w, h);
    }

    /// <summary>Rysuje grubą linię Bresenhama z końcówką o zadanym rozmiarze.</summary>
    public static void DrawThickLine(bool[,] canvas, int x0, int y0, int x1, int y1, int size, BrushShape shape, bool value, int w, int h)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        for (;;)
        {
            DrawBrush(canvas, x0, y0, size, shape, value, w, h);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { if (x0 == x1) break; err += dy; x0 += sx; }
            if (e2 <= dx) { if (y0 == y1) break; err += dx; y0 += sy; }
        }
    }

    /// <summary>Zwraca true jeśli narzędzie rysuje kształt (wymaga preview na mouse-move).</summary>
    public static bool IsShapeTool(DrawTool tool) => tool switch
    {
        DrawTool.Pencil or DrawTool.Eraser or DrawTool.FloodFill => false,
        DrawTool.FlipH or DrawTool.FlipV => false,
        DrawTool.HLine or DrawTool.VLine => true,
        _ => true,
    };
}
