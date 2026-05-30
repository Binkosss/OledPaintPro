using System.IO;

namespace OledPaintPro.Models;

/// <summary>Zarządza lokalną biblioteką szablonów oczu w folderze exe\eyes\</summary>
public class EyeLibrary
{
    // ── Singleton ────────────────────────────────────────────────────────────
    public static readonly EyeLibrary Instance = new();
    private EyeLibrary() { }

    // ── Ścieżka storage ──────────────────────────────────────────────────────
    static string LibDir =>
        Path.Combine(AppContext.BaseDirectory, "eyes");

    const string EXT = ".eyb";

    // ── Załadowana lista ─────────────────────────────────────────────────────
    public List<PixelTemplate> Templates { get; } = new();

    // ── Eventy ──────────────────────────────────────────────────────────────
    public event Action? Changed;

    // ── API ─────────────────────────────────────────────────────────────────
    public void Load()
    {
        Templates.Clear();
        Directory.CreateDirectory(LibDir);
        foreach (string file in Directory.GetFiles(LibDir, $"*{EXT}").OrderBy(f => f))
        {
            try { Templates.Add(PixelTemplate.LoadFrom(file)); }
            catch { /* uszkodzony plik — pomiń */ }
        }
        Changed?.Invoke();
    }

    public void Save(PixelTemplate t)
    {
        Directory.CreateDirectory(LibDir);
        string path = Path.Combine(LibDir, t.Id + EXT);
        t.SaveTo(path);

        int idx = Templates.FindIndex(x => x.Id == t.Id);
        if (idx >= 0) Templates[idx] = t;
        else          Templates.Add(t);

        Changed?.Invoke();
    }

    public void Delete(PixelTemplate t)
    {
        string path = Path.Combine(LibDir, t.Id + EXT);
        if (File.Exists(path)) File.Delete(path);
        Templates.RemoveAll(x => x.Id == t.Id);
        Changed?.Invoke();
    }
}
