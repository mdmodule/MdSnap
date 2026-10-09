using System.IO;
using System.Text.Json;

namespace MdSnap;

/// <summary>用户设置，持久化到 %LOCALAPPDATA%\MdSnap\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>热键组合文本，对应 AppConfig.Presets 里的一项。</summary>
    public string HotkeyText { get; set; } = "Ctrl+Shift+A";

    /// <summary>保存目录；空字符串 = 用系统桌面。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>保存格式：png / jpg / bmp。</summary>
    public string SaveFormat { get; set; } = "png";

    /// <summary>序号计数对应的日期（yyyyMMdd）。</summary>
    public string SequenceDate { get; set; } = "";

    /// <summary>该日期已用到的最大序号。删掉旧图后仍从这里往下走，不会重号。</summary>
    public int SequenceLast { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdSnap", "settings.json");

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 设置文件损坏就用默认值，不影响使用
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 写失败不阻断流程
        }
    }
}
