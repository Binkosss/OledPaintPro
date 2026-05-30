namespace OledPaintPro.Models;

/// <summary>
/// Ustawienia ruchu sprite'a — eksportowane jako #define do pliku include.
/// Używane przez Main.cpp użytkownika, który sam implementuje logikę ruchu.
/// </summary>
public class SpriteMotionSettings
{
    // ── Nazwa ─────────────────────────────────────────────────────────────
    public string SpriteName { get; set; } = "mySprite";

    // ── Rozmiar sprite'a ──────────────────────────────────────────────────
    public int SpriteWidth  { get; set; } = 32;
    public int SpriteHeight { get; set; } = 32;

    // ── Rozmiar ekranu docelowego ─────────────────────────────────────────
    public int ScreenWidth  { get; set; } = 128;
    public int ScreenHeight { get; set; } = 64;

    // ── Pozycja startowa ─────────────────────────────────────────────────
    public int StartX { get; set; } = 0;
    public int StartY { get; set; } = 16;

    // ── Pozycja końcowa ───────────────────────────────────────────────────
    public int EndX { get; set; } = 96;
    public int EndY { get; set; } = 16;

    // ── Prędkość ──────────────────────────────────────────────────────────
    /// <summary>Opóźnienie między klatkami w ms (im mniej — tym szybciej).</summary>
    public int DelayMs { get; set; } = 16;

    // ── Tryb pętli ────────────────────────────────────────────────────────
    public SpriteLoopMode LoopMode { get; set; } = SpriteLoopMode.Loop;

    // ── Tło (opcjonalne) ─────────────────────────────────────────────────
    /// <summary>Czy eksportować bitmapę tła razem ze spritem.</summary>
    public bool HasBackground { get; set; } = false;
    public string BackgroundName { get; set; } = "myBackground";
}

public enum SpriteLoopMode
{
    Once,    // raz od Start do End
    Loop,    // zapętlenie od Start do End
    Bounce,  // odbijanie Start ↔ End
}
