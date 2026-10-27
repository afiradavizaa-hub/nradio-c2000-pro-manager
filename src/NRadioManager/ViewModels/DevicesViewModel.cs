using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NRadioManager.Controls;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

public sealed record ClientRow(
    string Index, string Name, string Hostname, string Mac, string Ip, string ConnType, string Status,
    string TotalRx, string TotalTx, string MonthRx, string MonthTx, string Day3Rx, string Day3Tx,
    string AccessTime, bool IsBlocked, bool CustomNamed, bool Online, bool CounterSuspect, string CounterWarning,
    long RawTotalBytes, long DisplayTotalBytes, string UsageTotalText, bool UsesLocalBaseline);

/// <summary>
/// 设备与流量：在线终端清单、自定义名称、单设备流量详情（当日 / 近七天 / 本月）、拉黑与解除。
/// 统计口径与概览页完全一致（都取自同一份本地采样历史）。
/// </summary>
public sealed class DevicesViewModel : ModuleViewModel
{
    public DevicesViewModel(MainViewModel host) : base(host, "设备与流量", "在线终端、自定义名称与单设备流量")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        ResetStatsCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var r = MessageBox.Show(
                "将清零当前设备的流量统计与本机历史基线。\n\nC2000 Pro 会调用真实 SIM 流量清零接口；" +
                "终端主界面的总量使用本机重置基线，因此会立即刷新。操作不可撤销，是否继续？",
                "重置设备统计", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            if (MessageBox.Show("最终确认：清零流量统计？", "最终确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            if (Host.C2000Adapter is { } adapter)
            {
                var result = await adapter.ResetDataUsageAsync(true, true, CancellationToken.None);
                if (!result.Confirmed)
                    throw new RouterException(RouterErrorKind.Device, result.Message, "cpedata-clean");
            }
            else
            {
                await Host.Api!.DeviceActionAsync("4", null, CancellationToken.None);
            }
            Host.History.ResetLocal();
            ResetAllUsageBaselines();
            Host.Log.Warn(LogCategory.Data, "终端流量", "用户确认清零设备流量统计与本机终端基线。");
        }, "已下发重置统计命令"), () => Host.IsConnected);

        BlockCommand = new AsyncRelayCommand(p => ConfirmAndRunAsync(p, block: true), p => Host.IsConnected);
        UnblockCommand = new AsyncRelayCommand(p => ConfirmAndRunAsync(p, block: false), p => Host.IsConnected);
        SelectDeviceCommand = new RelayCommand(p => { if (p is ClientRow row) OpenDetail(row); });
        CloseDetailCommand = new RelayCommand(() => { Selected = null; DetailVisible = false; });

        SaveAliasCommand = new RelayCommand(_ =>
        {
            if (Selected is null) return;
            Host.Aliases.Set(Selected.Mac, AliasInput);
            var mac = Selected.Mac;
            var idx = Clients.ToList().FindIndex(c => c.Mac == mac);
            ApplyAliasToRows();
            FinishAliasChange(string.IsNullOrWhiteSpace(AliasInput)
                ? $"已清除 {mac} 的自定义名称，恢复显示设备原始主机名。"
                : $"已把 {mac} 命名为「{AliasInput}」，并已同步到列表与统计明细。");
            _ = idx;
        });

        ClearAliasCommand = new RelayCommand(_ =>
        {
            if (Selected is null) return;
            AliasInput = "";
            Host.Aliases.Set(Selected.Mac, null);
            ApplyAliasToRows();
            FinishAliasChange("已恢复为设备原始主机名。");
        });
        SaveUsageCommand = new RelayCommand(_ => SaveSelectedUsage(), _ => Selected is not null);
        ResetSelectedUsageCommand = new RelayCommand(_ => ResetSelectedUsage(), _ => Selected is not null);
        UseRawUsageCommand = new RelayCommand(_ => UseRawUsage(), _ => Selected is not null);
    }

    private void FinishAliasChange(string successMessage)
    {
        var error = Host.Aliases.LastSaveError;
        if (string.IsNullOrWhiteSpace(error))
        {
            IsError = false;
            Message = successMessage;
            return;
        }

        IsError = true;
        var message = "设备名称已在当前会话生效，但保存到本地失败：" + error;
        Host.Log.Error(LogCategory.Persistence, "设备名称", message);
        Host.Notices.Raise(NoticeLevel.Error, Title, message,
            logCategory: LogCategory.Persistence, logSource: "设备名称", logEvent: false);
    }

    private async Task ConfirmAndRunAsync(object? parameter, bool block)
    {
        if (parameter is not ClientRow row) return;
        var text = block
            ? $"禁止「{row.Name}」（{row.Mac}）上网？\n设备侧标记为黑名单（action=2），该终端会立即失去网络访问。"
            : $"解除对「{row.Name}」（{row.Mac}）的限制？\n设备侧移除黑名单（action=3）。";
        if (MessageBox.Show(text, block ? "禁止上网" : "解除限制", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            if (Host.C2000Adapter is { } adapter)
            {
                var result = await adapter.SetClientAccessAsync(row.Mac, !block, CancellationToken.None);
                if (!result.Confirmed)
                    throw new RouterException(RouterErrorKind.Device, result.Message, "client-access");
            }
            else
            {
                await Host.Api!.DeviceActionAsync(block ? "2" : "3", row.Mac, CancellationToken.None);
            }
        }, block ? $"已禁止 {row.Name} 上网" : $"已解除 {row.Name} 的限制");
    }

    // ---------------- 列表 ----------------

    public ObservableCollection<ClientRow> Clients { get; } = new();

    public string KnownDeviceCount { get; private set; } = "—";
    public string OnlineCount { get; private set; } = "—";
    public string TotalBanner { get; private set; } = "—";
    public string CounterWarning { get; private set; } = "";
    public bool HasCounterWarning => CounterWarning.Length > 0;

    public string DataSourceNote =>
        "流量数据来自设备接口（client_list）：累计 / 本月 / 近 3 日都是设备侧统计口径。" +
        "固件对部分离线/历史终端会返回失真的计数器（例如单机几十 TB，远超整机累计），" +
        "这类值已标为「计数器异常」并隐藏，请以本地采样为准。" +
        "详情页中的「当日」「近七天」为本地采样（由累计计数差分得到，只覆盖软件运行时段），两者口径不同，界面已分别标注。";

    public string RoutingNote =>
        "流量去向（域名 / 服务类别）无法实现：已核查设备提供的全部 30 个接口，没有任何 DNS 查询记录、连接日志或流量分类接口，" +
        "因此不存在可真实获取的数据源。" +
        "本软件不会为此抓包或注入代理——那需要常驻后台进程、会记录你的全部上网行为，且与「不默认记录用户上网活动」的要求冲突。" +
        "在设备固件不提供日志的前提下，任何「域名排行」都只能是编造的数据。";

    // ---------------- 详情 ----------------

    public ObservableCollection<BarItem> DeviceBars { get; } = new();

    private ClientRow? _selected;
    public ClientRow? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(HasSelection));
            (SaveUsageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ResetSelectedUsageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (UseRawUsageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => _selected is not null;

    private bool _detailVisible;
    public bool DetailVisible { get => _detailVisible; private set => Set(ref _detailVisible, value); }

    public string DetailTitle => _selected is null ? "未选择设备" : $"设备详情 · {_selected.Name}";
    public string DetailHostname { get; private set; } = "—";
    public string DetailMac { get; private set; } = "—";
    public string DetailIp { get; private set; } = "—";
    public string DetailConnType { get; private set; } = "—";
    public string DetailOnline { get; private set; } = "—";
    public string DetailToday { get; private set; } = "—";
    public string DetailWeek { get; private set; } = "—";
    public string DetailMonthLocal { get; private set; } = "—";
    public string DetailMonthDevice { get; private set; } = "—";
    public string DetailIdentityNote { get; private set; } = "";
    public string DetailUsageTotal { get; private set; } = "—";
    public string DetailUsageSource { get; private set; } = "—";
    public string ManualUsageInputGb { get; set; } = "";

    private string _aliasInput = "";
    public string AliasInput { get => _aliasInput; set => Set(ref _aliasInput, value); }

    public ICommand ResetStatsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand BlockCommand { get; }
    public ICommand UnblockCommand { get; }
    public ICommand SelectDeviceCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand SaveAliasCommand { get; }
    public ICommand ClearAliasCommand { get; }
    public ICommand SaveUsageCommand { get; }
    public ICommand ResetSelectedUsageCommand { get; }
    public ICommand UseRawUsageCommand { get; }

    private DeviceManagementInfo? _lastInfo;
    private StatusInfo? _lastStatus;

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;
        var info = await api.GetDevicesAsync(CancellationToken.None);
        var status = await api.GetStatusAsync(CancellationToken.None);
        ApplyDeviceData(info, status);
    }

    /// <summary>由全局轮询直接喂入最新终端与整机状态，保证流量数字和重置结果实时刷新。</summary>
    public void ApplyDeviceData(DeviceManagementInfo info, StatusInfo status)
    {
        _lastInfo = info;
        _lastStatus = status;

        Clients.Clear();
        var suspect = 0;
        foreach (var c in info.ClientList ?? new List<ClientEntry>())
        {
            var blocked = c.Status == "2";
            var hasIp = !string.IsNullOrWhiteSpace(c.Ip) &&
                        !string.Equals(c.Ip, "NA", StringComparison.OrdinalIgnoreCase);
            var online = c.Status != "0" && hasIp;
            var statusText = blocked ? "已禁止上网" : online ? "正常" : "离线（设备历史记录）";
            var hostname = string.IsNullOrWhiteSpace(c.Name) ? "未知设备" : c.Name;
            var counters = TrafficSanity.Inspect(c, status);
            if (!counters.Reliable) suspect++;
            var rawTotal = Math.Max(0, counters.Rx ?? 0) + Math.Max(0, counters.Tx ?? 0);
            var baseline = Host.Settings.TerminalTrafficBaselinesBytes.GetValueOrDefault(c.Mac ?? "");
            if (rawTotal < baseline)
            {
                baseline = rawTotal;
                Host.Settings.TerminalTrafficBaselinesBytes[c.Mac ?? ""] = baseline;
            }
            var displayTotal = Math.Max(0, rawTotal - baseline);
            Clients.Add(new ClientRow(
                c.Index ?? "", Host.DisplayName(c.Mac, hostname), hostname, c.Mac ?? "", c.Ip ?? "",
                c.ConnType == "WIFI" ? "无线" : c.ConnType ?? "—",
                statusText,
                counters.Reliable ? J.Bytes(counters.Rx) : "—",
                counters.Reliable ? J.Bytes(counters.Tx) : "—",
                counters.Reliable ? J.Bytes(counters.MonthRx) : "—",
                counters.Reliable ? J.Bytes(counters.MonthTx) : "—",
                counters.Reliable ? J.Bytes(counters.Day3Rx) : "—",
                counters.Reliable ? J.Bytes(counters.Day3Tx) : "—",
                c.AccessTime ?? "—", blocked, Host.Aliases.Has(c.Mac),
                online, !counters.Reliable, counters.Reason ?? "",
                rawTotal, displayTotal, J.Bytes(displayTotal), baseline > 0));
        }

        foreach (var k in info.KnownDevices ?? new List<KnownDevice>())
        {
            if (Clients.Any(c => string.Equals(c.Mac, k.Mac, StringComparison.OrdinalIgnoreCase))) continue;
            var hostname = string.IsNullOrWhiteSpace(k.Name) ? "未知设备" : k.Name;
            Clients.Add(new ClientRow(k.Index ?? "", Host.DisplayName(k.Mac, hostname), hostname, k.Mac ?? "",
                k.IpAddress ?? "", k.ConnType == "WLAN" ? "无线" : k.ConnType ?? "—",
                "离线（历史设备）", "—", "—", "—", "—", "—", "—", k.ConnTime ?? "—", false,
                Host.Aliases.Has(k.Mac), false, false, "", 0, 0, "—", false));
        }

        KnownDeviceCount = (info.KnownDevices?.Count ?? 0).ToString();
        OnlineCount = Clients.Count(c => c.Online).ToString();
        CounterWarning = suspect == 0
            ? ""
            : $"已隐藏 {suspect} 台终端的设备侧计数器：固件上报值与整机累计明显矛盾，不能当作真实流量。";

        var localTotal = Clients.Where(x => x.Online).Sum(x => x.DisplayTotalBytes);
        TotalBanner = $"本机显示终端合计：{J.Bytes(localTotal)} · " +
                      $"设备原始计数：↓ {J.Bytes(J.Long(status.RxByteAll))} · ↑ {J.Bytes(J.Long(status.TxByteAll))}";

        foreach (var n in new[]
                 {
                     nameof(KnownDeviceCount), nameof(OnlineCount), nameof(TotalBanner),
                     nameof(CounterWarning), nameof(HasCounterWarning),
                 })
            OnPropertyChanged(n);

        // 详情面板选中项在新数据里可能已消失（设备断开），此时关闭详情避免展示过期数据
        if (Selected is not null)
        {
            var again = Clients.FirstOrDefault(c => c.Mac == Selected.Mac);
            if (again is null) { Selected = null; DetailVisible = false; }
            else OpenDetail(again, silent: true, preserveInputs: true);
        }

        // 概览的自动已用值直接消费本页的显示累计，终端变化或本页清零后无需再等额外请求。
        Host.Dashboard.RefreshTerminalUsageFromClients(
            updateInput: Host.Settings.MonthlyDataManualUsedGb is null);
    }

    private void ApplyAliasToRows()
    {
        var list = Clients.ToList();
        Clients.Clear();
        foreach (var c in list)
            Clients.Add(c with { Name = Host.DisplayName(c.Mac, c.Hostname), CustomNamed = Host.Aliases.Has(c.Mac) });
        if (Selected is not null)
            Selected = Clients.FirstOrDefault(c => c.Mac == Selected.Mac);
    }

    private void SaveSelectedUsage()
    {
        if (Selected is null) return;
        if (!double.TryParse(ManualUsageInputGb, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out var gb) || gb < 0 ||
            gb > 1_000_000)
        {
            Host.Notices.Raise(NoticeLevel.Warning, "终端流量",
                "请输入 0–1000000 GB 的有效累计用量。");
            return;
        }
        // 基线可以小于 0：允许手动录入比当前设备计数器更大的“外部/历史累计”。
        // 后续新增流量仍会以 raw - baseline 的方式实时增长。
        var baseline = CalculateTerminalBaseline(Selected.RawTotalBytes, gb);
        Host.Settings.TerminalTrafficBaselinesBytes[Selected.Mac] = baseline;
        Host.PersistSettings();
        if (_lastInfo is not null && _lastStatus is not null) ApplyDeviceData(_lastInfo, _lastStatus);
        Host.Notices.Raise(NoticeLevel.Success, "终端流量",
            $"已把「{Selected.Name}」的显示累计用量设为 {gb:0.###} GB；后续按设备计数器继续增长。");
    }

    private void ResetSelectedUsage()
    {
        if (Selected is null) return;
        Host.Settings.TerminalTrafficBaselinesBytes[Selected.Mac] = Selected.RawTotalBytes;
        Host.PersistSettings();
        Host.History.ResetDevice(Selected.Mac);
        ManualUsageInputGb = "0";
        OnPropertyChanged(nameof(ManualUsageInputGb));
        if (_lastInfo is not null && _lastStatus is not null) ApplyDeviceData(_lastInfo, _lastStatus);
        Host.Notices.Raise(NoticeLevel.Success, "终端流量",
            $"「{Selected.Name}」的本机显示用量已清零并立即刷新。");
    }

    private void UseRawUsage()
    {
        if (Selected is null) return;
        Host.Settings.TerminalTrafficBaselinesBytes.Remove(Selected.Mac);
        Host.PersistSettings();
        ManualUsageInputGb = (Selected.RawTotalBytes / 1024d / 1024d / 1024d)
            .ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(ManualUsageInputGb));
        if (_lastInfo is not null && _lastStatus is not null) ApplyDeviceData(_lastInfo, _lastStatus);
        Host.Notices.Raise(NoticeLevel.Info, "终端流量",
            "已恢复显示设备原始累计值。");
    }

    public void ResetAllUsageBaselines()
    {
        if (_lastInfo?.ClientList is not null && _lastStatus is not null)
        {
            foreach (var c in _lastInfo.ClientList)
            {
                if (string.IsNullOrWhiteSpace(c.Mac)) continue;
                var counters = TrafficSanity.Inspect(c, _lastStatus);
                Host.Settings.TerminalTrafficBaselinesBytes[c.Mac] =
                    Math.Max(0, counters.Rx ?? 0) + Math.Max(0, counters.Tx ?? 0);
            }
        }
        Host.PersistSettings();
        if (_lastInfo is not null && _lastStatus is not null)
            ApplyDeviceData(_lastInfo, _lastStatus);
    }

    internal static long CalculateTerminalBaseline(long rawTotalBytes, double targetGb)
        => rawTotalBytes - (long)Math.Round(targetGb * 1024d * 1024d * 1024d);

    /// <summary>打开设备流量详情：三个统计周期 + 每日趋势，全部来自与概览页相同的本地历史。</summary>
    private void OpenDetail(ClientRow row, bool silent = false, bool preserveInputs = false)
    {
        Selected = row;
        DetailVisible = true;
        if (!preserveInputs)
            AliasInput = Host.Aliases.Get(row.Mac) ?? "";

        DetailHostname = row.Hostname;
        DetailMac = row.Mac;
        DetailIp = row.Ip;
        DetailConnType = row.ConnType;
        DetailOnline = row.Status;
        DetailUsageTotal = $"设备计数器总量 {row.UsageTotalText}";
        DetailUsageSource = row.UsesLocalBaseline
            ? "当前显示值 = 设备计数器 − 本机重置/手动基线；会随后续采样实时增长。"
            : "当前显示设备原始累计计数器。";
        if (!preserveInputs)
            ManualUsageInputGb = (row.DisplayTotalBytes / 1024d / 1024d / 1024d)
                .ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);

        var h = Host.History;
        var today = h.DeviceTotal(row.Mac, DateTime.Today);
        DetailToday = $"↓ {J.Bytes(today.Rx)} · ↑ {J.Bytes(today.Tx)} · 合计 {J.Bytes(today.Rx + today.Tx)}";

        var week = h.DeviceDaily(row.Mac, 7);
        var wRx = week.Sum(x => x.Rx);
        var wTx = week.Sum(x => x.Tx);
        DetailWeek = $"↓ {J.Bytes(wRx)} · ↑ {J.Bytes(wTx)} · 合计 {J.Bytes(wRx + wTx)}";

        var month = h.MonthDaily(true);
        var monthRows = h.DeviceDaily(row.Mac, DateTime.Today.Day);
        var mRx = monthRows.Sum(x => x.Rx);
        var mTx = monthRows.Sum(x => x.Tx);
        DetailMonthLocal = $"↓ {J.Bytes(mRx)} · ↑ {J.Bytes(mTx)} · 合计 {J.Bytes(mRx + mTx)}";

        DetailMonthDevice = row.CounterSuspect
            ? $"设备计数器异常，已隐藏（{row.CounterWarning}）"
            : row.MonthRx == "—"
                ? "设备未上报该终端的本月计数器"
                : $"↓ {row.MonthRx} · ↑ {row.MonthTx}（含软件未运行期间的流量）";

        DetailIdentityNote = Host.Aliases.Has(row.Mac)
            ? "已按 MAC 地址关联自定义名称与历史流量，IP 变化不影响识别。"
            : "未设置自定义名称，显示设备上报的主机名（若主机名变化，历史仍按 MAC 归属）。";
        _ = month;

        DeviceBars.Clear();
        foreach (var p in week) DeviceBars.Add(new BarItem(p.Key, p.Label, p.Rx, p.Tx));

        foreach (var n in new[]
                 {
                     nameof(DetailHostname), nameof(DetailMac), nameof(DetailIp), nameof(DetailConnType),
                     nameof(DetailOnline), nameof(DetailToday), nameof(DetailWeek), nameof(DetailMonthLocal),
                     nameof(DetailMonthDevice), nameof(DetailIdentityNote), nameof(DetailTitle),
                     nameof(DetailUsageTotal), nameof(DetailUsageSource), nameof(ManualUsageInputGb),
                 })
            OnPropertyChanged(n);

        if (!silent)
        {
            Message = null;
            IsError = false;
        }
    }
}
