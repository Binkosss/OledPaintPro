# COPILOT CONTEXT — OledPaintPro

## ⚠️ Pliki których NIE RUSZAĆ

### `EyeEditorControl.xaml` + `EyeEditorControl.xaml.cs`
- Przeznaczony **WYŁĄCZNIE** dla edytora oka (oko = EyeTemplate z EyeLibrary)
- NIE dodawać tu funkcji dla Mouth (usta) ani Other (inne)
- NIE dodawać tu funkcji animacji (AnimationEditorControl robi to osobno)
- NIE dodawać tu funkcji Sprite
- Jest teraz pusty (szkielet) — wypełniać tylko logiką specyficzną dla oka

### `EyePickerPopup.xaml` + `EyePickerPopup.xaml.cs`
- Tylko dla biblioteki EyeLibrary
- Nie łączyć z MouthPickerPopup ani OtherPickerPopup

---

## Struktura plików — co robi co

| Plik | Odpowiedzialność |
|---|---|
| `EyeEditorControl` | Edytor szablonu oka (tylko Eye) |
| `EyePickerPopup` | Picker szablonów oka |
| `MouthPickerPopup` | Picker szablonów ust |
| `OtherPickerPopup` | Picker szablonów "inne" |
| `PickerPopupTemplate` | Wzorzec/szablon dla popupów |
| `AnimationEditorControl` | Edytor animacji klatka-po-klatce |
| `PixelCanvasControl` | Główny canvas rysowania (narzędzia, symetria, zaznaczenie) |
| `MainWindow` | Główne okno, zakładki, topbar |
| `Drawing/PixelDraw.cs` | Algorytmy rysowania (linie, kształty, pędzel) |
| `Drawing/PixelRenderer.cs` | Renderowanie bool[,] → WriteableBitmap |
| `Drawing/SelectionState.cs` | Stan narzędzia Zaznaczanie |
| `Drawing/BitmapParser.cs` | Parser kodu C (.h) → bool[,] |
| `Models/EyeTemplate.cs` | Model szablonu (Eye/Mouth/Other) |
| `Models/EyeLibrary.cs` | Biblioteka szablonów oczu |
| `Models/MouthLibrary.cs` | Biblioteka szablonów ust |
| `Models/OtherLibrary.cs` | Biblioteka szablonów "inne" |

---

## Zasady ogólne

- Każda kategoria (Eye / Mouth / Other) ma **osobny plik Popup i Library**
- Animacja to **osobna zakładka** — `AnimationEditorControl`
- Sprite (planowany) to **osobna zakładka** — nie mieszać z animacją
- `PixelCanvasControl` to wspólna kontrolka używana przez edytor rysowania ORAZ przez każdą klatkę animacji

