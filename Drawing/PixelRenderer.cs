using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OledPaintPro.Drawing;

/// <summary>
/// Renderuje tablicę bool[,] do WriteableBitmap z siatką OLED.
/// Jeden egzemplarz na canvas — przechowuje bitmapę i ją reużywa.
/// </summary>
public sealed class PixelRenderer
{
    // ── Kolory OLED ─────────────────────────────────────────────────────────
    private const uint ColorOn      = 0xFF_F2_F2_FF;   // biały piksel — lekko niebieskawy
    private const uint ColorOff     = 0xFF_02_02_10;   // czarny piksel — ciemny granat
    private const uint ColorGrid1px = 0xFF_18_18_18;   // siatka 1-pikselowa
    private const uint ColorGrid8px = 0xFF_2A_2A_2A;   // siatka 8-pikselowa (granica bajtu)
    private const int  GridMajor    = 8;               // co ile pikseli gruba siatka

    private WriteableBitmap? _bitmap;

    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Renderuje piksele do bitmapy i zwraca ją (reużywa jeśli rozmiar się nie zmienił).
    /// </summary>
    public WriteableBitmap Render(bool[,] pixels, int w, int h, int zoom,
        bool showGrid1px, bool showGrid8px,
        bool[,]? floatPixels = null, int floatX = 0, int floatY = 0, int floatW = 0, int floatH = 0,
        bool showSelBorder = false, int selBX = 0, int selBY = 0, int selBW = 0, int selBH = 0,
        bool showSymV = false, bool showSymH = false,
        bool showRotHandle = false, int rotHandleCanvasX = 0, int rotHandleCanvasY = 0,
        double dpiX = 96.0, double dpiY = 96.0)
    {
        int bw = w * zoom;
        int bh = h * zoom;

        if (_bitmap == null || _bitmap.PixelWidth != bw || _bitmap.PixelHeight != bh
            || Math.Abs(_bitmap.DpiX - dpiX) > 0.5)
            _bitmap = new WriteableBitmap(bw, bh, dpiX, dpiY, PixelFormats.Bgra32, null);

        _bitmap.Lock();
        unsafe
        {
            byte* back   = (byte*)_bitmap.BackBuffer;
            int   stride = _bitmap.BackBufferStride;

            // ── Piksele OLED (bazowe) ─────────────────────────────────────
            for (int row = 0; row < h; row++)
            for (int col = 0; col < w; col++)
            {
                uint color = pixels[row, col] ? ColorOn : ColorOff;
                for (int dy = 0; dy < zoom; dy++)
                for (int dx = 0; dx < zoom; dx++)
                    *((uint*)(back + (row * zoom + dy) * stride + (col * zoom + dx) * 4)) = color;
            }

            // ── Uniesione piksele zaznaczenia (floating, rysowane przed siatką) ──
            if (floatPixels != null)
            {
                for (int row = 0; row < floatH; row++)
                for (int col = 0; col < floatW; col++)
                {
                    int bx = floatX + col, by = floatY + row;
                    if ((uint)bx >= (uint)w || (uint)by >= (uint)h) continue;
                    uint c = floatPixels[row, col] ? ColorOn : ColorOff;
                    for (int dy = 0; dy < zoom; dy++)
                    for (int dx = 0; dx < zoom; dx++)
                        *((uint*)(back + (by * zoom + dy) * stride + (bx * zoom + dx) * 4)) = c;
                }
            }

            // ── Siatka 1px (piksele) ─────────────────────────────────────
            if (showGrid1px && zoom >= 4)
            {
                for (int row = 1; row < h; row++)
                {
                    int sy = row * zoom;
                    for (int x = 0; x < bw; x++)
                        *((uint*)(back + sy * stride + x * 4)) = ColorGrid1px;
                }
                for (int col = 1; col < w; col++)
                {
                    int sx = col * zoom;
                    for (int y = 0; y < bh; y++)
                        *((uint*)(back + y * stride + sx * 4)) = ColorGrid1px;
                }
            }

            // ── Siatka 8px (granica bajtu) ───────────────────────────────
            if (showGrid8px)
            {
                for (int row = 0; row <= h; row += GridMajor)
                {
                    int sy = row * zoom;
                    if (sy >= bh) continue;
                    for (int x = 0; x < bw; x++)
                        *((uint*)(back + sy * stride + x * 4)) = ColorGrid8px;
                }
                for (int col = 0; col <= w; col += GridMajor)
                {
                    int sx = col * zoom;
                    if (sx >= bw) continue;
                    for (int y = 0; y < bh; y++)
                        *((uint*)(back + y * stride + sx * 4)) = ColorGrid8px;
                }
            }

            // ── Ramka zaznaczenia (przerywana, rysowana na wierzchu) ──────
            if (showSelBorder && selBW > 0 && selBH > 0)
            {
                const uint ColorSelA = 0xFF_28_E8_B0;   // jasny teal
                const uint ColorSelB = 0xFF_00_00_00;   // czarny (przerwa)
                int x0s = selBX * zoom,                 y0s = selBY * zoom;
                int x1s = (selBX + selBW) * zoom - 1,  y1s = (selBY + selBH) * zoom - 1;
                x1s = Math.Min(x1s, bw - 1);
                y1s = Math.Min(y1s, bh - 1);
                // poziome krawędzie (góra + dół)
                for (int sx = x0s; sx <= x1s; sx++)
                {
                    uint c = (sx / 4) % 2 == 0 ? ColorSelA : ColorSelB;
                    if ((uint)sx < (uint)bw)
                    {
                        if ((uint)y0s < (uint)bh) *((uint*)(back + y0s * stride + sx * 4)) = c;
                        if ((uint)y1s < (uint)bh) *((uint*)(back + y1s * stride + sx * 4)) = c;
                    }
                }
                // pionowe krawędzie (lewo + prawo)
                for (int sy = y0s; sy <= y1s; sy++)
                {
                    uint c = (sy / 4) % 2 == 0 ? ColorSelA : ColorSelB;
                    if ((uint)sy < (uint)bh)
                    {
                        if ((uint)x0s < (uint)bw) *((uint*)(back + sy * stride + x0s * 4)) = c;
                        if ((uint)x1s < (uint)bw) *((uint*)(back + sy * stride + x1s * 4)) = c;
                    }
                }

                // ── Uchwyty skalowania (8 kwadratów 5x5 px ekranowych) ──────
                const uint HandleFill    = 0xFF_FF_FF_FF;  // biały środek
                const uint HandleBorder  = 0xFF_00_00_00;  // czarny obramowanie
                const int  HR = 2;                          // promień uchwytu (HR*2+1 = 5 px)
                int cxs = (x0s + x1s) / 2;
                int cys = (y0s + y1s) / 2;
                // [pikselX_ekranu, pikselY_ekranu]
                Span<(int hx, int hy)> hpts = stackalloc (int, int)[]
                {
                    (x0s, y0s), (cxs, y0s), (x1s, y0s),
                    (x1s, cys),
                    (x1s, y1s), (cxs, y1s), (x0s, y1s),
                    (x0s, cys),
                };
                foreach (var (hx, hy) in hpts)
                {
                    for (int dy = -HR; dy <= HR; dy++)
                    for (int dx = -HR; dx <= HR; dx++)
                    {
                        int sx2 = hx + dx, sy2 = hy + dy;
                        if ((uint)sx2 >= (uint)bw || (uint)sy2 >= (uint)bh) continue;
                        bool isBorder = dx == -HR || dx == HR || dy == -HR || dy == HR;
                        uint col = isBorder ? HandleBorder : HandleFill;
                        *((uint*)(back + sy2 * stride + sx2 * 4)) = col;
                    }
                }

                // ── Uchwyt rotacji (kółko + linia od bottom-center) ──────────
                if (showRotHandle)
                {
                    const uint RotColor  = 0xFF_FF_CC_00;   // żółty
                    const uint RotBorder = 0xFF_00_00_00;
                    const int  RR = 4;                       // promień kółka px ekranowych

                    // Linia od bottom-center zaznaczenia do uchwytu rotacji
                    int linX = (x0s + x1s) / 2;
                    int linY0 = y1s;
                    int linY1 = rotHandleCanvasY * zoom + zoom / 2;
                    int lineSteps = Math.Abs(linY1 - linY0) + 1;
                    for (int step = 0; step <= lineSteps; step++)
                    {
                        int sy2 = linY0 + (lineSteps > 0 ? (linY1 - linY0) * step / lineSteps : 0);
                        if ((uint)linX < (uint)bw && sy2 >= 0 && sy2 < bh + 30)
                        {
                            int sy2c = Math.Clamp(sy2, 0, bh - 1);
                            bool dash = (step / 3) % 2 == 0;
                            uint lc = dash ? RotColor : 0xFF_00_00_00;
                            *((uint*)(back + sy2c * stride + linX * 4)) = lc;
                        }
                    }

                    // Kółko uchwytu rotacji
                    int rhScreenX = rotHandleCanvasX * zoom + zoom / 2;
                    int rhScreenY = rotHandleCanvasY * zoom + zoom / 2;
                    for (int dy = -RR; dy <= RR; dy++)
                    for (int dx = -RR; dx <= RR; dx++)
                    {
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        if (dist > RR) continue;
                        int sx2 = rhScreenX + dx, sy2 = rhScreenY + dy;
                        if (sx2 < 0 || sx2 >= bw) continue;
                        // rysuj nawet poza dolną krawędzią bitmapy — clamping
                        int sy2c = Math.Clamp(sy2, 0, bh - 1);
                        bool isBorder = dist > RR - 1.5;
                        uint col = isBorder ? RotBorder : RotColor;
                        *((uint*)(back + sy2c * stride + sx2 * 4)) = col;
                    }
                }
            }

            // ── Oś symetrii ──────────────────────────────────────────────────
            const uint ColorSymV = 0xFF_FF_40_40;   // czerwona pionowa
            const uint ColorSymH = 0xFF_FF_40_40;   // czerwona pozioma
            if (showSymV)
            {
                int sx = bw / 2;
                for (int sy = 0; sy < bh; sy++)
                {
                    bool dash = (sy / 4) % 2 == 0;
                    uint c = dash ? ColorSymV : 0xFF_00_00_00;
                    if ((uint)sx < (uint)bw)
                        *((uint*)(back + sy * stride + sx * 4)) = c;
                }
            }
            if (showSymH)
            {
                int sy = bh / 2;
                for (int sx = 0; sx < bw; sx++)
                {
                    bool dash = (sx / 4) % 2 == 0;
                    uint c = dash ? ColorSymH : 0xFF_00_00_00;
                    if ((uint)sy < (uint)bh)
                        *((uint*)(back + sy * stride + sx * 4)) = c;
                }
            }
        }
        _bitmap.AddDirtyRect(new Int32Rect(0, 0, bw, bh));
        _bitmap.Unlock();
        return _bitmap;
    }

    /// <summary>Wymusza przebudowanie bitmapy przy następnym Render (zmiana zoom).</summary>
    public void Invalidate() => _bitmap = null;
}
