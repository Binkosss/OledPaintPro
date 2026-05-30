using System.IO;

namespace OledPaintPro.Models;

/// <summary>Zarządza lokalną biblioteką dodatkowych ikon/kształtów w folderze exe\others\</summary>
public class OtherLibrary
{
    public static readonly OtherLibrary Instance = new();
    private OtherLibrary() { }

    static string LibDir => Path.Combine(AppContext.BaseDirectory, "others");
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
