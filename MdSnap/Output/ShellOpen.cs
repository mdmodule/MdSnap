using System.Diagnostics;

namespace MdSnap.Output;

/// <summary>
/// 用资源管理器打开目录 / 定位文件。
/// 必须走 UseShellExecute=true —— 直接 <c>Process.Start("explorer.exe", 路径)</c> 在新版 .NET 下
/// 会以当前工作目录为基准去解析，工作目录不可访问时直接抛「拒绝访问」，用户点「打开保存目录」就失败。
/// </summary>
internal static class ShellOpen
{
    /// <summary>打开一个目录。</summary>
    public static void Folder(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>在资源管理器中选中某个文件。</summary>
    public static void RevealInFolder(string filePath)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

    /// <summary>用系统默认程序打开文件。</summary>
    public static void File(string path)
        => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
