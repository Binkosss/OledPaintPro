using System.IO;

namespace OledPaintPro.Models;

/// <summary>Zarządza lokalną biblioteką szablonów ust w folderze exe\mouths\</summary>
public class MouthLibrary
{
    public static readonly MouthLibrary Instance = new();
    private MouthLibrary() { }

    static string LibDir => Path.Combine(AppContext.BaseDirectory, "mouths");
    const string EXT = ".eyb";

    public List<PixelTemplate> Templates { get; } = new();
    public event Action? Changed;

    public void Load()
    {
        Templates.Clear();
        Directory.CreateDirectory(LibDir);
        foreach (string file in Directory.GetFiles(LibDir, $"*{EXT}").OrderBy(f => f))
        {
            try { Templates.Add(PixelTemplate.LoadFrom(file)); }
            catch { }
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
