using System.IO;
using System.Text.Json;
using PrintMark.Core;

namespace PrintMark.AdminApp;

public record SavedWatermark(string Name, WatermarkConfig Config, int PaperIndex, int OrientationIndex);

public sealed class WatermarkPresetStore
{
    private readonly string _path;
    public WatermarkPresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintMark", "watermark-presets.json");
    }
    public List<SavedWatermark> Load()
    {
        if (!File.Exists(_path)) return new();
        return JsonSerializer.Deserialize<List<SavedWatermark>>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("The saved watermark library is empty or invalid.");
    }
    public List<SavedWatermark> Save(SavedWatermark preset)
    {
        var name = preset.Name.Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Enter a watermark name of 1–80 characters.");
        var items = Load();
        items.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        items.Add(preset with { Name = name });
        items = items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return items;
    }
}
