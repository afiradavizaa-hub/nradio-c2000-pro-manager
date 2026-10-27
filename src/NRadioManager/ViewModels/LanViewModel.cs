using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json.Nodes;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

public sealed record StaticIpEntry(string Mac, string Ip, string Index);

/// <summary>局域网模块：DHCP 地址池、DNS、静态 IP 绑定、强制门户重定向（后两项原厂界面未提供）。</summary>
public sealed class LanViewModel : ModuleViewModel
{
    public LanViewModel(MainViewModel host) : base(host, "局域网", "DHCP、DNS 与静态地址绑定")
    {
        SaveDhcpCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            ValidateIp(Ip, "路由器地址");
            ValidateIp(Start, "地址池起始地址");
            ValidateIp(End, "地址池结束地址");
            var lease = J.Int(LeaseTimeText);
            if (lease is null or < 60 or > 604800)
                throw new RouterException(RouterErrorKind.Device, "租期请在 60–604800 秒之间。");
            await Host.Api!.SetLanAsync(Ip, Start, End, lease, null, null, null, null, null, null, CancellationToken.None);
        }, "DHCP 设置已下发（修改路由器地址后需用新地址重新登录）"), () => Host.IsConnected);

        SaveDnsCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (DnsEnable)
            {
                ValidateIp(Dns1, "首选 DNS");
                if (!string.IsNullOrWhiteSpace(Dns2)) ValidateIp(Dns2, "备用 DNS");
            }
            await Host.Api!.SetLanAsync(null, null, null, null, DnsEnable, Dns1, Dns2, null, null, null, CancellationToken.None);
        }, "DNS 设置已下发"), () => Host.IsConnected);

        SaveIpv6Command = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetLanAsync(null, null, null, null, null, null, null, DhcpV6, null, null, CancellationToken.None);
        }, "IPv6 服务设置已下发"), () => Host.IsConnected);

        SaveRedirectCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (RedirectEnable && string.IsNullOrWhiteSpace(RedirectUrl))
                throw new RouterException(RouterErrorKind.Device, "启用重定向时必须填写目标地址。");
            await Host.Api!.SetLanAsync(null, null, null, null, null, null, null, null, RedirectEnable, RedirectUrl, CancellationToken.None);
        }, "强制门户重定向设置已下发"), () => Host.IsConnected);

        AddStaticIpCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(NewStaticMac) || string.IsNullOrWhiteSpace(NewStaticIp))
                throw new RouterException(RouterErrorKind.Device, "请填写 MAC 地址与 IP 地址。");
            ValidateIp(NewStaticIp, "绑定 IP");
            await Host.Api!.AddStaticIpAsync(NewStaticMac.Trim(), NewStaticIp.Trim(), CancellationToken.None);
            NewStaticMac = NewStaticIp = "";
        }, "静态 IP 绑定已下发"), () => Host.IsConnected);

        DeleteStaticIpCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            if (p is not StaticIpEntry entry) return;
            await Host.Api!.DeleteStaticIpAsync(entry.Mac, CancellationToken.None);
        }, "已删除静态 IP 绑定"), _ => Host.IsConnected);
    }

    private static void ValidateIp(string value, string field)
    {
        if (!IPAddress.TryParse(value, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new RouterException(RouterErrorKind.Device, $"{field} 不是合法的 IPv4 地址。");
    }

    private bool _dhcpEnabled;
    public bool DhcpEnabled { get => _dhcpEnabled; set => Set(ref _dhcpEnabled, value); }

    private string _ip = "—";
    /// <summary>路由器自身地址，可由用户修改（修改后需用新地址重新连接）。</summary>
    public string Ip { get => _ip; set => Set(ref _ip, value); }

    public string Mask { get; private set; } = "—";
    public string Mac { get; private set; } = "—";
    public string LanDomain { get; private set; } = "—";

    private string _start = "";
    public string Start { get => _start; set => Set(ref _start, value); }

    private string _end = "";
    public string End { get => _end; set => Set(ref _end, value); }

    private string _leaseTimeText = "";
    public string LeaseTimeText { get => _leaseTimeText; set => Set(ref _leaseTimeText, value); }

    private bool _dnsEnable;
    public bool DnsEnable { get => _dnsEnable; set => Set(ref _dnsEnable, value); }

    private string _dns1 = "";
    public string Dns1 { get => _dns1; set => Set(ref _dns1, value); }

    private string _dns2 = "";
    public string Dns2 { get => _dns2; set => Set(ref _dns2, value); }

    private bool _dhcpV6;
    public bool DhcpV6 { get => _dhcpV6; set => Set(ref _dhcpV6, value); }

    private bool _redirectEnable;
    public bool RedirectEnable { get => _redirectEnable; set => Set(ref _redirectEnable, value); }

    private string _redirectUrl = "";
    public string RedirectUrl { get => _redirectUrl; set => Set(ref _redirectUrl, value); }

    private string _newStaticMac = "";
    public string NewStaticMac { get => _newStaticMac; set => Set(ref _newStaticMac, value); }

    private string _newStaticIp = "";
    public string NewStaticIp { get => _newStaticIp; set => Set(ref _newStaticIp, value); }

    public ObservableCollection<StaticIpEntry> StaticIps { get; } = new();

    public string RedirectNote =>
        "强制门户重定向（redirect_enable / redirect_url）在设备接口中存在，但原厂界面没有入口，本软件已实现。首次使用建议先在局域网内验证效果。";

    public ICommand SaveDhcpCommand { get; }
    public ICommand SaveDnsCommand { get; }
    public ICommand SaveIpv6Command { get; }
    public ICommand SaveRedirectCommand { get; }
    public ICommand AddStaticIpCommand { get; }
    public ICommand DeleteStaticIpCommand { get; }

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;
        var d = await api.GetDhcpAsync(CancellationToken.None);

        DhcpEnabled = J.Bool(d.Status) ?? false;
        Ip = d.Ip ?? "—";
        Mask = d.Mask ?? "—";
        Mac = d.Mac ?? "—";
        Start = d.Start ?? "";
        End = d.End ?? "";
        LeaseTimeText = d.LeaseTime ?? "";
        DnsEnable = J.Bool(d.DnsEnable) ?? false;
        Dns1 = d.Dns1 ?? "";
        Dns2 = d.Dns2 ?? "";
        DhcpV6 = J.Bool(d.DhcpV6Server) ?? false;
        RedirectEnable = J.Bool(d.RedirectEnable) ?? false;
        RedirectUrl = d.RedirectUrl ?? "";

        StaticIps.Clear();
        foreach (var node in d.FixedIpList ?? new List<JsonNode>())
        {
            if (node is JsonObject o)
                StaticIps.Add(new StaticIpEntry(o["mac"]?.ToString() ?? "", o["ip"]?.ToString() ?? "", o["index"]?.ToString() ?? ""));
        }

        var home = await api.GetHomepageAsync(CancellationToken.None);
        LanDomain = string.IsNullOrWhiteSpace(home.LanDomain) ? "—" : home.LanDomain;

        foreach (var n in new[] { nameof(Ip), nameof(Mask), nameof(Mac), nameof(LanDomain) }) OnPropertyChanged(n);
    }
}
