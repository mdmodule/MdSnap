using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace MdSnap.Output;

/// <summary>
/// 存盘与历史扫描。文件名格式：yyyyMMdd_序号（例如 20261008_001.png）。
/// 目录与格式来自用户设置。
/// </summary>
public static class FileSaver
{
    private static readonly string[] KnownExts = { ".png", ".jpg", ".jpeg", ".bmp" };

    public static string DesktopDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>保存根目录（设置为空时用系统桌面）。</summary>
    public static string SaveRoot
    {
        get
        {
            var dir = AppSettings.Current.SaveDirectory;
            return string.IsNullOrWhiteSpace(dir) ? DesktopDir : dir;
        }
    }

    /// <summary>今天的日期串（yyyyMMdd）。</summary>
    public static string Today => DateTime.Now.ToString("yyyyMMdd");

    /// <summary>
    /// 实际落盘目录 = 根目录下以当天日期命名的子文件夹（不存在就建）。
    /// 截图不再直接堆在桌面，而是按天归档到 <c>桌面\20261008\</c> 这样的文件夹里。
    /// </summary>
    public static string SaveDir
    {
        get
        {
            string path = Path.Combine(SaveRoot, Today);
            try { Directory.CreateDirectory(path); } catch { }
            return path;
        }
    }

    public static string Format => NormalizeFormat(AppSettings.Current.SaveFormat);

    public static string NormalizeFormat(string? raw) => (raw ?? "").Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "jpg",
        "bmp" => "bmp",
        _ => "png",
    };

    public static string Extension => "." + Format;

    /// <summary>
    /// 按当天已有文件算出下一个可用路径。
    ///
    /// 序号取「目录里最大号」和「已用过的最大号（持久化）」的较大值 +1：
    /// 后者保证**删掉旧图之后也不会重号**（只扫目录的话，删空就又回到 001）。
    /// 取到号后立刻写回设置，等于把号占住 —— 即使这次最终没落盘，也不会再发第二次。
    /// </summary>
    public static string NextPath(string? ext = null)
    {
        ext ??= Extension;
        string date = DateTime.Now.ToString("yyyyMMdd");
        string dir = SaveDir;

        try { Directory.CreateDirectory(dir); } catch { /* 目录建不了就交给写入时报错 */ }

        // 日期文件夹 + 根目录都扫：前者是新的归档位置，后者兼容升级前平铺在桌面的旧图
        int scanned = Math.Max(
            MaxSequenceInDirectory(dir, date),
            MaxSequenceInDirectory(SaveRoot, date));

        var settings = AppSettings.Current;
        int used = settings.SequenceDate == date ? settings.SequenceLast : 0;

        int next = Math.Max(scanned, used) + 1;

        settings.SequenceDate = date;
        settings.SequenceLast = next;
        settings.Save();

        return Path.Combine(dir, $"{date}_{next:D3}{ext}");
    }

    /// <summary>目录里当天已存在的最大序号（别人放进来的文件也认）。</summary>
    private static int MaxSequenceInDirectory(string dir, string date)
    {
        int max = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, date + "_*"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!KnownExts.Contains(ext)) continue;
                if (name.Length <= date.Length + 1 || name[date.Length] != '_') continue;
                if (int.TryParse(name[(date.Length + 1)..], out int n) && n > max) max = n;
            }
        }
        catch { /* 读不到就只靠持久化的序号 */ }
        return max;
    }

    public static void Save(Bitmap bmp, string path)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            _ => ImageFormat.Png,
        };
        bmp.Save(path, format);
    }

    /// <summary>按扩展名给 WPF 编码器（编辑器导出用）。</summary>
    public static BitmapEncoder CreateEncoder(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

    /// <summary>该格式是否需要先转成无 alpha 的 Bgr24（JPEG 不支持透明）。</summary>
    public static bool NeedsOpaqueConversion(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

    /// <summary>
    /// 扫描历史截图：根目录下的散图（旧版本留下的）+ 所有日期文件夹里的图，按修改时间倒序。
    /// </summary>
    public static List<HistoryItem> ScanHistory(int limit = 120)
    {
        var list = new List<HistoryItem>();
        try
        {
            string root = SaveRoot;
            if (!Directory.Exists(root)) return list;

            var folders = new List<string> { root };
            foreach (var sub in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(sub);
                if (name.Length == 8 && name.All(char.IsDigit)) folders.Add(sub);   // 按天归档的文件夹
            }

            var files = new List<FileInfo>();
            foreach (var folder in folders)
            {
                try
                {
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
                    {
                        if (KnownExts.Contains(f.Extension.ToLowerInvariant())) files.Add(f);
                    }
                }
                catch { /* 单个文件夹读不了就跳过 */ }
            }

            foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(limit))
                list.Add(new HistoryItem(f.FullName, f.Name, f.LastWriteTime));
        }
        catch { /* 目录不可读就返回空列表 */ }
        return list;
    }
}

/// <summary>一条历史截图。缩略图延迟加载。</summary>
public sealed class HistoryItem : INotifyPropertyChanged
{
    private BitmapImage? _thumb;

    public string Path { get; }
    public string Name { get; }
    public DateTime Time { get; }
    public string TimeText => Time.ToString("MM-dd HH:mm");

    public BitmapImage? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryItem(string path, string name, DateTime time)
    {
        Path = path;
        Name = name;
        Time = time;
    }

    /// <summary>解码缩略图，可在后台线程调用；赋值给 Thumb 必须在 UI 线程。</summary>
    public BitmapImage? DecodeThumb(int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = width;
            bmp.UriSource = new Uri(Path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;   // 个别文件坏了就留空缩略图
        }
    }
}
