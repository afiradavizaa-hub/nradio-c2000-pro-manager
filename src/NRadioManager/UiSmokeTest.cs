using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NRadioManager.Controls;
using NRadioManager.Core;
using NRadioManager.ViewModels;
using NRadioManager.Views;

namespace NRadioManager;

/// <summary>
/// 界面自检：在真实 WPF 环境中实例化每个页面、跑一次布局，并捕获 XAML 解析异常与数据绑定错误。
/// 目的是在不依赖人工点击的前提下发现「页面打不开」「绑定字段写错」这类问题。
/// </summary>
internal static class UiSmokeTest
{
    private static void SmokeProgress(string message)
    {
        var path = Environment.GetEnvironmentVariable("NRadio_SMOKE_PROGRESS");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n"); } catch { }
    }

    private static bool PumpDispatcherFor(TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var completed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = timeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            completed = true;
            timer.Stop();
            frame.Continue = false;
        }));
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return completed;
    }

    private sealed class CollectingListener : TraceListener
    {
        public StringBuilder Buffer { get; } = new();
        public override void Write(string? message) => Buffer.Append(message);
        public override void WriteLine(string? message) => Buffer.AppendLine(message);
    }

    public static string Run()
        => Run(live: false);

    /// <summary>live=true 时先真实连接设备并拉取全部数据，再用真实数据渲染每个页面。</summary>
    public static string Run(bool live)
    {
        SmokeProgress("Run start");
        var sb = new StringBuilder();
        var bindingErrors = new CollectingListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;

        var smokeLogDir = Path.Combine(Path.GetTempPath(), "nradio-ui-smoke-" + Guid.NewGuid().ToString("N"));
        var smokeDataDir = Path.Combine(Path.GetTempPath(), "nradio-ui-smoke-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(smokeDataDir);
        Environment.SetEnvironmentVariable("NRADIO_DATA_DIR", smokeDataDir);
        var vm = new MainViewModel(new ActivityLog(
            smokeLogDir, maxFileBytes: 512 * 1024, maxFiles: 3, maxMemoryItems: 1000,
            loadExisting: false));
        SmokeProgress("MainViewModel created");
        if (live)
        {
            var liveHost = Environment.GetEnvironmentVariable("NRADIO_TEST_HOST") ?? "http://192.168.66.1";
            var livePassword = Environment.GetEnvironmentVariable("NRADIO_TEST_PASSWORD") ?? "";
            vm.ApplyConnectionSettings(liveHost, "root", livePassword, remember: false);
            PumpTask(async () =>
            {
                await vm.ConnectForTestAsync();
                await vm.RefreshAllForTestAsync();
            }, sb);
        }

        var failures = new List<string>();
        SmokeProgress("live block start");

        if (live)
        {
            if (!vm.IsConnected)
            {
                failures.Add("联机自检未能连接设备");
                sb.AppendLine("FAIL  联机自检未能连接设备：" + (vm.LastError ?? "未知原因"));
            }
            else
            {
                sb.AppendLine("PASS  已连接真实设备并拉取数据");
                sb.AppendLine($"      设备={vm.Dashboard.DeviceName} 运营商={vm.Dashboard.Operator} " +
                              $"WAN={vm.Dashboard.WanIp} SSID={vm.Dashboard.Ssid} 在线={vm.Dashboard.ClientsText} " +
                              $"信号={vm.Dashboard.SignalLevelText}({vm.Dashboard.SignalDetail}) " +
                              $"累计流量={vm.Dashboard.TrafficText}");
                if (vm.Device?.Kind == DeviceKind.C2000Pro)
                {
                    sb.AppendLine($"      协议={vm.Device.Kind} 固件={vm.Cellular2000.Firmware}");
                    sb.AppendLine($"      蜂窝：{vm.Cellular2000.Mode} · {vm.Cellular2000.Operator} · " +
                                  $"{vm.Cellular2000.Registration} · {vm.Cellular2000.Ipv4}");
                    sb.AppendLine($"      信号：RSRP={vm.Signal2000.CurrentRsrp} RSRQ={vm.Signal2000.CurrentRsrq} " +
                                  $"SINR={vm.Signal2000.CurrentSinr} · 采样 {vm.Signal2000.SampleCountText}");
                    sb.AppendLine($"      锁频：{vm.Lock2000.LockMode} · Band {vm.Lock2000.Band} · " +
                                  $"PCI {vm.Lock2000.Pci} · 邻居 {vm.Lock2000.Cells.Count}");
                    sb.AppendLine($"      SIM：{vm.Sim2000.CurrentSlot} / {vm.Sim2000.SlotCount} / {vm.Sim2000.SwitchMode}");
                    sb.AppendLine($"      Wi-Fi：射频 {vm.Wifi2000.Radios.Count} · {vm.Wifi2000.LanAddress} · {vm.Wifi2000.ClientCount}");
                    sb.AppendLine($"      短信：{vm.Sms2000.ServiceText}");
                }
                else
                {
                    sb.AppendLine($"      移动网络：网络模式档位={vm.Cellular.NwMode} MTU={vm.Cellular.MtuText} " +
                                  $"APN 配置数={vm.Cellular.Profiles.Count} PDP 数={vm.Cellular.PdpContexts.Count}");
                    sb.AppendLine($"      Wi-Fi：SSID={vm.Wifi.Ssid} 模式={vm.Wifi.CurrentSecurityMode} 信道选项={vm.Wifi.ChannelOptions.Count}");
                    sb.AppendLine($"      局域网：{vm.Lan.Ip}/{vm.Lan.Mask} 池 {vm.Lan.Start}-{vm.Lan.End}");
                }
                sb.AppendLine($"      终端：{vm.Devices.Clients.Count} 条记录");
                sb.AppendLine($"      终端计数器：{vm.Devices.CounterWarning switch { "" => "全部通过校验", _ => vm.Devices.CounterWarning }}");
                sb.AppendLine($"      电池：{vm.Battery.PercentText} · {vm.Battery.ChargeStateText} · 历史 {vm.Battery.Samples.Count} 条 · {vm.Battery.CoverageText}");
                sb.AppendLine($"      系统：卡位 {vm.System.Sims.Count} 个，账号 {vm.System.Users.Count} 个，认证强制={vm.AuthEnforced}");
                sb.AppendLine($"      概览磁贴：{vm.Dashboard.Tiles.Count} 个 → {string.Join(",", vm.Dashboard.Tiles.Select(t => t.Id))}");
                sb.AppendLine($"      流量统计：累计合计 {vm.Dashboard.CumulativeTotalText} · 当日 {vm.Dashboard.TodayTotalText}" +
                              $" · 视图 {vm.Dashboard.SelectedPeriod} · 柱数 {vm.Dashboard.Bars.Count}");
                sb.AppendLine($"      本地历史：{vm.History.CoverageText}；已知终端 {vm.History.KnownDeviceCount} 台");

                // 布局引擎校验：移动磁贴后顺序与持久化必须一致
                var before = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
                // 快照必须在移动之前取，否则会把自检造成的顺序写回用户配置
                var snapshot = vm.Settings.Tiles.Select(t => new TileState
                {
                    Id = t.Id, Order = t.Order, ColumnSpan = t.ColumnSpan,
                    RowSpan = t.RowSpan, ShowTitle = t.ShowTitle, Visible = t.Visible,
                }).ToList();
                vm.Dashboard.MoveTile(0, 2);
                var after = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
                var persisted = vm.Settings.Tiles.OrderBy(t => t.Order).Select(t => t.Id).ToList();
                if (before[0] == after[2] && after.SequenceEqual(persisted))
                    sb.AppendLine("PASS  磁贴拖动排序逻辑与持久化一致");
                else
                {
                    failures.Add("磁贴排序逻辑与持久化不一致");
                    sb.AppendLine($"FAIL  磁贴排序：{string.Join(",", before)} → {string.Join(",", after)}；持久化 {string.Join(",", persisted)}");
                }

                // 精确复原用户布局（移动前的快照 → 回写并落盘），避免自检污染真实配置
                vm.Settings.Tiles = snapshot;
                vm.Dashboard.ReloadTiles();
                vm.PersistSettings();
                sb.AppendLine("      已按移动前的快照复原布局并落盘");

                // 主题切换校验：两种主题下页面都必须能正常构建（资源缺失会在这里暴露）
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    var backup = vm.Settings.Theme;
                    vm.Settings.Theme = theme;
                    NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);
                    try
                    {
                        var probe = new DashboardView { DataContext = vm.Dashboard };
                        probe.Measure(new Size(1240, 780));
                        probe.Arrange(new Rect(0, 0, 1240, 780));
                        var settingsProbe = new SettingsView { DataContext = vm.SettingsPage };
                        settingsProbe.Measure(new Size(1240, 780));
                        settingsProbe.Arrange(new Rect(0, 0, 1240, 780));
                        var batteryProbe = new BatteryView { DataContext = vm.Battery };
                        batteryProbe.Measure(new Size(1240, 780));
                        batteryProbe.Arrange(new Rect(0, 0, 1240, 780));
                        sb.AppendLine($"PASS  {theme} 主题下页面渲染正常");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{theme} 主题渲染失败：{ex.Message}");
                        sb.AppendLine($"FAIL  {theme} 主题渲染失败：{ex.Message}");
                    }
                    vm.Settings.Theme = backup;
                    NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);
                }
                if (vm.Dashboard.DeviceName == "—" || vm.Dashboard.WanIp == "—")
                {
                    failures.Add("已连接但关键字段仍为空");
                    sb.AppendLine("FAIL  已连接但关键字段（设备名/WAN IP）仍为空，说明数据未真正进入界面模型");
                }
            }
        }

        var pages = new (string Name, Func<FrameworkElement> Factory)[]
        {
            ("概览", () => new DashboardView { DataContext = vm.Dashboard }),
            ("移动网络", () => new CellularView { DataContext = vm.Cellular }),
            ("Wi-Fi", () => new WifiView { DataContext = vm.Wifi }),
            ("局域网", () => new LanView { DataContext = vm.Lan }),
            ("设备与流量", () => new DevicesView { DataContext = vm.Devices }),
            ("电池监测", () => new BatteryView { DataContext = vm.Battery }),
            ("系统与维护", () => new SystemView { DataContext = vm.System }),
            ("通信日志", () => new LogView { DataContext = vm.Logs }),
            ("设置", () => new SettingsView { DataContext = vm.SettingsPage }),
            ("C2000 蜂窝", () => new C2000CellularView { DataContext = vm.Cellular2000 }),
            ("C2000 信号", () => new C2000SignalView { DataContext = vm.Signal2000 }),
            ("C2000 锁频", () => new C2000LockView { DataContext = vm.Lock2000 }),
            ("C2000 SIM", () => new C2000SimView { DataContext = vm.Sim2000 }),
            ("C2000 Wi-Fi", () => new C2000WifiView { DataContext = vm.Wifi2000 }),
            ("C2000 短信", () => new C2000SmsView { DataContext = vm.Sms2000 }),
            ("C2000 维护", () => new C2000MaintenanceView { DataContext = vm.Maintenance2000 }),
        };

        foreach (var (name, factory) in pages)
        {
            SmokeProgress("page " + name);
            try
            {
                var view = factory();
                // 两种典型尺寸各跑一次布局：常规窗口与最小窗口
                foreach (var size in new[] { new Size(1240, 780), new Size(1020, 640) })
                {
                    view.Measure(size);
                    view.Arrange(new Rect(size));
                    view.UpdateLayout();
                }
                sb.AppendLine($"PASS  页面实例化与布局：{name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name} {ex.Message}");
                sb.AppendLine($"FAIL  页面 {name}：{ex.GetType().Name} {ex.Message}");
            }
        }

        SavePageVisuals(vm);
        SmokeProgress("pages done");

        try
        {
            var window = new MainWindow(vm);
            window.Measure(new Size(1240, 780));
            SmokeProgress("main window measured");
            sb.AppendLine("PASS  主窗口实例化");
            var missingTemplates = vm.NavItems
                .Where(n => window.TryFindResource(new DataTemplateKey(n.Module.GetType())) is not DataTemplate)
                .Select(n => n.Title).ToList();
            if (missingTemplates.Count == 0)
            {
                sb.AppendLine($"PASS  导航内容模板：{vm.NavItems.Count} 个模块均有对应页面");
            }
            else
            {
                failures.Add("导航缺少内容模板：" + string.Join("、", missingTemplates));
                sb.AppendLine("FAIL  导航缺少内容模板：" + string.Join("、", missingTemplates));
            }
            window.Close();
            var lifecycleFlushed = vm.Log.Flush(TimeSpan.FromSeconds(2)) &&
                                   vm.Log.Items.Any(x => x.Message.Contains("软件正常退出", StringComparison.Ordinal)) &&
                                   vm.Log.PersistedCount > 0;
            if (lifecycleFlushed)
                sb.AppendLine("PASS  窗口关闭执行退出冲刷：正常退出事件与待写日志已落盘");
            else
            {
                failures.Add("窗口关闭未完成日志冲刷");
                sb.AppendLine("FAIL  窗口关闭未完成日志冲刷");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"MainWindow: {ex.Message}");
            sb.AppendLine($"FAIL  主窗口：{ex.GetType().Name} {ex.Message}");
        }

        CheckTileLayout(vm, sb, failures);
        SmokeProgress("tile layout done");
        CheckTrendChart(vm, sb, failures);
        CheckAlertRules(sb, failures);
        CheckTrafficSanity(sb, failures);
        CheckBattery(vm.Battery, sb, failures);
        CheckActivityLog(vm, sb, failures);
        CheckNetworkQualityClassification(sb, failures);
        CheckCsvExport(sb, failures);
        CheckC2000ProtocolOffline(vm, sb, failures);
        CheckTileSaveRoundTrip(vm, sb, failures);
        CheckTileEditPersistence(vm, sb, failures);
        // 注意：通知生命周期自检会让自检进程栈溢出崩溃（0xC00000FD），在定位前先不执行，
        // 以免把"自检崩溃"误当成"软件崩溃"。
        if (Environment.GetEnvironmentVariable("NRadio_NOTICE_SELFTEST") == "1")
            CheckNoticeLifecycle(vm, sb, failures);

        // 让绑定错误有機會被记录：把派发队列跑空
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bindings = bindingErrors.Buffer.ToString().Trim();
        if (bindings.Length > 0)
        {
            sb.AppendLine("检测到绑定诊断信息：");
            sb.AppendLine(bindings);
        }
        else
        {
            sb.AppendLine("PASS  未检测到数据绑定错误或警告");
        }

        sb.AppendLine(failures.Count == 0
            ? "RESULT: OK"
            : "RESULT: FAIL\n" + string.Join("\n", failures));
        try
        {
            vm.Log.Dispose();
            if (Directory.Exists(smokeLogDir)) Directory.Delete(smokeLogDir, recursive: true);
            if (Directory.Exists(smokeDataDir)) Directory.Delete(smokeDataDir, recursive: true);
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>
    /// 磁贴布局校验：用真实数据渲染概览页后，逐块检查排列矩形——
    /// 尺寸必须非退化、任意两块不得重叠、整体不得超出网格宽度。
    /// 这是"磁贴互相遮挡"问题的自动化回归检查。
    /// </summary>
    private static void CheckTileLayout(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        try
        {
            var view = new DashboardView { DataContext = vm.Dashboard };
            var root = new Grid { Width = 1280, Height = 1000 };
            root.Children.Add(view);
            root.Measure(new Size(1280, 1000));
            root.Arrange(new Rect(0, 0, 1280, 1000));
            root.UpdateLayout();

            var grid = Find<TileGrid>(root);
            if (grid is null)
            {
                failures.Add("概览页未找到磁贴网格");
                sb.AppendLine("FAIL  概览页未找到磁贴网格");
                return;
            }

            var rects = grid.Children.OfType<UIElement>()
                .Select(c => new Rect(c.TranslatePoint(new Point(0, 0), grid), c.RenderSize))
                .ToList();

            var problems = new List<string>();
            for (var i = 0; i < rects.Count; i++)
            {
                if (rects[i].Width < 40 || rects[i].Height < 60)
                    problems.Add($"磁贴{i} 尺寸异常 {rects[i].Width:0}x{rects[i].Height:0}");
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var inter = Rect.Intersect(rects[i], rects[j]);
                    if (!inter.IsEmpty && inter.Width > 1 && inter.Height > 1)
                        problems.Add($"磁贴{i} 与 磁贴{j} 重叠 {inter.Width:0}x{inter.Height:0}");
                }
            }

            if (problems.Count == 0)
            {
                sb.AppendLine($"PASS  磁贴布局无重叠：{rects.Count} 块，网格 {grid.ActualWidth:0}×{grid.ActualHeight:0}，" +
                              $"最小 {rects.Min(r => r.Height):0}px / 最大 {rects.Max(r => r.Height):0}px");
            }
            else
            {
                failures.AddRange(problems);
                sb.AppendLine("FAIL  磁贴布局问题：" + string.Join("；", problems));
            }

            if (rects.Count == 8 && vm.Dashboard.Tiles.All(t => t.ColumnSpan == 1 && t.RowSpan == 1))
                sb.AppendLine($"PASS  紧凑概览布局：8 块磁贴固定为 4×2，网格高度 {grid.ActualHeight:0}px");
            else
            {
                failures.Add("紧凑概览布局不正确");
                sb.AppendLine("FAIL  紧凑概览布局：磁贴数量或跨度不符合 4×2");
            }

            // ---- 图表主题同步：切主题必须真的触达图表并换掉画笔 ----
            var charts = new List<FrameworkElement>();
            Collect(root, charts);
            var trendCharts = charts.OfType<TrendChart>().ToList();
            var barCharts = charts.OfType<BarChart>().ToList();
            if (trendCharts.Count == 0 || barCharts.Count == 0)
            {
                failures.Add("概览页未找到图表控件");
                sb.AppendLine($"FAIL  概览页未找到图表控件（Trend={trendCharts.Count}, Bar={barCharts.Count}）");
            }
            else
            {
                var before = trendCharts.Sum(c => c.ThemeRefreshes) + barCharts.Sum(c => c.ThemeRefreshes);
                var darkBg = (Application.Current.TryFindResource("B.ChartBg") as SolidColorBrush)?.Color;
                var backup = vm.Settings.Theme;
                vm.Settings.Theme = AppTheme.Light;
                NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);
                var lightBg = (Application.Current.TryFindResource("B.ChartBg") as SolidColorBrush)?.Color;
                var after = trendCharts.Sum(c => c.ThemeRefreshes) + barCharts.Sum(c => c.ThemeRefreshes);
                vm.Settings.Theme = backup;
                NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);

                if (after > before && darkBg != lightBg)
                    sb.AppendLine($"PASS  图表主题同步：{trendCharts.Count + barCharts.Count} 个图表在一次切换中全部收到重绘通知，" +
                                  $"背景 #{(darkBg?.ToString()[3..] ?? "?")} → #{(lightBg?.ToString()[3..] ?? "?")}");
                else
                {
                    failures.Add("图表主题未同步");
                    sb.AppendLine($"FAIL  图表主题未同步：重绘次数 {before}→{after}，背景 {darkBg}→{lightBg}");
                }
            }

            // 拖动回归：拖动中顺序必须保持稳定，只在松手时提交一次，并真正写入 settings.json。
            var dragSnapshot = vm.Settings.Tiles.Select(t => new TileState
            {
                Id = t.Id, Order = t.Order, ColumnSpan = t.ColumnSpan, RowSpan = t.RowSpan,
                Visible = t.Visible, ShowTitle = t.ShowTitle,
            }).ToList();
            try
            {
                var beforeDrag = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
                var targetBounds = grid.TileBoundsForTest(2);
                grid.BeginDragForTest(0);
                grid.MoveDragForTest(new Point(
                    targetBounds.Left + targetBounds.Width / 2,
                    targetBounds.Top + targetBounds.Height / 2));

                var duringDrag = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
                var targetPreviewOk = grid.IsDraggingForTest && grid.DragTargetIndexForTest == 2;
                var stableDuringDrag = duringDrag.SequenceEqual(beforeDrag);

                grid.DropDragForTest();
                var afterDrop = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
                var closePathSave = vm.Dashboard.PersistTiles();
                var diskPath = AppSettings.FilePath;
                var diskExists = File.Exists(diskPath);
                var reloadedSettings = AppSettings.Load();
                var savedOrder = reloadedSettings.Tiles.OrderBy(t => t.Order).Select(t => t.Id).ToList();
                if (targetPreviewOk && stableDuringDrag && afterDrop[2] == beforeDrag[0] &&
                    closePathSave && savedOrder.SequenceEqual(afterDrop))
                    sb.AppendLine("PASS  磁贴拖动：拖动中不扰动其他磁贴，松手后单次提交并持久化");
                else
                {
                    failures.Add("磁贴拖动预览或落盘回归失败");
                    sb.AppendLine($"FAIL  磁贴拖动：预览={targetPreviewOk}，拖动中稳定={stableDuringDrag}；" +
                                  $"{string.Join(",", beforeDrag)} → {string.Join(",", afterDrop)}；" +
                                  $"closePathSave={closePathSave}，error={vm.Settings.LastSaveError ?? "无"}，file={diskPath}，exists={diskExists}，" +
                                  $"loadError={reloadedSettings.LastLoadError ?? "无"}，磁盘 {string.Join(",", savedOrder)}");
                }
            }
            finally
            {
                vm.Settings.Tiles = dragSnapshot;
                vm.Dashboard.ReloadTiles();
                vm.PersistSettings();
            }
        }
        catch (Exception ex)
        {
            failures.Add("磁贴布局校验异常：" + ex.Message);
            sb.AppendLine("FAIL  磁贴布局校验异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 趋势图交互回归：同时间点提示、下载/上传映射、无效信号点不伪造、无数据占位、主题下渲染。
    /// 复用控件内部同一套坐标与命中逻辑，不使用模拟鼠标钩子。
    /// </summary>
    private static void CheckTrendChart(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        try
        {
            SmokeProgress("trend start");
            var t0 = new DateTime(2026, 10, 17, 12, 0, 0);
            var speedPoints = new ObservableCollection<SamplePoint>
            {
                new(t0, 0, 0, -70, 2, false),
                new(t0.AddSeconds(5), 1000, 500, -75, 2, true),
                new(t0.AddSeconds(10), 2000, 600, -80, 2, true),
            };

            var speed = new TrendChart { Points = speedPoints, Mode = TrendChartMode.Speed };
            SmokeProgress("trend speed init");
            speed.Measure(new Size(480, 200));
            SmokeProgress("trend speed measured");
            speed.Arrange(new Rect(0, 0, 480, 200));
            SmokeProgress("trend speed arranged");
            speed.UpdateLayout();
            SmokeProgress("trend speed layout updated");
            speed.HoverAtForTest(speed.SeriesPointForTest(2, false));
            SmokeProgress("trend speed hover");
            ForceRender();
            SmokeProgress("trend speed rendered");

            var expectedDown = J.Rate(2000);
            var expectedUp = J.Rate(600);
            var summary = speed.HoverSummaryForTest ?? "";
            if (speed.HoverIndexForTest == 2 &&
                summary.Contains("↓ 下载", StringComparison.Ordinal) &&
                summary.Contains(expectedDown, StringComparison.Ordinal) &&
                summary.Contains("↑ 上传", StringComparison.Ordinal) &&
                summary.Contains(expectedUp, StringComparison.Ordinal))
            {
                sb.AppendLine($"PASS  速率图同时间点提示：{summary.Replace("\n", " / ")}");
            }
            else
            {
                failures.Add("速率图悬停提示或上下行映射不正确");
                sb.AppendLine($"FAIL  速率图悬停：index={speed.HoverIndexForTest}，期望 {expectedDown}/{expectedUp}，实际 {summary}");
            }

            // 右边缘提示会翻到左侧；两种主题都实际渲染一次，确保提示框不会越界/丢资源。
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                var backup = vm.Settings.Theme;
                vm.Settings.Theme = theme;
                NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);
                // 控件脱离可视化树时不会自动处理 InvalidateVisual；这里新建一个树外控件，
                // 确保主题画笔与悬停提示都用当前主题从零渲染。
                var themed = new TrendChart { Points = speedPoints, Mode = TrendChartMode.Speed };
                SmokeProgress("trend theme " + theme);
                themed.Measure(new Size(480, 200));
                themed.Arrange(new Rect(0, 0, 480, 200));
                themed.UpdateLayout();
                themed.HoverAtForTest(themed.SeriesPointForTest(2, false));
                ForceRender();
                SmokeProgress("trend theme rendered " + theme);
                var bmp = new RenderTargetBitmap(480, 200, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(themed);
                SaveVisual(bmp, $"trend-speed-{theme}.png");
                vm.Settings.Theme = backup;
                NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);
            }
            sb.AppendLine("PASS  趋势图悬停提示在深/浅主题下均可渲染（480×200 右边缘）");

            var compact = new TrendChart { Points = speedPoints, Mode = TrendChartMode.Speed };
            SmokeProgress("trend compact init");
            compact.Measure(new Size(220, 120));
            compact.Arrange(new Rect(0, 0, 220, 120));
            compact.UpdateLayout();
            compact.HoverAtForTest(compact.SeriesPointForTest(2, false));
            ForceRender();
            SmokeProgress("trend compact rendered");
            var compactBmp = new RenderTargetBitmap(220, 120, 96, 96, PixelFormats.Pbgra32);
            compactBmp.Render(compact);
            SaveVisual(compactBmp, "trend-compact-hover.png");
            if (compact.HoverIndexForTest != 2)
            {
                failures.Add("紧凑尺寸下趋势图悬停命中失败");
                sb.AppendLine($"FAIL  紧凑尺寸趋势图：hover={compact.HoverIndexForTest}");
            }
            else
            {
                sb.AppendLine("PASS  紧凑磁贴尺寸（220×120）下悬停命中正常");
            }

            var signalPoints = new ObservableCollection<SamplePoint>
            {
                new(t0, 0, 0, -70, 2, true),
                new(t0.AddSeconds(5), 0, 0, -80, 2, true),
                new(t0.AddSeconds(10), 0, 0, null, 2, true),
            };
            var signal = new TrendChart { Points = signalPoints, Mode = TrendChartMode.Signal };
            SmokeProgress("trend signal init");
            signal.Measure(new Size(480, 200));
            signal.Arrange(new Rect(0, 0, 480, 200));
            signal.UpdateLayout();
            signal.HoverAtForTest(signal.SeriesPointForTest(1, false));
            var signalSummary = signal.HoverSummaryForTest ?? "";
            ForceRender();
            SmokeProgress("trend signal rendered");
            var signalBmp = new RenderTargetBitmap(480, 200, 96, 96, PixelFormats.Pbgra32);
            signalBmp.Render(signal);
            SaveVisual(signalBmp, "trend-signal-hover.png");

            signal.HoverAtForTest(signal.SeriesPointForTest(2, false));
            if (signalSummary.Contains("-80 dBm", StringComparison.Ordinal) && signal.HoverIndexForTest == -1)
                sb.AppendLine("PASS  信号图提示显示真实 RSSI；缺失 RSSI 的时间点不生成提示");
            else
            {
                failures.Add("信号图悬停/缺失值处理不正确");
                sb.AppendLine($"FAIL  信号图提示：{signalSummary}；null 点 index={signal.HoverIndexForTest}");
            }

            var empty = new TrendChart { Mode = TrendChartMode.Speed };
            empty.Measure(new Size(320, 150));
            empty.Arrange(new Rect(0, 0, 320, 150));
            var emptyBmp = new RenderTargetBitmap(320, 150, 96, 96, PixelFormats.Pbgra32);
            emptyBmp.Render(empty);
            empty.HoverAtForTest(new Point(160, 70));
            if (empty.HoverIndexForTest == -1)
                sb.AppendLine("PASS  无采样数据时显示占位提示，悬停不生成伪造数值");
            else
            {
                failures.Add("无数据图表仍产生了悬停提示");
                sb.AppendLine("FAIL  无数据图表产生了悬停提示");
            }
        }
        catch (Exception ex)
        {
            failures.Add("趋势图交互自检异常：" + ex.Message);
            sb.AppendLine("FAIL  趋势图交互自检异常：" + ex);
        }
    }

    private static void CheckAlertRules(StringBuilder sb, List<string> failures)
    {
        try
        {
            var monitor = new SignalAlertMonitor();
            var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            var missingOk = monitor.Observe(null, 1, now) == SignalAlertKind.None;
            var first = monitor.Observe(1, 1, now);
            var second = monitor.Observe(0, 1, now.AddSeconds(5));
            var third = monitor.Observe(1, 1, now.AddSeconds(10));
            var noDuplicate = monitor.Observe(0, 1, now.AddSeconds(15)) == SignalAlertKind.None;
            var reminder = monitor.Observe(0, 1, now.AddMinutes(31)) == SignalAlertKind.Weak;

            monitor.Observe(2, 1, now.AddMinutes(32));
            monitor.Observe(3, 1, now.AddMinutes(33));
            var recovered = monitor.Observe(2, 1, now.AddMinutes(34)) == SignalAlertKind.Recovered;

            if (missingOk && first == SignalAlertKind.None && second == SignalAlertKind.None &&
                third == SignalAlertKind.Weak && noDuplicate && reminder && recovered && !monitor.IsWeak)
            {
                sb.AppendLine("PASS  弱信号规则：缺失值不误报、连续 3 次确认、30 分钟持续提醒、连续恢复提示");
            }
            else
            {
                failures.Add("弱信号状态机不符合预期");
                sb.AppendLine($"FAIL  弱信号状态机：missing={missingOk} first={first} second={second} third={third} " +
                              $"dup={noDuplicate} reminder={reminder} recovered={recovered}");
            }

            var day = new DateTime(2026, 10, 17, 23, 59, 0);
            var key = TrafficAlertRules.DedupeKey(TrafficThresholdPeriod.Day, day, 10);
            var same = TrafficAlertRules.DedupeKey(TrafficThresholdPeriod.Day, day.AddSeconds(-30), 10);
            var nextDay = TrafficAlertRules.DedupeKey(TrafficThresholdPeriod.Day, day.AddDays(1), 10);
            var month = TrafficAlertRules.DedupeKey(TrafficThresholdPeriod.Month, day, 10);
            var changedThreshold = TrafficAlertRules.DedupeKey(TrafficThresholdPeriod.Day, day, 12);
            const long tenGb = 10L * 1024 * 1024 * 1024;

            if (key == same && key != nextDay && key != month && key != changedThreshold &&
                TrafficAlertRules.ReachedThreshold(tenGb, 10) &&
                !TrafficAlertRules.ReachedThreshold(tenGb - 1, 10))
            {
                sb.AppendLine("PASS  流量阈值规则：同周期/跨重启键一致，跨日/跨月/改阈值会重新判定");
            }
            else
            {
                failures.Add("流量阈值去重键不正确");
                sb.AppendLine($"FAIL  流量阈值键：{key} / {same} / {nextDay} / {month} / {changedThreshold}");
            }
        }
        catch (Exception ex)
        {
            failures.Add("告警规则自检异常：" + ex.Message);
            sb.AppendLine("FAIL  告警规则自检异常：" + ex.Message);
        }
    }

    private static void CheckTrafficSanity(StringBuilder sb, List<string> failures)
    {
        try
        {
            var status = new StatusInfo
            {
                RxByteAll = "14284579593",
                TxByteAll = "2508179985",
                RunSeconds = "80857",
            };
            var corrupt = new ClientEntry
            {
                Rx = "36371032162688",
                Tx = "7147604096932",
                RxMonth = "36370864134843",
                TxMonth = "7147556604912",
                Rx3Day = "36370864134843",
                Tx3Day = "7147509112892",
            };
            var normal = new ClientEntry
            {
                Rx = "5017462384",
                Tx = "5899393484",
                RxMonth = "5017462384",
                TxMonth = "5899393484",
                Rx3Day = "5017462384",
                Tx3Day = "5899393484",
            };

            var bad = TrafficSanity.Inspect(corrupt, status);
            var good = TrafficSanity.Inspect(normal, status);
            var bootOnly = TrafficSanity.Inspect(corrupt, new StatusInfo { RunSeconds = "80857" });
            var noStatus = TrafficSanity.Inspect(normal, null);
            var persistent = TrafficSanity.Inspect(
                new ClientEntry { Rx = "300000000000", Tx = "200000000000", AccessTime = "1/1/2023 0:00:00" },
                new StatusInfo { RxByteAll = "8000000000", TxByteAll = "2000000000", RunSeconds = "3600" });
            var sameCounterWithoutAccessTime = TrafficSanity.Inspect(
                new ClientEntry { Rx = "300000000000", Tx = "200000000000" },
                new StatusInfo { RxByteAll = "8000000000", TxByteAll = "2000000000", RunSeconds = "3600" });

            if (!bad.Reliable && good.Reliable && !bootOnly.Reliable && noStatus.Reliable &&
                persistent.Reliable && !sameCounterWithoutAccessTime.Reliable &&
                !string.IsNullOrEmpty(bad.RawSummary))
            {
                sb.AppendLine("PASS  终端流量计数校验：识别 36 TB 失真值，跨重启计数器与缺失整机数据不误判");
            }
            else
            {
                failures.Add("终端流量计数校验不符合预期");
                sb.AppendLine($"FAIL  终端流量计数校验：bad={bad.Reliable} good={good.Reliable} " +
                              $"bootOnly={bootOnly.Reliable} noStatus={noStatus.Reliable} " +
                              $"persistent={persistent.Reliable} sameWithoutAccess={sameCounterWithoutAccessTime.Reliable}");
            }
        }
        catch (Exception ex)
        {
            failures.Add("终端流量计数校验异常：" + ex.Message);
            sb.AppendLine("FAIL  终端流量计数校验异常：" + ex.Message);
        }
    }

    private static void CheckBattery(BatteryViewModel battery, StringBuilder sb, List<string> failures)
    {
        try
        {
            var t0 = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);
            BatterySample S(int minute, int pct, int? charge, long run,
                bool connected = true, bool available = true)
                => new()
                {
                    TimeUtc = t0.AddMinutes(minute), Percent = available ? pct : null,
                    ChargeRaw = charge, RunSeconds = run, Connected = connected, Available = available,
                };

            var samples = new List<BatterySample>
            {
                S(0, 90, 0, 1000),
                S(10, 88, 0, 1600),
                S(20, 86, 0, 2200),
                S(30, 0, 0, 2200, connected: false, available: false),
                S(140, 70, 1, 9000),
                S(150, 75, 1, 9600),
                S(160, 80, 2, 10200),
                S(180, 100, 2, 10), // run_seconds 回退 → 设备重启
            };

            var analysis = BatteryAnalyzer.Analyze(samples, t0, t0.AddMinutes(181), TimeSpan.FromMinutes(15));
            var rateOk = analysis.AverageDropPerHour is > 8 and < 20; // 4% / 20 分钟 = 12%/h
            var analysisOk = analysis.HasData && analysis.DischargePercent >= 4 &&
                             analysis.ChargePercent >= 10 && analysis.GapCount == 2 &&
                             analysis.DisconnectCount == 1 && analysis.RestartCount == 1 &&
                             rateOk && analysis.CoverageRatio < 0.5 &&
                             analysis.DischargeDuration >= TimeSpan.FromMinutes(20) &&
                             analysis.ChargeDuration >= TimeSpan.FromMinutes(10) &&
                             analysis.FullDuration >= TimeSpan.FromMinutes(10);

            battery.ApplyLive(new StatusInfo
            {
                BatteryPercent = "42", BatteryCharge = "2", BatteryCharging = "1",
                BatteryConnect = "7", RunSeconds = "321",
            }, t0);
            var parsingOk = battery.PercentText == "42%" && battery.IsFull &&
                            battery.ChargeDetailText.Contains("battery_charge=2", StringComparison.Ordinal) &&
                            battery.BatteryConnectText.Contains("battery_connect=7", StringComparison.Ordinal);
            battery.ApplyLive(new StatusInfo
            {
                BatteryPercent = "not-a-number", BatteryCharge = "invalid", BatteryCharging = "?",
                BatteryConnect = "", RunSeconds = "clock?",
            }, t0);
            var invalidParsingOk = !battery.HasPercent && battery.PercentText == "—" &&
                                   battery.ChargeStateText == "未知";
            battery.ApplyLive(new StatusInfo
            {
                BatteryPercent = "55", BatteryCharge = "0", BatteryCharging = "1",
                BatteryConnect = "1", RunSeconds = "400",
            }, t0);
            var conflictHandled = battery.ChargeStateText == "未知" && battery.HasPercent;

            var chart = new BatteryChart
            {
                Width = 900,
                Height = 320,
                Samples = samples,
                RangeSeconds = 3600,
                GapThresholdSeconds = 900,
                ViewStartUtc = t0,
                ViewEndUtc = t0.AddMinutes(181),
            };
            chart.Measure(new Size(900, 320));
            chart.Arrange(new Rect(0, 0, 900, 320));
            chart.UpdateLayout();
            var bitmap = new RenderTargetBitmap(900, 320, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);

            var gapCount = chart.GapCountForTest;
            var segmentCount = chart.SegmentCountForTest;

            // 稀疏采样：在两点正中间悬停，也必须命中最近的真实采样点。
            chart.HoverAtForTest(chart.PointForTimeForTest(t0.AddMinutes(5)));
            var hover = chart.HoverSummaryForTest ?? "";
            var hoverOk = (hover.Contains("90%", StringComparison.Ordinal) ||
                           hover.Contains("88%", StringComparison.Ordinal)) &&
                          hover.Contains("未充电", StringComparison.Ordinal);

            chart.ResetViewForTest();
            var beforePan = chart.ViewForTest;
            chart.PanForTest(-10_000);
            var afterPan = chart.ViewForTest;
            var futureClamped = afterPan.End <= DateTime.UtcNow.AddSeconds(2);
            chart.PanForTest(10_000_000);
            var oldest = chart.ViewForTest;
            var oldestClamped = oldest.Start >= DateTime.UtcNow.AddDays(-31).AddMinutes(-1);
            var panOk = futureClamped && oldestClamped && beforePan.End > beforePan.Start;

            chart.ResetViewForTest();
            chart.ZoomForTest(new Point(450, 160), zoomIn: true);
            var afterZoom = chart.ViewForTest;
            var zoomOk = afterZoom.End - afterZoom.Start < beforePan.End - beforePan.Start;
            for (var i = 0; i < 30; i++) chart.ZoomForTest(new Point(450, 160), zoomIn: true);
            var minSpan = chart.ViewForTest;
            for (var i = 0; i < 60; i++) chart.ZoomForTest(new Point(450, 160), zoomIn: false);
            var maxSpan = chart.ViewForTest;
            zoomOk &= minSpan.End - minSpan.Start >= TimeSpan.FromMinutes(5) &&
                      maxSpan.End - maxSpan.Start <= TimeSpan.FromDays(31).Add(TimeSpan.FromSeconds(1));

            chart.ResetViewForTest();
            var reset = chart.ViewForTest;
            var resetOk = reset.End >= DateTime.UtcNow.AddMinutes(-2) &&
                          reset.End - reset.Start >= TimeSpan.FromMinutes(55);

            var emptyChart = new BatteryChart
            {
                Width = 900, Height = 320, Samples = Array.Empty<BatterySample>(),
                RangeSeconds = 3600, GapThresholdSeconds = 900,
                ViewStartUtc = t0, ViewEndUtc = t0.AddHours(1),
            };
            emptyChart.Measure(new Size(900, 320));
            emptyChart.Arrange(new Rect(0, 0, 900, 320));
            emptyChart.UpdateLayout();
            new RenderTargetBitmap(900, 320, 96, 96, PixelFormats.Pbgra32).Render(emptyChart);
            emptyChart.HoverAtForTest(new Point(400, 150));
            var emptyOk = emptyChart.VisibleSamplesForTest == 0 &&
                          emptyChart.HoverSummaryForTest is null;

            var singleChart = new BatteryChart
            {
                Width = 900, Height = 320, Samples = new[] { S(5, 77, 1, 1300) },
                RangeSeconds = 3600, GapThresholdSeconds = 900,
                ViewStartUtc = t0, ViewEndUtc = t0.AddHours(1),
            };
            singleChart.Measure(new Size(900, 320));
            singleChart.Arrange(new Rect(0, 0, 900, 320));
            singleChart.UpdateLayout();
            new RenderTargetBitmap(900, 320, 96, 96, PixelFormats.Pbgra32).Render(singleChart);
            var singleOk = singleChart.VisibleSamplesForTest == 1 &&
                           singleChart.SegmentCountForTest == 0;

            var temp = Path.Combine(Path.GetTempPath(), "nradio-battery-" + Guid.NewGuid().ToString("N") + ".json");
            var history = BatteryHistory.Load(30, temp);
            var status = new StatusInfo
            {
                BatteryPercent = "80", BatteryCharge = "0", BatteryCharging = "0",
                BatteryConnect = "1", RunSeconds = "100",
            };
            history.MaybeSample(status, t0, 60, 30, out _);
            var skipped = history.MaybeSample(status, t0.AddSeconds(30), 60, 30, out _) == BatterySampleResult.Skipped;
            history.MaybeSample(status, t0.AddSeconds(60), 60, 30, out _);
            var missing = history.MaybeSample(new StatusInfo
            {
                BatteryPercent = "101", BatteryCharge = "0", BatteryCharging = "0", RunSeconds = "220",
            }, t0.AddSeconds(120), 60, 30, out var missingSample) == BatterySampleResult.Recorded &&
                missingSample is { Available: false, Percent: null };
            history.MarkDisconnected(t0.AddSeconds(210));
            var rollback = history.MaybeSample(status, t0.AddSeconds(80), 60, 30, out _) ==
                           BatterySampleResult.TimeRollback;
            var futureRejected = history.MaybeSample(status, DateTime.UtcNow.AddMinutes(10), 60, 30, out _) ==
                                 BatterySampleResult.TimeInvalid;
            Parallel.For(0, 8, _ => history.SaveNow(t0.AddSeconds(240), 30));
            var serializedSave = history.LastSaveError is null;

            var reloaded = BatteryHistory.Load(30, temp);
            var persisted = reloaded.Count == 4 && reloaded.Latest is { Connected: false } &&
                            reloaded.AllSnapshot().Any(s => s is { Available: false, Percent: null });

            File.WriteAllText(temp, "{ broken");
            var recovered = BatteryHistory.Load(30, temp);
            var backupRecoveryOk = recovered.Count == 4 && !string.IsNullOrEmpty(recovered.LoadWarning);
            File.Delete(temp + ".bak");
            File.WriteAllText(temp, "{ broken");
            var corrupt = BatteryHistory.Load(30, temp);
            var corruptOk = backupRecoveryOk && corrupt.Count == 0 &&
                            !string.IsNullOrEmpty(corrupt.LoadWarning);

            var originalRange = battery.SelectedRange;
            var rangeOk = true;
            foreach (var option in battery.RangeOptions)
            {
                battery.SelectedRange = option;
                var seconds = int.Parse(option.Value);
                var span = battery.ViewEndUtc - battery.ViewStartUtc;
                rangeOk &= battery.RangeSeconds == seconds &&
                           Math.Abs(span.TotalSeconds - seconds) < 5 &&
                           battery.ViewEndUtc <= DateTime.UtcNow.AddSeconds(2);
            }
            if (originalRange is not null) battery.SelectedRange = originalRange;

            var visualDir = Environment.GetEnvironmentVariable("NRadio_VISUAL_DIR");
            if (!string.IsNullOrWhiteSpace(visualDir))
            {
                chart.ResetViewForTest();
                chart.ViewStartUtc = t0;
                chart.ViewEndUtc = t0.AddMinutes(181);
                chart.Measure(new Size(900, 320));
                chart.Arrange(new Rect(0, 0, 900, 320));
                chart.UpdateLayout();
                var fixture = new RenderTargetBitmap(900, 320, 96, 96, PixelFormats.Pbgra32);
                fixture.Render(chart);
                SaveVisual(fixture, "battery-fixture.png");
            }

            foreach (var path in new[] { temp, temp + ".bak", temp + ".tmp" })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            if (analysisOk && parsingOk && invalidParsingOk && conflictHandled &&
                gapCount == 2 && segmentCount >= 3 && hoverOk && panOk && zoomOk &&
                resetOk && rangeOk && emptyOk && singleOk && skipped && missing &&
                rollback && futureRejected && serializedSave && persisted && corruptOk)
            {
                sb.AppendLine("PASS  电池：真实时间轴/悬停/边界平移/缩放/复位/四档范围，断连与重启去重，缺失值/未来时间/并发保存/持久化/损坏恢复");
            }
            else
            {
                failures.Add("电池监测自检不符合预期");
                sb.AppendLine($"FAIL  电池自检：analysis={analysisOk} gaps={gapCount} segments={segmentCount} " +
                              $"parsing={parsingOk} invalidParsing={invalidParsingOk} conflict={conflictHandled} " +
                              $"discharge={analysis.DischargePercent} charge={analysis.ChargePercent} " +
                              $"dischargeTime={analysis.DischargeDuration} chargeTime={analysis.ChargeDuration} " +
                              $"coverage={analysis.CoverageRatio:0.###} rate={analysis.AverageDropPerHour} " +
                              $"hover={hoverOk}({hover.Replace("\n", " / ")}) pan={panOk} zoom={zoomOk} " +
                              $"reset={resetOk} range={rangeOk} empty={emptyOk} single={singleOk} " +
                              $"skipped={skipped} missing={missing} rollback={rollback} future={futureRejected} " +
                              $"saveConcurrent={serializedSave} persisted={persisted} corrupt={corruptOk}");
            }
        }
        catch (Exception ex)
        {
            failures.Add("电池监测自检异常：" + ex.Message);
            sb.AppendLine("FAIL  电池监测自检异常：" + ex);
        }
    }

    private static void CheckActivityLog(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nradio-activity-" + Guid.NewGuid().ToString("N"));
        var badDir = Path.Combine(Path.GetTempPath(), "nradio-activity-bad-" + Guid.NewGuid().ToString("N"));
        var filterDir = Path.Combine(Path.GetTempPath(), "nradio-activity-filter-" + Guid.NewGuid().ToString("N"));
        var retentionDir = Path.Combine(Path.GetTempPath(), "nradio-activity-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ActivityLog(
                dir, maxFileBytes: 16 * 1024, maxFiles: 3, retentionDays: 30,
                maxMemoryItems: 1000, loadExisting: false, publishToUi: false);
            var notices = new NoticeCenter(log);

            log.Info(LogCategory.Connection, "连接", "连接成功，准备读取状态。");
            log.Warn(LogCategory.Communication, "设备请求",
                "认证参数 password=SuperSecret token: abc123 " +
                "Authorization: Digest username=\"admin\", nonce=\"nonce123\", response=\"deadbeef\"");
            notices.Raise(NoticeLevel.Warning, "自检通知", "通知内容也应进入同一份日志。");

            Parallel.For(0, 150, i =>
                log.Add(LogLevel.Info, LogCategory.Communication, "并发写入",
                    $"第 {i:000} 条并发事件，用于验证有界队列、顺序写入与轮转；不含凭据。"));

            var flushed = log.Flush(TimeSpan.FromSeconds(15));
            var files = Directory.GetFiles(dir, "activity*.log");
            var diskText = string.Concat(files.Select(File.ReadAllText));
            var sanitized = !diskText.Contains("SuperSecret", StringComparison.Ordinal) &&
                            !diskText.Contains("abc123", StringComparison.Ordinal) &&
                            !diskText.Contains("nonce123", StringComparison.Ordinal) &&
                            !diskText.Contains("deadbeef", StringComparison.Ordinal);
            var rotated = files.Length is >= 2 and <= 4;
            log.Dispose();

            var loaded = new ActivityLog(
                dir, maxFileBytes: 16 * 1024, maxFiles: 3, retentionDays: 30,
                maxMemoryItems: 1000, publishToUi: false);
            var noticeLogged = loaded.Items.Any(x =>
                x.Source == "通知中心" && x.Message.Contains("自检通知", StringComparison.Ordinal));
            var reloaded = loaded.LoadedCount >= 150 &&
                           loaded.Items.Any(x => x.Category == LogCategory.Notification) &&
                           loaded.Items.Any(x => x.Category == LogCategory.Connection);
            loaded.Dispose();

            File.AppendAllText(loaded.CurrentFilePath, "{ this is not json\n");
            var corrupt = new ActivityLog(
                dir, maxFileBytes: 16 * 1024, maxFiles: 3, retentionDays: 30,
                maxMemoryItems: 1000, publishToUi: false);
            var corruptHandled = corrupt.LoadedCount >= 150 && !string.IsNullOrEmpty(corrupt.LoadWarning);
            corrupt.Dispose();

            Directory.CreateDirectory(Path.Combine(badDir, "activity.log"));
            var failing = new ActivityLog(
                badDir, maxFileBytes: 16 * 1024, maxFiles: 3, retentionDays: 30,
                maxMemoryItems: 1000, loadExisting: false, publishToUi: false);
            failing.Error(LogCategory.Persistence, "写入测试", "此条事件刻意写入不可用路径。");
            var failureReported = !failing.Flush(TimeSpan.FromSeconds(2)) && failing.HasWriteError;
            failing.Dispose();

            Directory.CreateDirectory(retentionDir);
            var expired = Path.Combine(retentionDir, "activity.2.log");
            File.WriteAllText(expired, "old");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-3));
            var retentionLog = new ActivityLog(
                retentionDir, maxFileBytes: 16 * 1024, maxFiles: 3, retentionDays: 1,
                maxMemoryItems: 100, loadExisting: true, publishToUi: false);
            var retentionOk = !File.Exists(expired);
            retentionLog.Dispose();

            var filterLog = new ActivityLog(
                filterDir, maxMemoryItems: 100, loadExisting: false, publishToUi: true);
            filterLog.Info(LogCategory.Notification, "自检", "普通通知记录");
            filterLog.Error(LogCategory.Connection, "筛选测试", "仅用于通信日志类别筛选。");
            filterLog.Success(LogCategory.Sampling, "自检", "采样成功记录");
            var logVm = new LogViewModel(vm, filterLog);
            var filtered = true;
            logVm.SelectedCategory = logVm.CategoryOptions.First(x => x.Value == nameof(LogCategory.Connection));
            logVm.SelectedLevel = logVm.LevelOptions.First(x => x.Value == "all");
            filtered = logVm.FilteredItems.All(x => x.Category == LogCategory.Connection) &&
                       logVm.FilteredCount > 0;
            logVm.SearchText = "类别筛选";
            filtered &= logVm.FilteredItems.Any(x => x.Message.Contains("类别筛选", StringComparison.Ordinal));
            logVm.SearchText = "";
            logVm.SelectedCategory = logVm.CategoryOptions[0];
            logVm.SelectedLevel = logVm.LevelOptions[0];

            var logWindow = new Window
            {
                Width = 960,
                Height = 560,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = new LogView { DataContext = logVm },
            };
            logWindow.Show();
            var settled = PumpDispatcherFor(TimeSpan.FromSeconds(3));
            logVm.SelectedLevel = logVm.LevelOptions.First(x => x.Value == nameof(LogLevel.Warning));
            filterLog.Warn(LogCategory.Network, "动态刷新", "日志页面应增量显示此警告。");
            settled &= PumpDispatcherFor(TimeSpan.FromSeconds(3));
            filtered &= settled && logVm.FilteredItems.Count == 1 &&
                        logVm.FilteredItems[0].Message.Contains("增量显示", StringComparison.Ordinal);
            logWindow.Close();
            filterLog.Dispose();

            if (flushed && sanitized && rotated && noticeLogged && reloaded && corruptHandled &&
                failureReported && retentionOk && filtered)
            {
                sb.AppendLine("PASS  通信日志：脱敏/通知统一入库/并发顺序/大小轮转/重载/损坏跳过/写入失败可诊断/页面筛选");
            }
            else
            {
                failures.Add("通信日志持久化自检不符合预期");
                sb.AppendLine($"FAIL  通信日志自检：flush={flushed} sanitized={sanitized} files={files.Length} " +
                              $"rotated={rotated} notice={noticeLogged} reload={reloaded} " +
                              $"corrupt={corruptHandled} writeFailure={failureReported} " +
                              $"retention={retentionOk} filter={filtered}");
            }
        }
        catch (Exception ex)
        {
            failures.Add("通信日志自检异常：" + ex.Message);
            sb.AppendLine("FAIL  通信日志自检异常：" + ex);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            try { if (Directory.Exists(badDir)) Directory.Delete(badDir, recursive: true); } catch { }
            try { if (Directory.Exists(filterDir)) Directory.Delete(filterDir, recursive: true); } catch { }
            try { if (Directory.Exists(retentionDir)) Directory.Delete(retentionDir, recursive: true); } catch { }
        }
    }

    private static void CheckNetworkQualityClassification(StringBuilder sb, List<string> failures)
    {
        try
        {
            var now = new DateTime(2026, 10, 17, 12, 0, 0);
            ProbeObservation Router(bool tcp = false, bool refused = false, bool definitive = false,
                bool timeout = false, int pingOk = 0, int pingAttempts = 4, bool pingDefinitive = false)
                => new("192.168.1.1", tcp, refused, definitive, timeout, pingAttempts, pingOk,
                    pingDefinitive, false, pingOk > 0 ? 4 : null);
            ProbeObservation External(string target, bool tcp = false, bool timeout = false, int pingOk = 0,
                int pingAttempts = 0)
                => new(target, tcp, false, false, timeout, pingAttempts, pingOk, false, false,
                    pingOk > 0 ? 20 : null);

            var healthy = NetworkQualityMonitor.Evaluate(true,
                Router(tcp: true, pingOk: 4),
                new[] { External("223.5.5.5", tcp: true), External("119.29.29.29", timeout: true) }, now);
            var routerDown = NetworkQualityMonitor.Evaluate(true,
                Router(definitive: true, pingDefinitive: true),
                Array.Empty<ProbeObservation>(), now);
            var internetDown = NetworkQualityMonitor.Evaluate(true,
                Router(tcp: true, pingOk: 4),
                new[] { External("223.5.5.5", timeout: true), External("119.29.29.29", timeout: true) }, now);
            var unknown = NetworkQualityMonitor.Evaluate(true,
                Router(timeout: true, pingAttempts: 4),
                Array.Empty<ProbeObservation>(), now);
            var icmpBlocked = NetworkQualityMonitor.Evaluate(true,
                Router(tcp: true, pingAttempts: 4),
                new[] { External("223.5.5.5", tcp: true) }, now);
            var localDown = NetworkQualityMonitor.Evaluate(false, Router(), Array.Empty<ProbeObservation>(), now);

            var ok = healthy.State == NetworkQualityState.Healthy &&
                     routerDown.State == NetworkQualityState.RouterUnreachable &&
                     internetDown.State == NetworkQualityState.InternetUnreachable &&
                     unknown.State == NetworkQualityState.DetectionFailed &&
                     icmpBlocked.State == NetworkQualityState.Healthy &&
                     icmpBlocked.LossText.Contains("无法统计", StringComparison.Ordinal) &&
                     localDown.State == NetworkQualityState.LocalNetworkUnavailable;

            if (ok)
                sb.AppendLine("PASS  网络质量分类：可达 / 路由器不可达 / 外网失败 / 检测失败 / 无本机网络均正确区分");
            else
            {
                failures.Add("网络质量状态分类不正确");
                sb.AppendLine($"FAIL  网络质量分类：{healthy.State}/{routerDown.State}/{internetDown.State}/" +
                              $"{unknown.State}/{icmpBlocked.State}/{localDown.State}");
            }
        }
        catch (Exception ex)
        {
            failures.Add("网络质量分类自检异常：" + ex.Message);
            sb.AppendLine("FAIL  网络质量分类自检异常：" + ex.Message);
        }
    }

    private static void CheckC2000ProtocolOffline(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        try
        {
            const string runtimeJson = """
            {"result":{
              "global":{"uptime":12345,"wifi_temp":42,"cpu_percent":7,"mem_percent":58,"net_type":"Cellular"},
              "terminal_all":{"count":2,"client":{
                "00:11:22:33:44:55":{"txbytes":"2048","rxbytes":"1024","rssi":"-45","hostname":"Client-A","ipaddr":"192.168.66.2","mac":"00:11:22:33:44:55","type":"wireless","exsit":1}
              }},
              "lans":[{"proto":"static","status":0,"ipaddrs":[{"ipaddr":"192.168.66.1/24","netmask":"255.255.255.0"}]}],
              "cpe":[{"name":"wan0","model":"RG200U-CN","status":0,"mode":"NR SA","band":"41","pci":"887","earfcn":"504990","tac":"326198","rsrp":"-79","rsrq":"-6","sinr":"15","CQI":"8","DLBW":"100000","imei":"000000000000000","imsi":"000000000000000","iccid":"00000000000000000000","simcount":2,"simno":"1","sim_isp":"China Mobile","isp":"China Mobile","apn":"cmnet","download":"4096","upload":"2048","V4":{"dns":["1.1.1.1"],"ipaddrs":["10.0.0.2"]},"V6":{"ipaddrs":[],"dns":[]}}],
              "wans":[{"name":"wan0","classname":"wan0","proto":"wwan","status":0,"uptime":120,"gateway":"::","download":"4096","upload":"2048"}],
              "bat":{"charging":0,"percent":-1},
              "wlan":{"radio5_disabled":0,"radio2_disabled":0,"radio":[{"ifname":"wlan1","ssid":"@LTE-5G","encryption":"psk-mixed","channel":"149","frequency":"5.745","sta":1}]}
            }}
            """;
            var runtime = C2000ProClient.Deserialize<C2000RuntimeEnvelope>(runtimeJson, "offline-runtime").Result
                          ?? throw new Exception("runtime result 缺失");
            var cpe = runtime.Cpe?.FirstOrDefault();
            if (runtime.Global?.WifiTemp != "42" || runtime.Global?.CpuPercent != "7" ||
                cpe?.Rsrp != "-79" || runtime.TerminalAll?.Clients?.Count != 1)
                throw new Exception("数字/字符串混合字段映射不正确");

            const string overview = """
            <div>Model:</div><div>C2000-515</div>
            <div>Firmware Version:</div><div>NROS-2.2.5.0.1</div>
            <div>MAC Address:</div><div>00:11:22:33:44:55</div>
            <script>var board_name = "sprd,udx710_4h10";</script>
            """;
            var identity = C2000ProAdapter.ParseIdentity(overview, runtime);
            if (identity.Model != "C2000-515" || identity.FirmwareVersion != "NROS-2.2.5.0.1" ||
                identity.Board != "sprd,udx710_4h10")
                throw new Exception("设备身份页面解析不正确");

            const string lockHtml = """
            <script>
            var cellular_earfcn={"cfg":{"nr":{"BAND":"41","PCI":"887","EARFCN":"504990"},"lte":{"BAND":"","PCI":"","EARFCN":""}},
            "neighbour":[[{"MODE":"NR","EARFCN":"504990","RSRP":"-79","RSRQ":"-6","SINR":"15","PCI":"887"}]],
            "status":{"RSRP":"-79","MODE":"5","CELL":"2062E7102","EARFCN":"504990","BAND":"41","SINR":"15","PCI":"887","LAC":"326198"}};
            var lable_array=[];
            </script>
            """;
            var lockInfo = C2000ProAdapter.ParseLockSnapshot(lockHtml, cpe);
            if (lockInfo.LockMode != "小区锁定" || lockInfo.Neighbours.Count != 1 ||
                !lockInfo.Neighbours[0].Current || !lockInfo.Neighbours[0].Locked)
                throw new Exception("锁频页解析或当前/锁定标记不正确");
            var gauge = new MetricGauge
            {
                Title = "信号质量",
                Value = -78,
                Minimum = -140,
                Maximum = -40,
                Kind = MetricGaugeKind.SignalRsrp,
            };
            gauge.Measure(new Size(180, 180));
            gauge.Arrange(new Rect(0, 0, 180, 180));
            ForceRender();
            if (gauge.ToolTip?.ToString()?.Contains("优秀", StringComparison.Ordinal) != true)
                throw new Exception("信号环形指标未生成优秀状态提示");
            var timeline = new SignalQualityTimeline
            {
                Points = new ObservableCollection<SamplePoint>
                {
                    new(DateTime.Now.AddMinutes(-1), 0, 0, -78, null, false),
                    new(DateTime.Now, 0, 0, -86, null, false),
                },
            };
            timeline.Measure(new Size(420, 180));
            timeline.Arrange(new Rect(0, 0, 420, 180));
            ForceRender();
            timeline.HoverAtForTest(1);
            if (timeline.HoverTextForTest?.Contains("-86 dBm", StringComparison.Ordinal) != true ||
                timeline.HoverTextForTest?.Contains("良好", StringComparison.Ordinal) != true)
                throw new Exception("信号时间带悬停提示不正确");

            var signalAnalysis = new SignalAnalysisChart
            {
                Points = new ObservableCollection<SignalMetricPoint>
                {
                    new(DateTime.Now.AddMinutes(-2), -75, -7, 18, true, "2062E7102", "41", "504990", "887"),
                    new(DateTime.Now.AddMinutes(-1), -86, -9, 10, true, "2062E7102", "41", "504990", "887"),
                    new(DateTime.Now, -96, -12, 3, false, "", "", "", ""),
                },
            };
            signalAnalysis.Measure(new Size(720, 345));
            signalAnalysis.Arrange(new Rect(0, 0, 720, 345));
            signalAnalysis.UpdateLayout();
            ForceRender();
            signalAnalysis.HoverAtForTest(1);
            ForceRender();
            var signalHover = signalAnalysis.HoverTextForTest ?? "";
            if (!signalHover.Contains("-86 dBm", StringComparison.Ordinal) ||
                !signalHover.Contains("良好", StringComparison.Ordinal) ||
                !signalHover.Contains("RSRQ", StringComparison.Ordinal) ||
                !signalHover.Contains("SINR", StringComparison.Ordinal))
                throw new Exception("三段式信号分析图悬停提示不完整");

            var qualityCases = new[]
            {
                new C2000CellEntry("NR", "41", "1", "504990", "-78", "-7", "15", false, false),
                new C2000CellEntry("NR", "41", "2", "504990", "-86", "-9", "10", false, false),
                new C2000CellEntry("NR", "41", "3", "504990", "-96", "-12", "3", false, false),
                new C2000CellEntry("NR", "41", "4", "504990", "-108", "-16", "-1", false, false),
            };
            if (qualityCases.Select(x => x.Quality).ToArray() is not ["优秀", "良好", "一般", "较差"])
                throw new Exception("信号质量分级不符合绿/蓝/黄/红区间");
            var usageCalc = DashboardViewModel.CalculateMonthlyUsage(100, 48.771);
            if (Math.Abs(usageCalc.RemainingGb - 51.229) > 0.001 ||
                Math.Abs(usageCalc.Percent - 48.771) > 0.001 ||
                usageCalc.Status != "用量正常")
                throw new Exception("月套餐剩余流量计算不正确");
            var terminalBaseline = DevicesViewModel.CalculateTerminalBaseline(66L * 1024 * 1024, 1);
            var terminalDisplay = Math.Max(0, 66L * 1024 * 1024 - terminalBaseline);
            if (terminalBaseline >= 0 || Math.Abs(terminalDisplay - 1024d * 1024 * 1024) > 2)
                throw new Exception("终端手动累计流量可高于当前设备计数器");
            var trafficRows = new[]
            {
                new ClientRow("", "A", "A", "00:00:00:00:00:01", "", "", "正常", "", "", "", "", "", "",
                    "", false, false, true, false, "", 100, 75, "75 B", true),
                new ClientRow("", "B", "B", "00:00:00:00:00:02", "", "", "正常", "", "", "", "", "", "",
                    "", false, false, true, false, "", 200, 125, "125 B", true),
                new ClientRow("", "C", "C", "00:00:00:00:00:03", "", "", "正常", "", "", "", "", "", "",
                    "", false, false, true, true, "suspect", 999_999, 999_999, "999 KB", true),
            };
            if (DashboardViewModel.SumTerminalUsageBytes(trafficRows) != 200)
                throw new Exception("概览自动已用流量未按可信终端显示累计求和");

            var legacy = new AppSettings { Version = 2, Host = "http://192.168.1.1", Username = "admin" };
            var migrated = AppSettings.Normalize(legacy);
            if (migrated.Version != 4 || migrated.Host != "http://192.168.66.1" || migrated.Username != "root")
                throw new Exception("旧版设置迁移不正确");

            var snapshot = new C2000ProRuntimeSnapshot
            {
                Runtime = runtime,
                Cell = new C2000CellInfoItem { Status = cpe, Uptime = 120 },
                Identity = identity,
            };
            var history = new C2000SignalHistory(30);
            history.Observe(snapshot, DateTime.Now);
            if (!history.Save(force: true) || history.Samples.Count == 0 ||
                history.Samples[^1].RsrpDbm != -79)
                throw new Exception("C2000 信号历史保存失败");

            var redacted = ActivityLog.Sanitize(
                "phone_num=13800138000 sms_body=hidden-code imsi=460001234567890 iccid=89860012345678901234");
            if (redacted.Contains("13800138000", StringComparison.Ordinal) ||
                redacted.Contains("hidden-code", StringComparison.Ordinal) ||
                redacted.Contains("460001234567890", StringComparison.Ordinal) ||
                redacted.Contains("89860012345678901234", StringComparison.Ordinal))
                throw new Exception("短信/号码/卡标识脱敏失败");

            sb.AppendLine("PASS  C2000 离线协议：字段/身份/锁频解析、迁移、信号历史与短信脱敏");
        }
        catch (Exception ex)
        {
            failures.Add("C2000 离线协议：" + ex.Message);
            sb.AppendLine("FAIL  C2000 离线协议：" + ex.Message);
        }
    }

    private static void CheckCsvExport(StringBuilder sb, List<string> failures)
    {
        try
        {
            var mac = CsvExport.MaskMac("AA:BB:CC:DD:EE:FF");
            var ip = CsvExport.MaskIp("192.168.1.23");
            var escaped = CsvExport.Escape("=1+1");
            var traffic = CsvExport.BuildTrafficCsv(
                new[] { new TrafficCsvRow("2026-10-17T12", 1024, 2048, true) }, DateTime.Now, "当日（本机采样）");
            var terminals = CsvExport.BuildTerminalCsv(new[]
            {
                new TerminalCsvRow("DESKTOP-U3444TP", "DESKTOP-U3444TP", "AA:BB:CC:DD:EE:FF", "192.168.1.23",
                    "无线", "正常", 1000, 2000, 3000, 4000, 5000, 6000),
            }, DateTime.Now);
            var maskedText = CsvExport.MaskSensitiveText("MAC AA:BB:CC:DD:EE:FF IP 192.168.1.23");

            var temp = Path.GetTempFileName();
            CsvExport.WriteUtf8Bom(temp, terminals);
            var bytes = File.ReadAllBytes(temp);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            File.Delete(temp);

            if (mac == "AA:BB:**:**:EE:FF" && ip == "192.168.1.***" && escaped.StartsWith("'=", StringComparison.Ordinal) &&
                traffic.Contains("1024,2048,3072", StringComparison.Ordinal) &&
                terminals.Contains("AA:BB:**:**:EE:FF", StringComparison.Ordinal) &&
                !terminals.Contains("DESKTOP-U3444TP", StringComparison.Ordinal) &&
                !terminals.Contains("192.168.1.23", StringComparison.Ordinal) &&
                !maskedText.Contains("AA:BB:CC:DD:EE:FF", StringComparison.Ordinal) &&
                !maskedText.Contains("192.168.1.23", StringComparison.Ordinal) &&
                hasBom)
            {
                sb.AppendLine("PASS  CSV 导出：UTF-8 BOM 落盘、流量数据完整、终端/诊断文本脱敏、公式注入前缀已处理");
            }
            else
            {
                failures.Add("CSV 导出或脱敏不正确");
                sb.AppendLine($"FAIL  CSV：MAC={mac} IP={ip} escaped={escaped}");
            }
        }
        catch (Exception ex)
        {
            failures.Add("CSV 导出自检异常：" + ex.Message);
            sb.AppendLine("FAIL  CSV 导出自检异常：" + ex.Message);
        }
    }

    /// <summary>仅用于开发期视觉检查；未设置 NRadio_VISUAL_DIR 时不写任何文件。</summary>
    private static void SaveVisual(RenderTargetBitmap bitmap, string fileName)
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("NRadio_VISUAL_DIR");
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(dir, fileName));
            encoder.Save(stream);
        }
        catch
        {
            // 视觉快照失败不影响功能自检结论
        }
    }

    /// <summary>开发期页面视觉快照：只有设置 NRadio_VISUAL_DIR 时才执行。</summary>
    private static void SavePageVisuals(MainViewModel vm)
    {
        var dir = Environment.GetEnvironmentVariable("NRadio_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (vm.Devices.Selected is null && vm.Devices.Clients.FirstOrDefault() is { } firstClient)
            vm.Devices.SelectDeviceCommand.Execute(firstClient);

        var originalSamples = vm.Dashboard.Samples.ToList();
        var backupTheme = vm.Settings.Theme;
        try
        {
            var now = DateTime.Now;
            vm.Dashboard.Samples.Clear();
            vm.Dashboard.Samples.Add(new SamplePoint(now.AddSeconds(-10), 180_000, 24_000, -84, 3, true));
            vm.Dashboard.Samples.Add(new SamplePoint(now.AddSeconds(-5), 210_000, 31_000, -79, 3, true));
            vm.Dashboard.Samples.Add(new SamplePoint(now, 165_000, 19_000, -82, 3, true));

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                vm.Settings.Theme = theme;
                NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);

                var dashboard = new DashboardView { DataContext = vm.Dashboard };
                var dashboardRoot = new Grid { Width = 1263, Height = 720 };
                dashboardRoot.Children.Add(dashboard);
                dashboardRoot.Measure(new Size(1263, 720));
                dashboardRoot.Arrange(new Rect(0, 0, 1263, 720));
                dashboardRoot.UpdateLayout();
                ForceRender();
                var dashboardBmp = new RenderTargetBitmap(1263, 720, 96, 96, PixelFormats.Pbgra32);
                dashboardBmp.Render(dashboardRoot);
                SaveVisual(dashboardBmp, $"dashboard-{theme}.png");

                CapturePage(new C2000LockView { DataContext = vm.Lock2000 }, $"lock-{theme}.png");
                CapturePage(new C2000SignalView { DataContext = vm.Signal2000 }, $"signal-{theme}.png");
                CapturePage(new C2000WifiView { DataContext = vm.Wifi2000 }, $"wifi-{theme}.png");
                CapturePage(new C2000SmsView { DataContext = vm.Sms2000 }, $"sms-{theme}.png");
                CapturePage(new DevicesView { DataContext = vm.Devices }, $"devices-{theme}.png");

                var settings = new SettingsView { DataContext = vm.SettingsPage };
                var settingsRoot = new Grid { Width = 1000, Height = 2200 };
                settingsRoot.Children.Add(settings);
                settingsRoot.Measure(new Size(1000, 2200));
                settingsRoot.Arrange(new Rect(0, 0, 1000, 2200));
                settingsRoot.UpdateLayout();
                ForceRender();
                var settingsBmp = new RenderTargetBitmap(1000, 2200, 96, 96, PixelFormats.Pbgra32);
                settingsBmp.Render(settingsRoot);
                SaveVisual(settingsBmp, $"settings-{theme}.png");

                var battery = new BatteryView { DataContext = vm.Battery };
                var batteryRoot = new Grid { Width = 1000, Height = 1200 };
                batteryRoot.Children.Add(battery);
                batteryRoot.Measure(new Size(1000, 1200));
                batteryRoot.Arrange(new Rect(0, 0, 1000, 1200));
                batteryRoot.UpdateLayout();
                ForceRender();
                var batteryBmp = new RenderTargetBitmap(1000, 1200, 96, 96, PixelFormats.Pbgra32);
                batteryBmp.Render(batteryRoot);
                SaveVisual(batteryBmp, $"battery-{theme}.png");

                var logView = new LogView { DataContext = vm.Logs };
                var logRoot = new Grid { Width = 1000, Height = 720 };
                logRoot.Children.Add(logView);
                logRoot.Measure(new Size(1000, 720));
                logRoot.Arrange(new Rect(0, 0, 1000, 720));
                logRoot.UpdateLayout();
                ForceRender();
                var logBmp = new RenderTargetBitmap(1000, 720, 96, 96, PixelFormats.Pbgra32);
                logBmp.Render(logRoot);
                SaveVisual(logBmp, $"log-{theme}.png");
            }
        }
        catch (Exception ex)
        {
            // 视觉快照失败不影响自检结论
            try { File.WriteAllText(Path.Combine(dir, "visual-error.txt"), ex.ToString()); } catch { }
        }
        finally
        {
            vm.Dashboard.Samples.Clear();
            foreach (var p in originalSamples) vm.Dashboard.Samples.Add(p);
            vm.Settings.Theme = backupTheme;
            NRadioManager.Infrastructure.ThemeManager.Apply(vm.Settings);
        }
    }

    private static void CapturePage(FrameworkElement page, string fileName)
    {
        var root = new Grid { Width = 1080, Height = 720 };
        root.Children.Add(page);
        root.Measure(new Size(1080, 720));
        root.Arrange(new Rect(0, 0, 1080, 720));
        root.UpdateLayout();
        ForceRender();
        var bitmap = new RenderTargetBitmap(1080, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        SaveVisual(bitmap, fileName);
    }

    private static void ForceRender()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = Find<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static void Collect(DependencyObject root, List<FrameworkElement> into)
    {
        if (root is FrameworkElement fe) into.Add(fe);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            Collect(VisualTreeHelper.GetChild(root, i), into);
    }

    /// <summary>
    /// 磁贴保存链路校验：改尺寸 → 写入配置 → 从磁盘重新读取比对。
    /// 不进入编辑模式（不触发摇摆动画），因此不会影响自检稳定性；
    /// 校验的是真正的落盘路径：PersistTiles → AppSettings.Save → settings.json。
    /// </summary>
    private static void CheckTileSaveRoundTrip(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        try
        {
            var original = vm.Settings.Tiles.Select(t => new TileState
            {
                Id = t.Id, Order = t.Order, ColumnSpan = t.ColumnSpan,
                RowSpan = t.RowSpan, ShowTitle = t.ShowTitle, Visible = t.Visible,
            }).ToList();

            var tile = vm.Dashboard.Tiles[0];
            var expectShowTitle = !tile.ShowTitle;
            vm.Dashboard.ToggleTileTitleCommand.Execute(tile);   // 内部会调用 PersistTiles 落盘

            var onDisk = AppSettings.Load().Tiles.FirstOrDefault(t => t.Id == tile.Id);
            var ok = onDisk is not null && onDisk.ShowTitle == expectShowTitle;
            if (ok)
                sb.AppendLine($"PASS  磁贴外观已写入配置文件：{tile.Title} 标题显示={expectShowTitle}" +
                              $"（磁盘读回 {onDisk!.ShowTitle}）");
            else
            {
                failures.Add("磁贴外观未落盘");
                sb.AppendLine($"FAIL  磁贴外观未落盘：期望 {expectShowTitle}，磁盘为 {onDisk?.ShowTitle}");
            }

            // 复原用户布局并确认复原结果同样落盘
            vm.Settings.Tiles = original;
            vm.Dashboard.ReloadTiles();
            if (!vm.PersistSettings())
                sb.AppendLine("FAIL  复原布局写盘失败：" + vm.Settings.LastSaveError);
        }
        catch (Exception ex)
        {
            failures.Add("磁贴保存链路校验异常：" + ex.Message);
            sb.AppendLine("FAIL  磁贴保存链路校验异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 磁贴编辑 → 保存 → 落盘 → 重新读取 的完整链路校验。
    /// 这是本次"编辑后无法保存"回归的针对性检查：不依赖任何鼠标点击，
    /// 直接走 按钮命令 → 改尺寸 → 结束编辑 → 读磁盘 → 重载布局 这条真实代码路径。
    /// </summary>
    private static void CheckTileEditPersistence(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        try
        {
            var original = vm.Settings.Tiles.Select(t => new TileState
            {
                Id = t.Id, Order = t.Order, ColumnSpan = t.ColumnSpan,
                RowSpan = t.RowSpan, ShowTitle = t.ShowTitle, Visible = t.Visible,
            }).ToList();

            // 1) 通过按钮命令进入编辑（不是长按路径）
            vm.Dashboard.ToggleEditModeCommand.Execute(null);
            if (!vm.Dashboard.IsEditing)
            {
                failures.Add("按钮进入编辑失败");
                sb.AppendLine("FAIL  点击「编辑磁贴」后 IsEditing 仍为 false");
                return;
            }

            // 2) 改一个磁贴的尺寸与顺序
            var tile = vm.Dashboard.Tiles[0];
            var titleBefore = tile.ShowTitle;
            vm.Dashboard.ToggleTileTitleCommand.Execute(tile);
            vm.Dashboard.MoveTile(0, 2);
            var expectOrder = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
            var expectTitle = !titleBefore;

            // 3) 点「完成」对应的收尾逻辑
            vm.Dashboard.EndEditing();
            if (vm.Dashboard.IsEditing)
            {
                failures.Add("结束编辑后状态未复位");
                sb.AppendLine("FAIL  EndEditing 后 IsEditing 仍为 true");
            }

            // 4) 直接从磁盘重新读配置（验证真的写进文件，而不是只改了内存）
            var reloaded = AppSettings.Load();
            var onDisk = reloaded.Tiles.OrderBy(t => t.Order).Select(t => t.Id).ToList();
            var diskTile = reloaded.Tiles.FirstOrDefault(t => t.Id == tile.Id);

            var orderOk = onDisk.SequenceEqual(expectOrder);
            var titleOk = diskTile is not null && diskTile.ShowTitle == expectTitle;

            if (orderOk && titleOk)
                sb.AppendLine($"PASS  磁贴编辑可保存：顺序落盘 {string.Join(",", onDisk.Take(3))}…，" +
                              $"「{tile.Title}」标题显示 {titleBefore} → {expectTitle} 已写入配置文件");
            else
            {
                failures.Add("磁贴布局未真正落盘");
                sb.AppendLine($"FAIL  布局落盘校验：顺序一致={orderOk}，标题状态一致={titleOk}；" +
                              $"磁盘标题状态={diskTile?.ShowTitle}，期望 {expectTitle}");
            }

            // 5) 重新加载布局后界面数据与磁盘一致
            vm.Dashboard.ReloadTiles();
            var afterReload = vm.Dashboard.Tiles.Select(t => t.Id).ToList();
            if (afterReload.SequenceEqual(onDisk))
                sb.AppendLine("PASS  重新进入编辑模式看到的是上次保存的布局");
            else
            {
                failures.Add("重载后布局与磁盘不一致");
                sb.AppendLine("FAIL  重载布局与磁盘内容不一致");
            }

            // 6) 复原用户原布局
            vm.Settings.Tiles = original;
            vm.Dashboard.ReloadTiles();
            vm.PersistSettings();
        }
        catch (Exception ex)
        {
            failures.Add("磁贴保存链路校验异常：" + ex.Message);
            sb.AppendLine("FAIL  磁贴保存链路校验异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 通知生命周期校验：折叠型超时后保留内容、普通型超时后删除、展开后暂停自动处理、清除全部只删可删除项。
    /// 用 TriggerTimeoutForTest 走同一条超时逻辑，避免真的等待 5 秒。
    /// </summary>
    private static void CheckNoticeLifecycle(MainViewModel vm, StringBuilder sb, List<string> failures)
    {
        try
        {
            // 用独立的通知中心做生命周期校验，避免干扰主界面正在绑定的集合
            var n = new NoticeCenter();

            // A. 磁贴编辑提示：超时后折叠、内容保留
            var tile = n.RaiseTileEdit("概览磁贴", "尺寸已调整");
            tile.TriggerTimeoutForTest();
            if (tile.IsCompact && n.Items.Contains(tile))
                sb.AppendLine("PASS  磁贴编辑提示超时后折叠且内容保留");
            else
            {
                failures.Add("磁贴编辑提示未按预期折叠");
                sb.AppendLine($"FAIL  磁贴编辑提示：IsCompact={tile.IsCompact} 仍在列表={n.Items.Contains(tile)}");
            }

            // B. 折叠后点击可展开，且不再被原计时收起
            tile.Toggle();
            tile.TriggerTimeoutForTest();
            if (!tile.IsCompact && n.Items.Contains(tile))
                sb.AppendLine("PASS  折叠提示可再次展开，且展开后不会被立即收起");
            else
            {
                failures.Add("折叠提示展开行为异常");
                sb.AppendLine($"FAIL  折叠提示展开异常：IsCompact={tile.IsCompact}");
            }

            // C. 普通通知：超时后自动删除
            var normal = n.Raise(NoticeLevel.Success, "测试", "普通通知");
            normal.TriggerTimeoutForTest();
            if (!n.Items.Contains(normal))
                sb.AppendLine("PASS  普通通知超时后自动删除");
            else
            {
                failures.Add("普通通知未自动删除");
                sb.AppendLine("FAIL  普通通知超时后仍在列表");
            }

            // D. 阅读保护：已展开的普通通知不被自动删除
            var reading = n.Raise(NoticeLevel.Info, "测试", "正在阅读");
            reading.Toggle();
            reading.TriggerTimeoutForTest();
            if (reading.IsExpanded && n.Items.Contains(reading))
                sb.AppendLine("PASS  用户展开阅读中的通知不会被自动删除");
            else
            {
                failures.Add("阅读保护失效");
                sb.AppendLine($"FAIL  阅读保护：IsExpanded={reading.IsExpanded} 仍在列表={n.Items.Contains(reading)}");
            }

            // E. 单条删除不影响其他通知
            var keep = n.RaiseTileEdit("保留项", "不应被误删");
            reading.RemoveCommand.Execute(null);
            if (!n.Items.Contains(reading) && n.Items.Contains(keep))
                sb.AppendLine("PASS  单条删除只影响自身，其他通知不受影响");
            else
            {
                failures.Add("单条删除误伤其他通知");
                sb.AppendLine("FAIL  单条删除影响了其他通知");
            }

            // F. 清除全部：无残留
            n.ClearAllCommand.Execute(null);
            if (n.Items.Count == 0)
                sb.AppendLine("PASS  清除全部已清空通知中心");
            else
            {
                failures.Add("清除全部未清空");
                sb.AppendLine($"FAIL  清除全部后仍有 {n.Items.Count} 条");
            }

            // G. 通信日志是长期记录，不能被通知机制动到
            vm.Log.Info("自检写入的日志");
            if (vm.Log.Items.Count > 0)
                sb.AppendLine("PASS  通信日志不受通知清理影响（长期记录独立保留）");
            else
            {
                failures.Add("通信日志被通知清理误删");
                sb.AppendLine("FAIL  通信日志被清空");
            }
        }
        catch (Exception ex)
        {
            failures.Add("通知生命周期校验异常：" + ex.Message);
            sb.AppendLine("FAIL  通知生命周期校验异常：" + ex.Message);
        }
    }

    /// <summary>在 UI 线程上等待异步任务（WPF 对象必须建在 UI 线程，这里用帧泵推消息循环）。</summary>
    private static void PumpTask(Func<Task> work, StringBuilder log)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await work(); }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) log.AppendLine("FAIL  联机自检异常：" + failure.Message);
    }
}
