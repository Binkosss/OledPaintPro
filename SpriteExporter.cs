using System.Text;
using OledPaintPro.Models;

namespace OledPaintPro;

/// <summary>
/// Generuje plik nagłówkowy .h z definicjami sprite'a i jego ustawieniami ruchu.
/// Plik jest gotowy do wrzucenia do folderu include/ projektu Arduino/ESP32.
/// Użytkownik implementuje logikę ruchu w swoim Main.cpp korzystając z tych #define.
/// </summary>
public static class SpriteExporter
{
    /// <summary>
    /// Generuje zawartość pliku .h z bitmapą sprite'a i wszystkimi definicjami ruchu.
    /// </summary>
    public static string Export(bool[,] spritePixels, SpriteMotionSettings s,
        bool[,]? bgPixels = null)
    {
        int sw = s.SpriteWidth, sh = s.SpriteHeight;
        int bpr = (sw + 7) / 8;
        string nm = s.SpriteName.ToUpper();

        var sb = new StringBuilder();

        sb.AppendLine($"// ═══════════════════════════════════════════════════════");
        sb.AppendLine($"// Sprite: {s.SpriteName}");
        sb.AppendLine($"// Wygenerowane przez OLED Paint Pro");
        sb.AppendLine($"// Wrzuć do folderu include/ i dołącz w Main.cpp:");
        sb.AppendLine($"//   #include \"{s.SpriteName}.h\"");
        sb.AppendLine($"// ═══════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine($"#pragma once");
        sb.AppendLine($"#include <pgmspace.h>");
        sb.AppendLine();

        // ── Definicje sprite'a ─────────────────────────────────────────────
        sb.AppendLine($"// ── Rozmiar sprite'a ──────────────────────────────────");
        sb.AppendLine($"#define {nm}_WIDTH       {sw}");
        sb.AppendLine($"#define {nm}_HEIGHT      {sh}");
        sb.AppendLine($"#define {nm}_BPR         {bpr}   // bajtów na wiersz");
        sb.AppendLine();

        // ── Definicje ruchu ────────────────────────────────────────────────
        sb.AppendLine($"// ── Rozmiar ekranu ────────────────────────────────────");
        sb.AppendLine($"#define {nm}_SCREEN_W    {s.ScreenWidth}");
        sb.AppendLine($"#define {nm}_SCREEN_H    {s.ScreenHeight}");
        sb.AppendLine();
        sb.AppendLine($"// ── Ruch: pozycja startowa i końcowa ──────────────────");
        sb.AppendLine($"#define {nm}_START_X     {s.StartX}");
        sb.AppendLine($"#define {nm}_START_Y     {s.StartY}");
        sb.AppendLine($"#define {nm}_END_X       {s.EndX}");
        sb.AppendLine($"#define {nm}_END_Y       {s.EndY}");
        sb.AppendLine();
        sb.AppendLine($"// ── Prędkość ──────────────────────────────────────────");
        sb.AppendLine($"#define {nm}_DELAY_MS    {s.DelayMs}   // ms między klatkami");
        sb.AppendLine();

        // ── Tryb pętli ─────────────────────────────────────────────────────
        sb.AppendLine($"// ── Tryb pętli: 0=Once  1=Loop  2=Bounce ─────────────");
        sb.AppendLine($"#define {nm}_LOOP_MODE   {(int)s.LoopMode}   // {s.LoopMode}");
        sb.AppendLine();

        // ── Obliczone wartości pomocnicze ──────────────────────────────────
        int dx = s.EndX - s.StartX;
        int dy = s.EndY - s.StartY;
        int steps = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        int totalMs = steps * s.DelayMs;

        sb.AppendLine($"// ── Wartości obliczone (pomocnicze) ───────────────────");
        sb.AppendLine($"#define {nm}_STEPS       {steps}   // liczba kroków ruchu");
        sb.AppendLine($"#define {nm}_TOTAL_MS    {totalMs}   // całkowity czas animacji [ms]");
        sb.AppendLine();

        // ── Bitmapa sprite'a ───────────────────────────────────────────────
        sb.AppendLine($"// ── Bitmapa sprite'a (MSB-first, PROGMEM) ─────────────");
        sb.AppendLine($"// Użycie: display.drawBitmap(x, y, {s.SpriteName}, {nm}_WIDTH, {nm}_HEIGHT, WHITE);");
        sb.AppendLine($"const unsigned char PROGMEM {s.SpriteName}[] = {{");
        AppendBitmap(sb, spritePixels, sw, sh);
        sb.AppendLine("};");
        sb.AppendLine();

        // ── Tło (opcjonalne) ───────────────────────────────────────────────
        if (s.HasBackground && bgPixels != null)
        {
            int bw = bgPixels.GetLength(1), bh = bgPixels.GetLength(0);
            int bgBpr = (bw + 7) / 8;
            sb.AppendLine($"// ── Bitmapa tła (MSB-first, PROGMEM) ─────────────────");
            sb.AppendLine($"// Użycie: display.drawBitmap(0, 0, {s.BackgroundName}, {bw}, {bh}, WHITE);");
            sb.AppendLine($"#define {nm}_BG_WIDTH   {bw}");
            sb.AppendLine($"#define {nm}_BG_HEIGHT  {bh}");
            sb.AppendLine($"#define {nm}_BG_BPR     {bgBpr}");
            sb.AppendLine($"const unsigned char PROGMEM {s.BackgroundName}[] = {{");
            AppendBitmap(sb, bgPixels, bw, bh);
            sb.AppendLine("};");
            sb.AppendLine();
        }

        // ── Przykład użycia w Main.cpp ─────────────────────────────────────
        sb.AppendLine($"// ════════════════════════════════════════════════════════");
        sb.AppendLine($"// Przykład użycia w Main.cpp:");
        sb.AppendLine($"// ════════════════════════════════════════════════════════");
        sb.AppendLine($"//");
        sb.AppendLine($"// #include \"{s.SpriteName}.h\"");
        sb.AppendLine($"//");
        sb.AppendLine($"// int16_t sprX = {nm}_START_X;");
        sb.AppendLine($"// int16_t sprY = {nm}_START_Y;");
        sb.AppendLine($"// int8_t  dirX = (({nm}_END_X - {nm}_START_X) > 0) ? 1 : -1;");
        sb.AppendLine($"// int8_t  dirY = (({nm}_END_Y - {nm}_START_Y) > 0) ? 1 : -1;");
        sb.AppendLine($"//");
        sb.AppendLine($"// void updateSprite() {{");
        if (s.HasBackground)
            sb.AppendLine($"//   display.drawBitmap(0, 0, {s.BackgroundName}, {nm}_BG_WIDTH, {nm}_BG_HEIGHT, WHITE);");
        else
            sb.AppendLine($"//   display.clearDisplay();");
        sb.AppendLine($"//   display.drawBitmap(sprX, sprY, {s.SpriteName}, {nm}_WIDTH, {nm}_HEIGHT, WHITE);");
        sb.AppendLine($"//   display.display();");
        sb.AppendLine($"//   delay({nm}_DELAY_MS);");
        sb.AppendLine($"//");

        switch (s.LoopMode)
        {
            case SpriteLoopMode.Once:
                sb.AppendLine($"//   // Once — ruch jednokierunkowy");
                sb.AppendLine($"//   if (sprX != {nm}_END_X) sprX += dirX;");
                sb.AppendLine($"//   if (sprY != {nm}_END_Y) sprY += dirY;");
                break;
            case SpriteLoopMode.Loop:
                sb.AppendLine($"//   // Loop — po dotarciu do końca wróć na start");
                sb.AppendLine($"//   sprX += dirX;");
                sb.AppendLine($"//   sprY += dirY;");
                sb.AppendLine($"//   if (sprX == {nm}_END_X && sprY == {nm}_END_Y) {{");
                sb.AppendLine($"//     sprX = {nm}_START_X;");
                sb.AppendLine($"//     sprY = {nm}_START_Y;");
                sb.AppendLine($"//   }}");
                break;
            case SpriteLoopMode.Bounce:
                sb.AppendLine($"//   // Bounce — odbijanie między Start a End");
                sb.AppendLine($"//   sprX += dirX;");
                sb.AppendLine($"//   sprY += dirY;");
                sb.AppendLine($"//   if (sprX <= {nm}_START_X || sprX >= {nm}_END_X) dirX = -dirX;");
                sb.AppendLine($"//   if (sprY <= {nm}_START_Y || sprY >= {nm}_END_Y) dirY = -dirY;");
                break;
        }

        sb.AppendLine($"// }}");

        return sb.ToString();
    }

    /// <summary>
    /// Generuje plik .h z wieloma klatkami sprite'a (animowany sprite).
    /// Każda klatka to osobna tablica PROGMEM.
    /// </summary>
    public static string ExportMultiFrame(List<PixelTemplate> frames, int sw, int sh,
        SpriteMotionSettings s)
    {
        int bpr = (sw + 7) / 8;
        string nm = s.SpriteName.ToUpper();
        var sb = new StringBuilder();

        sb.AppendLine($"// ═══════════════════════════════════════════════════════");
        sb.AppendLine($"// Sprite (wieloklatkowy): {s.SpriteName}");
        sb.AppendLine($"// Wygenerowane przez OLED Paint Pro");
        sb.AppendLine($"// #include \"{s.SpriteName}.h\"");
        sb.AppendLine($"// ═══════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine($"#pragma once");
        sb.AppendLine($"#include <pgmspace.h>");
        sb.AppendLine();
        sb.AppendLine($"#define {nm}_WIDTH        {sw}");
        sb.AppendLine($"#define {nm}_HEIGHT       {sh}");
        sb.AppendLine($"#define {nm}_BPR          {bpr}");
        sb.AppendLine($"#define {nm}_FRAME_COUNT  {frames.Count}");
        sb.AppendLine($"#define {nm}_SCREEN_W     {s.ScreenWidth}");
        sb.AppendLine($"#define {nm}_SCREEN_H     {s.ScreenHeight}");
        sb.AppendLine($"#define {nm}_START_X      {s.StartX}");
        sb.AppendLine($"#define {nm}_START_Y      {s.StartY}");
        sb.AppendLine($"#define {nm}_END_X        {s.EndX}");
        sb.AppendLine($"#define {nm}_END_Y        {s.EndY}");
        sb.AppendLine($"#define {nm}_DELAY_MS     {s.DelayMs}");
        sb.AppendLine($"#define {nm}_LOOP_MODE    {(int)s.LoopMode}   // {s.LoopMode}");
        sb.AppendLine();

        for (int fi = 0; fi < frames.Count; fi++)
        {
            sb.AppendLine($"// Klatka {fi + 1}/{frames.Count}");
            sb.AppendLine($"const unsigned char PROGMEM {s.SpriteName}_f{fi + 1:D3}[] = {{");
            AppendBitmap(sb, frames[fi].Pixels, sw, sh);
            sb.AppendLine("};");
            sb.AppendLine();
        }

        sb.AppendLine($"const unsigned char* const {s.SpriteName}_frames[{nm}_FRAME_COUNT] PROGMEM = {{");
        for (int fi = 0; fi < frames.Count; fi++)
        {
            bool last = fi == frames.Count - 1;
            sb.AppendLine($"  {s.SpriteName}_f{fi + 1:D3}{(last ? "" : ",")}");
        }
        sb.AppendLine("};");

        return sb.ToString();
    }

    static void AppendBitmap(StringBuilder sb, bool[,] pixels, int w, int h)
    {
        int bpr = (w + 7) / 8;
        for (int row = 0; row < h; row++)
        {
            sb.Append("  ");
            for (int b = 0; b < bpr; b++)
            {
                byte val = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int col = b * 8 + bit;
                    if (col < w && pixels[row, col])
                        val |= (byte)(0x80 >> bit);
                }
                bool last = row == h - 1 && b == bpr - 1;
                sb.Append($"0x{val:X2}{(last ? "" : ", ")}");
            }
            sb.AppendLine();
        }
    }
}
