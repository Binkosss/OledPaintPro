using System.IO;

namespace OledPaintPro.Models;

/// <summary>Pojedynczy szablon oka zapisany przez użytkownika.</summary>
public class PixelTemplate
{
    public string Id     { get; set; } = Guid.NewGuid().ToString("N");
    public string Name   { get; set; } = "Oko";
    public int    Width  { get; set; }
    public int    Height { get; set; }
    public bool[,] Pixels { get; set; } = new bool[1, 1];

    // ── Runtime — akcja po imporcie PNG (nie serializowane) ─────────────────
    [System.Text.Json.Serialization.JsonIgnore]
    public PngImportAction ResultAction { get; set; } = PngImportAction.OpenEditor;

    // ── Historia cofania per klatka (runtime, nie serializowane) ────────────
    [System.Text.Json.Serialization.JsonIgnore]
    public Stack<bool[,]> UndoStack { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public Stack<bool[,]> RedoStack { get; } = new();

    // ── Binarny zapis / odczyt (.eyb) ───────────────────────────────────────
    // Format: [4B W][4B H][N B name_utf8_len][name_utf8][W*H bitów spakowanych w bajty]
    public void SaveTo(string path)
    {
        using var fs = File.OpenWrite(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(Width);
        bw.Write(Height);

        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(Name);
        bw.Write(nameBytes.Length);
        bw.Write(nameBytes);

        // Piksele: każde 8 w jednym bajcie, row-major
        int total = Width * Height;
        int bytes = (total + 7) / 8;
        byte[] buf = new byte[bytes];
        for (int r = 0; r < Height; r++)
        for (int c = 0; c < Width;  c++)
        {
            int idx = r * Width + c;
            if (Pixels[r, c]) buf[idx / 8] |= (byte)(0x80 >> (idx % 8));
        }
        bw.Write(buf);
    }

    public static PixelTemplate LoadFrom(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        int w = br.ReadInt32();
        int h = br.ReadInt32();

        int nameLen   = br.ReadInt32();
        string name   = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));

        int total = w * h;
        int bytes = (total + 7) / 8;
        byte[] buf = br.ReadBytes(bytes);

        var pixels = new bool[h, w];
        for (int r = 0; r < h; r++)
        for (int c = 0; c < w; c++)
        {
            int idx = r * w + c;
            pixels[r, c] = (buf[idx / 8] & (0x80 >> (idx % 8))) != 0;
        }

        return new PixelTemplate
        {
            Id     = Path.GetFileNameWithoutExtension(path),
            Name   = name,
            Width  = w,
            Height = h,
            Pixels = pixels
        };
    }
}
