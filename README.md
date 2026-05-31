# 🖥️ OledPaintPro

Edytor grafiki pikselowej dla wyświetlaczy OLED — stworzony w WPF (.NET 8).

Aplikacja pozwala rysować, edytować i eksportować bitmapy bezpośrednio jako tablice w formacie `.h` (C/C++), gotowe do wgrania na mikrokontroler (Arduino, ESP32 itp.).

---

## ✨ Funkcje

- 🎨 Rysowanie pikselami (ołówek, gumka, linia, prostokąt, wypełnienie)
- 🔤 Narzędzie tekstowe — wpisz tekst i wypal go w piksele
- 📋 Kopiuj / Wklej kod `.h` z gotowych tablic
- 🗂️ Biblioteka gotowych wzorów (oczy, usta i inne)
- 🎬 Edytor animacji — wiele klatek, podgląd odtwarzania
- 📤 Eksport do pliku `.h` zgodnego z bibliotekami OLED (np. U8g2, Adafruit SSD1306)
- 🖼️ Obsługa różnych rozdzielczości wyświetlaczy OLED

---

## 🛠️ Technologie

- C# / WPF / .NET 8
- Windows 10/11

---

## 🚀 Uruchomienie

1. Sklonuj repozytorium
2. Otwórz `OledPaintPro.sln` w Visual Studio 2022+
3. Zbuduj i uruchom (F5)

---

## 💡 Historia powstania

Nie jestem programistą C# — na co dzień nie piszę w tym języku. Po prostu potrzebowałem narzędzia **tu i teraz**, żeby wygodnie rysować grafikę na wyświetlacz OLED do swojego projektu. Żadne gotowe rozwiązanie mnie nie satysfakcjonowało, więc postanowiłem zrobić własne.

Przy budowie tej aplikacji ogromnie pomógł mi **GitHub Copilot** — dosłownie w dwa dni ogarnął całość: architekturę, UI, logikę rysowania, edytor animacji, eksport... Potrzeba jest matką wynalazków (przydatnych) 😄

---

## 📄 Licencja

[MIT](LICENSE)

---

## 🙏 Podziękowania / Third-party credits

Część predefiniowanych bitmap pochodzi z projektu 
**[Lopaka](https://github.com/sbrin/lopaka)** autorstwa [@sbrin](https://github.com/sbrin)
i współtwórców, udostępnionego na licencji **Apache License 2.0**.

> Copyright (c) sbrin and Lopaka contributors  
> Licensed under the Apache License, Version 2.0  
> https://www.apache.org/licenses/LICENSE-2.0
