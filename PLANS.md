# OLED Paint Pro — Plany na przyszłość

## 🕹️ Tryb Sprite (eksport z kodem ruchu)

**Pomysł:** Zamiast eksportować animację klatka-po-klatce, eksportować jedną bitmapę sprite'a
z kodem C++ który nią porusza na ekranie OLED (Adafruit GFX / u8g2).

### Jak to działa na Arduino/ESP32:
```cpp
for (int x = startX; x <= endX; x++) {
    display.clearDisplay();
    display.drawBitmap(x, y, sprite, W, H, WHITE);
    display.display();
    delay(16); // ~60 FPS
}
```

### Planowane funkcje:
- Wybór bitmapa sprite'a (z istniejących kart rysowania)
- Opcjonalne tło (statyczna bitmapa)
- Start X/Y i End X/Y — skąd dokąd
- Prędkość (delay ms)
- Tryb pętli: Raz / Loop / Bounce (odbijanie)
- Kilka sprite'ów naraz (każdy z własnym ruchem)
- Easing (lerp — przyspiesza/zwalnia)

### Pliki do stworzenia:
| Plik | Co |
|---|---|
| `SpriteEditorControl.xaml` + `.cs` | Nowa kontrolka z podglądem ruchu |
| `SpriteMotionSettings.cs` | Model danych (start/end/speed/mode) |
| `SpriteExporter.cs` | Generator kodu C++ |
| `MainWindow.xaml.cs` | Przycisk `🕹️ Nowy Sprite` w topbarze |

### Uwagi:
- Niezależny od EyeEditorControl — osobna zakładka
- Eksport obok istniejących: 📄 Bitmapa, 🎬 Animacja, 🕹️ Sprite

---

## 🎬 Tween / Auto-generowanie klatek animacji

**Pomysł:** Definiujesz klatkę startową i końcową, program automatycznie generuje
klatki pośrednie (interpolacja pozycji, rozmiaru).

### Jak to działa:
```
Klatka 1:  👁 pozycja X=10  ← rysujesz ręcznie
Klatka 5:  👁 pozycja X=50  ← rysujesz ręcznie
Program generuje klatki 2,3,4 automatycznie (lerp)
```

### Uwagi:
- Wymaga zaznaczenia obiektu (sprite'a) w klatce
- Opcje easing: Linear, EaseIn, EaseOut, EaseBoth
- Na razie odłożone — najpierw tryb Sprite

---

## Inne pomysły (luźne)

- Warstwy (tło + sprite) w edytorze animacji
- Import GIF → automatyczne klatki
- Podgląd na żywo na fizycznym OLED przez USB/Serial

