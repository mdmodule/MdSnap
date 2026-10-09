using System.IO;
using System.Text;
using System.Windows;

namespace MdSnap.UI;

/// <summary>
/// 字体许可查看窗口。
///
/// 为什么要单独做一个窗口：字体（思源黑体子集）被内嵌进单文件 exe 里，
/// 用户在文件系统上拿不到 OFL 许可证文本，而 OFL 要求许可证随字体一起分发。
/// 这里直接把打包进程序集的 OFL.txt 读出来显示 —— 既满足合规，又保住"单文件"。
/// </summary>
public partial class LicenseWindow : Window
{
    private const string ResourceUri = "pack://application:,,,/Assets/Fonts/OFL.txt";

    public LicenseWindow()
    {
        InitializeComponent();
        LicenseText.Text = LoadLicenseText();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFx.Attach(this);
    }

    private static string LoadLicenseText()
    {
        try
        {
            using var stream = Application.GetResourceStream(new Uri(ResourceUri))?.Stream;
            if (stream is null) return "（未能从程序集里读到许可证文件）";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "（读取许可证失败：" + ex.Message + "）";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
