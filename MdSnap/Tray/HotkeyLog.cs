using System.IO;
using System.Text;

namespace MdSnap.Tray;

/// <summary>
/// 热键排查日志（默认关闭）。
///
/// 打开方式（二选一）：
///   · 启动参数加 --hotkey-log
///   · %LOCALAPPDATA%\MdSnap\settings.json 里 "HotkeyLog": true
///
/// 记录：注册/反注册的结果与错误码、每一次收到 WM_HOTKEY、看门狗的修复动作。
/// "按热键没反应"这类偶发问题只能靠这个定位：看按键到底有没有到进程。
/// 关闭时不产生任何文件、不增加 IO。
/// </summary>
internal static class HotkeyLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static bool Enabled { get; private set; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdSnap", "hotkey.log");

    public static void Init(bool fromSettings, bool fromCommandLine)
    {
        Enabled = fromSettings || fromCommandLine;
        Write($"--- MdSnap 热键日志启动 (settings={fromSettings} cli={fromCommandLine}) pid={Environment.ProcessId} ---");
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // 长期开着也别把文件写到无限大：超了就把后一半留下
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.WriteAllText(FilePath, ReadTail(FilePath, MaxBytes / 2), Encoding.UTF8);

                File.AppendAllText(FilePath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // 日志失败绝不影响主流程
            }
        }
    }

    private static string ReadTail(string path, long keep)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - keep), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
