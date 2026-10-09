using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MdSnap.Capture;
using MdSnap.Output;

namespace MdSnap;

public partial class App : Application
{
    private Mutex? _mutex;
    private Tray.TrayIcon? _tray;
    private UI.MainWindow? _main;

    /// <summary>当前 App 实例（主界面用它发托盘提示）。</summary>
    internal static App? Instance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 隐藏自检：MdSnap.exe --selftest [输出图片路径]
        // 不启动托盘，跑一次「全屏截图 -> 存盘」后用同名 .result.txt 报告结果。用于自动化验证。
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            RunSelfTest(e.Args.Length >= 2 ? e.Args[1] : null);
            Shutdown();
            return;
        }

        // 单实例：防止重复启动出现多个托盘图标 / 多个主界面
        _mutex = new Mutex(true, @"Global\MdSnap.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        Instance = this;

        // 前台窗口监视器必须先起来，否则「活动窗口」拿不到正确的目标
        Capture.ForegroundWatcher.Start();

        _tray = new Tray.TrayIcon(
            mainWindowProvider: () => _main is { IsVisible: true } ? _main : null,
            showMainWindow: ShowMainWindow);
        _tray.Initialize();

        // 启动即显示主界面（像红蜻蜓一样），同时托盘常驻
        ShowMainWindow();
    }

    /// <summary>显示 / 唤回主界面。</summary>
    private void ShowMainWindow()
    {
        if (_main is null)
        {
            _main = new UI.MainWindow();
            _main.Closed += (_, _) => _main = null;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;    // 强制前置
        _main.Topmost = false;
    }

    /// <summary>经托盘图标弹一条气泡提示。</summary>
    internal void Notify(string title, string message)
        => _tray?.ShowInfo(title, message);

    /// <summary>按当前设置重新注册热键（设置页换热键后调用）。返回是否成功。</summary>
    internal bool ReapplyHotkey() => _tray?.ReapplyHotkey() ?? false;

    [System.Runtime.InteropServices.DllImport("user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    private static void RunSelfTest(string? outPath)
    {
        outPath ??= Path.Combine(Path.GetTempPath(), "mdsnap_selftest.png");
        string report = outPath + ".result.txt";
        try
        {
            using var bmp = ScreenCapturer.CaptureVirtualScreen();
            FileSaver.Save(bmp, outPath);
            long size = new FileInfo(outPath).Length;

            // 顺带验证托盘图标资源能否通过 pack URI 加载（托盘空白图标是常见坑）
            string iconInfo;
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/MdSnap.ico");
                using var s = GetResourceStream(uri)?.Stream;
                if (s is null) iconInfo = "FAIL: 资源流为空";
                else using (var ico = new System.Drawing.Icon(s)) iconInfo = $"OK: {ico.Width}x{ico.Height}";
            }
            catch (Exception iex) { iconInfo = "FAIL: " + iex.Message; }

            // 覆盖层：XAML 能否加载 + DIP→物理像素裁剪换算是否正确
            string overlayInfo;
            try
            {
                using var probe = new System.Drawing.Bitmap(800, 600);          // 物理 800x600
                using var crop = Overlay.SelectionOverlay.ComputeCrop(
                    new Rect(25, 30, 100, 75), 200, 150, probe);                // 逻辑面 200x150 -> scale 4
                if (crop is null) overlayInfo = "FAIL: crop=null";
                else
                {
                    bool expect = crop.Width == 400 && crop.Height == 300;
                    overlayInfo = $"{(expect ? "OK" : "FAIL")}: {crop.Width}x{crop.Height} (期望 400x300)";
                }

                var ov = new Overlay.SelectionOverlay(probe);                    // 触发 XAML/BAML 加载
                overlayInfo += ov is null ? " | XAML=FAIL" : " | XAML=OK";
            }
            catch (Exception ox) { overlayInfo = "FAIL: " + ox.Message; }

            // 标注渲染：逐图元验证（底图用 2px 黑白棋盘，便于检验马赛克是否真的抹平了细节）
            string editorInfo;
            try
            {
                using var checker = new System.Drawing.Bitmap(400, 300);
                for (int y = 0; y < 300; y++)
                    for (int x = 0; x < 400; x++)
                        checker.SetPixel(x, y, ((x / 2 + y / 2) % 2 == 0)
                            ? System.Drawing.Color.Black : System.Drawing.Color.White);

                var baseSrc = Editor.AnnotateWindow.ToBitmapSource(checker);
                double block = Math.Max(6, 400 / 70.0);
                var small = new TransformedBitmap(baseSrc, new ScaleTransform(1.0 / block, 1.0 / block));
                small.Freeze();
                var ctx = new Editor.RenderContext { Pixelated = small, ImageWidth = 400, ImageHeight = 300 };

                var annos = new List<Editor.Anno>
                {
                    new Editor.ArrowAnno { Start = new Point(20, 20), End = new Point(180, 120), Color = Colors.Red, Thickness = 6 },
                    new Editor.RectAnno { Start = new Point(200, 20), End = new Point(380, 120), Color = Colors.Lime, Thickness = 5 },
                    new Editor.HighlightAnno { Start = new Point(20, 140), End = new Point(200, 190), Color = Colors.Yellow, Thickness = 4 },
                    new Editor.MosaicAnno { Start = new Point(240, 160), End = new Point(380, 270), Color = Colors.Black, Thickness = 4 },
                };

                var composed = Editor.AnnotateWindow.ComposeImage(baseSrc, null, annos, ctx, 400, 300);
                var conv = new FormatConvertedBitmap(composed, PixelFormats.Bgra32, null, 0);
                int cw = conv.PixelWidth, chh = conv.PixelHeight;
                var buf = new byte[cw * chh * 4];
                conv.CopyPixels(buf, cw * 4, 0);

                int red = 0, lime = 0, highlight = 0;
                for (int i = 0; i < buf.Length; i += 4)
                {
                    byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    if (r > 150 && g < 110 && b < 110) red++;                 // 箭头：不透明红
                    else if (g > 150 && r < 170 && b < 120) lime++;           // 矩形：不透明绿
                    if (r - b > 40) highlight++;                              // 高亮：半透明黄 -> r 明显高于 b
                }

                // 马赛克：区域内黑白细节应被抹平，同尺寸对照区仍应是锐利棋盘
                double ExtremeRatio(int x0, int y0, int x1, int y1)
                {
                    int ex = 0, tot = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            byte v = buf[(y * cw + x) * 4 + 1];
                            if (v < 60 || v > 195) ex++;
                            tot++;
                        }
                    return (double)ex / tot;
                }
                double mosaicFlat = ExtremeRatio(250, 170, 370, 260);
                double controlSharp = ExtremeRatio(30, 210, 150, 290);

                // 文字：单独在纯黑底上画白字，确认真的有白色像素
                bool textOk;
                {
                    using var black = new System.Drawing.Bitmap(400, 120);
                    using (var g = System.Drawing.Graphics.FromImage(black))
                        g.Clear(System.Drawing.Color.Black);
                    var bSrc = Editor.AnnotateWindow.ToBitmapSource(black);
                    var small2 = new TransformedBitmap(bSrc, new ScaleTransform(1.0 / block, 1.0 / block));
                    small2.Freeze();
                    var ctx2 = new Editor.RenderContext { Pixelated = small2, ImageWidth = 400, ImageHeight = 120 };
                    var only = new List<Editor.Anno>
                    {
                        new Editor.TextAnno { Position = new Point(20, 30), Text = "MdSnap 测试", FontSize = 30, Color = Colors.White, Thickness = 4 },
                    };
                    var txt = Editor.AnnotateWindow.ComposeImage(bSrc, null, only, ctx2, 400, 120);
                    var c2 = new FormatConvertedBitmap(txt, PixelFormats.Bgra32, null, 0);
                    var b2 = new byte[c2.PixelWidth * c2.PixelHeight * 4];
                    c2.CopyPixels(b2, c2.PixelWidth * 4, 0);
                    int whitePx = 0;
                    for (int i = 0; i < b2.Length; i += 4)
                        if (b2[i] > 200 && b2[i + 1] > 200 && b2[i + 2] > 200) whitePx++;
                    textOk = whitePx > 100;
                }

                bool shapesOk = red > 50 && lime > 50 && highlight > 500;
                bool mosaicOk = mosaicFlat < 0.15 && controlSharp > 0.5;
                bool allOk = shapesOk && mosaicOk && textOk;

                editorInfo = $"{(allOk ? "OK" : "FAIL")}: " +
                             $"箭头{(red > 50 ? "OK" : "FAIL")}({red}px) " +
                             $"矩形{(lime > 50 ? "OK" : "FAIL")}({lime}px) " +
                             $"高亮{(highlight > 500 ? "OK" : "FAIL")}({highlight}px) | " +
                             $"马赛克{(mosaicOk ? "OK" : "FAIL")}(平整{mosaicFlat:P0}/对照{controlSharp:P0}) | " +
                             $"文字{(textOk ? "OK" : "FAIL")}";
            }
            catch (Exception ex2) { editorInfo = "FAIL: " + ex2.Message; }

            // 橡皮擦命中判定：每种图元都要「压上去=命中、远离=不命中」
            string eraserInfo;
            try
            {
                var probes = new (string Name, Editor.Anno A, Point On, Point Off)[]
                {
                    ("箭头", new Editor.ArrowAnno { Start = new Point(20, 20), End = new Point(180, 120), Color = Colors.Red, Thickness = 6 },
                        new Point(100, 70), new Point(300, 250)),
                    ("矩形", new Editor.RectAnno { Start = new Point(200, 20), End = new Point(380, 120), Color = Colors.Lime, Thickness = 5 },
                        new Point(290, 70), new Point(50, 250)),
                    ("高亮", new Editor.HighlightAnno { Start = new Point(20, 140), End = new Point(200, 190), Color = Colors.Yellow, Thickness = 4 },
                        new Point(100, 165), new Point(330, 250)),
                    ("马赛克", new Editor.MosaicAnno { Start = new Point(240, 160), End = new Point(380, 270), Color = Colors.Black, Thickness = 4 },
                        new Point(300, 210), new Point(60, 250)),
                    ("文字", new Editor.TextAnno { Position = new Point(20, 30), Text = "MdSnap 测试", FontSize = 30, Color = Colors.White, Thickness = 4 },
                        new Point(60, 45), new Point(300, 250)),
                };

                var fails = new List<string>();
                foreach (var (name, anno, on, off) in probes)
                {
                    if (!anno.HitTest(on, 6)) fails.Add($"{name}漏判");
                    if (anno.HitTest(off, 6)) fails.Add($"{name}误判");
                }
                eraserInfo = fails.Count == 0
                    ? "OK: 5/5 图元命中判定正确（命中/不命中双向验证）"
                    : "FAIL: " + string.Join(", ", fails);
            }
            catch (Exception ex3) { eraserInfo = "FAIL: " + ex3.Message; }

            // 橡皮擦（擦画面）：画白 / 撤销回滚 / 合成后真的变白
            string eraseRasterInfo;
            try
            {
                int ew = 120, eh = 80;
                var ebuf = new byte[ew * eh * 4];

                // 1) 画白：圆心 alpha=255，远处 alpha=0
                var r1 = Editor.AnnotateWindow.PaintEraseCircle(ebuf, ew, eh, new Point(60, 40), 12);
                int centerA = ebuf[((40 * ew) + 60) * 4 + 3];
                int farA = ebuf[((3 * ew) + 3) * 4 + 3];
                bool painted = r1.Width > 0 && centerA == 255 && farA == 0;

                // 2) 撤销：小圆快照 -> 大圆盖住 -> 回滚 -> 小圆区域复原、区域外不受影响
                var rA = Editor.AnnotateWindow.PaintEraseCircle(ebuf, ew, eh, new Point(20, 20), 6);
                var snap = Editor.AnnotateWindow.ExtractRegion(ebuf, ew, rA);
                Editor.AnnotateWindow.PaintEraseCircle(ebuf, ew, eh, new Point(20, 20), 12);
                Editor.AnnotateWindow.RestoreRegion(ebuf, ew, rA, snap);
                bool undoOk = ebuf[((14 * ew) + 14) * 4 + 3] == 0      // 区内角点(在r=6圆外)恢复为未擦
                              && ebuf[((20 * ew) + 20) * 4 + 3] == 255 // 圆心仍白
                              && ebuf[((30 * ew) + 20) * 4 + 3] == 255;// 区外大圆未回滚

                // 3) 合成：深色底 + 擦除层 -> 擦过处变白，别处仍深色
                bool blendOk;
                {
                    using var dark = new System.Drawing.Bitmap(ew, eh);
                    using (var g = System.Drawing.Graphics.FromImage(dark))
                        g.Clear(System.Drawing.Color.FromArgb(255, 20, 20, 20));
                    var dSrc = Editor.AnnotateWindow.ToBitmapSource(dark);

                    var clean = new byte[ew * eh * 4];
                    Editor.AnnotateWindow.PaintEraseCircle(clean, ew, eh, new Point(60, 40), 12);
                    var eraseWb = new WriteableBitmap(ew, eh, 96, 96, PixelFormats.Bgra32, null);
                    eraseWb.WritePixels(new Int32Rect(0, 0, ew, eh), clean, ew * 4, 0);

                    var ctxE = new Editor.RenderContext { Pixelated = dSrc, ImageWidth = ew, ImageHeight = eh };
                    var outImg = Editor.AnnotateWindow.ComposeImage(dSrc, eraseWb, Array.Empty<Editor.Anno>(), ctxE, ew, eh);
                    var cE = new FormatConvertedBitmap(outImg, PixelFormats.Bgra32, null, 0);
                    var bE = new byte[cE.PixelWidth * cE.PixelHeight * 4];
                    cE.CopyPixels(bE, cE.PixelWidth * 4, 0);

                    int ci = ((40 * ew) + 60) * 4;   // 擦过 -> 应白
                    int xi = ((6 * ew) + 6) * 4;     // 没擦 -> 应仍是 20
                    blendOk = bE[ci] > 200 && bE[ci + 1] > 200 && bE[ci + 2] > 200
                              && Math.Abs(bE[xi] - 20) < 12;
                }

                bool ok = painted && undoOk && blendOk;
                eraseRasterInfo = $"{(ok ? "OK" : "FAIL")}: " +
                                  $"画白{(painted ? "OK" : "FAIL")}(中心α{centerA}/远处α{farA}) " +
                                  $"撤销回滚{(undoOk ? "OK" : "FAIL")} " +
                                  $"合成变白{(blendOk ? "OK" : "FAIL")}";
            }
            catch (Exception ex4) { eraseRasterInfo = "FAIL: " + ex4.Message; }

            // 主界面（启动界面）XAML 能否加载
            string mainUiInfo;
            try
            {
                var mw = new UI.MainWindow();
                mainUiInfo = mw is null ? "FAIL" : "OK: MainWindow XAML + 图标加载通过";
            }
            catch (Exception ex5) { mainUiInfo = "FAIL: " + ex5.Message; }

            // 存储设置：目录切换、格式归一化、序号递增
            string storageInfo;
            try
            {
                string date = DateTime.Now.ToString("yyyyMMdd");
                string tmp = Path.Combine(Path.GetTempPath(), "MdSnapTest_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tmp);

                var backupDir = AppSettings.Current.SaveDirectory;
                var backupFmt = AppSettings.Current.SaveFormat;
                var backupSeqDate = AppSettings.Current.SequenceDate;
                var backupSeqLast = AppSettings.Current.SequenceLast;

                // 整个设置文件先存一份，测完原样写回。
                // 只还原内存字段是不够的 —— NextPath() 内部会 Save()，测试值已经落盘了。
                string settingsPath = AppSettings.FilePath;
                byte[]? settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;

                AppSettings.Current.SaveDirectory = tmp;
                AppSettings.Current.SaveFormat = "jpg";
                AppSettings.Current.SequenceDate = "";
                AppSettings.Current.SequenceLast = 0;

                string seedDir = Path.Combine(tmp, date);
                Directory.CreateDirectory(seedDir);
                File.WriteAllText(Path.Combine(seedDir, $"{date}_001.jpg"), "x");
                File.WriteAllText(Path.Combine(seedDir, $"{date}_007.jpg"), "x");
                File.WriteAllText(Path.Combine(seedDir, $"{date}_003.png"), "x");   // 别的格式也算序号

                string next = FileSaver.NextPath();
                bool nameOk = Path.GetFileName(next) == $"{date}_008.jpg";
                bool fmtOk = FileSaver.NormalizeFormat("JPEG") == "jpg"
                             && FileSaver.NormalizeFormat(".PNG") == "png"
                             && FileSaver.NormalizeFormat("bmp") == "bmp";

                // 必须落在 <根目录>\<日期>\ 子文件夹里，而不是根目录平铺
                bool folderOk = Path.GetFileName(Path.GetDirectoryName(next)) == date;

                // 关键行为：把旧图全删掉，序号必须继续往下走，不能回到 001 重号
                foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)) File.Delete(f);
                string afterDelete = Path.GetFileName(FileSaver.NextPath());
                bool noRepeatOk = afterDelete == $"{date}_009.jpg";

                // 999 用完不能回绕：应该自然延长成 4 位数
                AppSettings.Current.SequenceDate = date;
                AppSettings.Current.SequenceLast = 999;
                string stem999 = Path.GetFileNameWithoutExtension(FileSaver.NextPath());
                bool overflowOk = stem999 == $"{date}_1000";
                string overflowText = stem999[(date.Length + 1)..];

                bool allOk = nameOk && fmtOk && folderOk && noRepeatOk && overflowOk;

                storageInfo = $"{(allOk ? "OK" : "FAIL")}: " +
                              $"下一个={Path.GetFileName(next)} (期望 {date}_008.jpg) " +
                              $"| 落日期文件夹{(folderOk ? "OK" : "FAIL")} " +
                              $"| 删空后={afterDelete} (期望 {date}_009.jpg) " +
                              $"| 999后={overflowText} (期望 1000)";

                AppSettings.Current.SaveDirectory = backupDir;
                AppSettings.Current.SaveFormat = backupFmt;
                AppSettings.Current.SequenceDate = backupSeqDate;
                AppSettings.Current.SequenceLast = backupSeqLast;

                // 把设置文件恢复成测试前的样子（测完绝不能留下测试值）
                try
                {
                    if (settingsBackup is not null) File.WriteAllBytes(settingsPath, settingsBackup);
                    else if (File.Exists(settingsPath)) File.Delete(settingsPath);
                }
                catch { }

                try { Directory.Delete(tmp, true); } catch { }
            }
            catch (Exception ex6) { storageInfo = "FAIL: " + ex6.Message; }

            // 活动窗口目标解析：任务栏/桌面/零值都必须排除
            string activeWinInfo;
            try
            {
                Capture.ForegroundWatcher.Start();

                bool zeroOk = !Capture.ForegroundWatcher.IsCapturableWindow(IntPtr.Zero);

                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                bool trayOk = tray == IntPtr.Zero
                              || !Capture.ForegroundWatcher.IsCapturableWindow(tray);

                IntPtr progman = FindWindow("Progman", null);
                bool progmanOk = progman == IntPtr.Zero
                                 || !Capture.ForegroundWatcher.IsCapturableWindow(progman);

                IntPtr resolved = Capture.ForegroundWatcher.ResolveActiveWindow();
                bool resolveOk = resolved == IntPtr.Zero
                                 || Capture.ForegroundWatcher.IsCapturableWindow(resolved);

                // 按坐标找窗口（覆盖层"活动窗口"悬停用）
                var vs = Capture.ScreenCapturer.VirtualScreenBounds();
                bool offScreenOk = Capture.ScreenCapturer.WindowAtPoint(vs.X - 500, vs.Y - 500) == IntPtr.Zero;
                bool emptyTitleOk = Capture.ScreenCapturer.WindowTitle(IntPtr.Zero).Length == 0;

                IntPtr atTray = Capture.ScreenCapturer.WindowAtPoint(
                    vs.X + vs.Width / 2, vs.Y + vs.Height - 6);
                bool trayPointOk = atTray == IntPtr.Zero
                                   || Capture.ForegroundWatcher.IsCapturableWindow(atTray);

                bool ok = zeroOk && trayOk && progmanOk && resolveOk
                          && offScreenOk && emptyTitleOk && trayPointOk;

                activeWinInfo = $"{(ok ? "OK" : "FAIL")}: 零值排除{(zeroOk ? "OK" : "FAIL")}" +
                                $" 任务栏排除{(trayOk ? "OK" : "FAIL")}" +
                                $" 桌面排除{(progmanOk ? "OK" : "FAIL")}" +
                                $" 解析结果有效{(resolveOk ? "OK" : "FAIL")}" +
                                $" | 坐标找窗: 屏外{(offScreenOk ? "OK" : "FAIL")}" +
                                $" 空标题{(emptyTitleOk ? "OK" : "FAIL")}" +
                                $" 任务栏位置{(trayPointOk ? "OK" : "FAIL")}";
            }
            catch (Exception ex7) { activeWinInfo = "FAIL: " + ex7.Message; }

            // 内嵌字体能否真正解析出家族名（解析不到 WPF 会静默回退，必须显式验证）
            string fontInfo;
            try
            {
                var ff = Application.Current?.TryFindResource("UiFont") as System.Windows.Media.FontFamily;
                var names = ff?.FamilyNames.Values?.ToList() ?? new List<string>();
                bool ok = names.Any(n => n.Contains("MdSnap Sans SC", StringComparison.Ordinal));
                fontInfo = $"{(ok ? "OK" : "FAIL")}: 解析到 [{string.Join(" / ", names)}]";
            }
            catch (Exception ex8) { fontInfo = "FAIL: " + ex8.Message; }

            // 托盘右键菜单：样式解析 + 实际渲染成图（便于人工核对外观，托盘菜单没法自动点开）
            string menuInfo;
            try
            {
                var keys = new[] { "AppContextMenu", "AppMenuItem", "AppSeparator", "AppMenuItemDanger" };
                var missing = keys.Where(k => Application.Current?.TryFindResource(k) is null).ToList();

                var menu = new System.Windows.Controls.ContextMenu
                {
                    Style = Application.Current?.TryFindResource("AppContextMenu") as Style,
                };

                (string Header, string? Gesture, string StyleKey, bool Sep)[] rows =
                {
                    ("显示主界面", null, "AppMenuItem", false),
                    ("截图（区域 / 全屏）", "Ctrl+Shift+A", "AppMenuItem", true),
                    ("抓取活动窗口", null, "AppMenuItem", false),
                    ("打开保存目录", null, "AppMenuItem", true),
                    ("退出", null, "AppMenuItemDanger", true),
                };
                foreach (var (header, gesture, styleKey, sep) in rows)
                {
                    if (sep)
                    {
                        menu.Items.Add(new System.Windows.Controls.Separator
                        {
                            Style = Application.Current?.TryFindResource("AppSeparator") as Style,
                        });
                    }
                    menu.Items.Add(new System.Windows.Controls.MenuItem
                    {
                        Header = header,
                        InputGestureText = gesture ?? "",
                        Style = Application.Current?.TryFindResource(styleKey) as Style,
                    });
                }

                menu.Measure(new Size(500, 500));
                menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
                menu.UpdateLayout();

                int mw = Math.Max(1, (int)Math.Ceiling(menu.DesiredSize.Width));
                int mh = Math.Max(1, (int)Math.Ceiling(menu.DesiredSize.Height));
                var rtb = new RenderTargetBitmap(mw, mh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(menu);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string menuPath = outPath + ".menu.png";
                using (var fs = File.Create(menuPath)) enc.Save(fs);

                menuInfo = (missing.Count == 0 ? "OK" : "FAIL: 缺样式 " + string.Join(",", missing))
                           + $" | 预览 {mw}x{mh} -> {Path.GetFileName(menuPath)}";
            }
            catch (Exception ex9) { menuInfo = "FAIL: " + ex9.Message; }

            // 字体许可证必须能随程序集一起分发（SIL OFL 1.1 的合规要求）
            string licenseInfo;
            try
            {
                using var licenseStream = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/Fonts/OFL.txt"))?.Stream;
                using var licenseReader = new StreamReader(licenseStream!, System.Text.Encoding.UTF8);
                string licenseText = licenseReader.ReadToEnd();
                bool licenseOk = licenseText.Contains("SIL OPEN FONT LICENSE", StringComparison.OrdinalIgnoreCase);
                licenseInfo = $"{(licenseOk ? "OK" : "FAIL")}: 内嵌许可证 {licenseText.Length} 字节";
            }
            catch (Exception ex10) { licenseInfo = "FAIL: " + ex10.Message; }

            File.WriteAllText(report,
                $"OK\nbitmap={bmp.Width}x{bmp.Height}\nfile={outPath}\nbytes={size}\ntrayIcon={iconInfo}\noverlay={overlayInfo}\neditor={editorInfo}\neraser_hit={eraserInfo}\neraser_raster={eraseRasterInfo}\nmain_ui={mainUiInfo}\nstorage={storageInfo}\nactive_window={activeWinInfo}\nfont={fontInfo}\ntray_menu={menuInfo}\nfont_license={licenseInfo}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, "FAIL\n" + ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Capture.ForegroundWatcher.Instance?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
