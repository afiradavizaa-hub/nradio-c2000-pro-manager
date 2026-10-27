using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

public sealed record SimRow(string Id, string Name, string Type, bool Present, bool Banned, string Iccid, string Imsi, bool IsActive);

/// <summary>系统与维护模块：设备信息、SIM/eSIM 卡位管理、管理员账号、重启与恢复出厂。</summary>
public sealed class SystemViewModel : ModuleViewModel
{
    public SystemViewModel(MainViewModel host) : base(host, "系统与维护", "设备信息、账号与维护操作")
    {
        RestartCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (MessageBox.Show("确定重启路由器？重启期间所有网络连接（包括本机）会中断约 30–60 秒。",
                    "重启设备", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await Host.Api!.RestartAsync(CancellationToken.None);
            MessageBox.Show("重启命令已下发。设备将在约 30–60 秒后恢复，期间本软件会显示连接失败，稍后请点击「重新连接」。",
                "已下发重启命令", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "重启命令已下发", reloadAfter: false), () => Host.IsConnected);

        RestoreFactoryCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var r = MessageBox.Show(
                "恢复出厂设置会清除全部配置：Wi-Fi 名称与密码、APN、账号设置等，设备会回到初始状态，当前网络会中断。\n\n确定继续？",
                "恢复出厂设置", MessageBoxButton.YesNo, MessageBoxImage.Stop);
            if (r != MessageBoxResult.Yes) return;
            if (MessageBox.Show("再次确认：此操作不可撤销。", "恢复出厂设置",
                    MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
            await Host.Api!.RestoreFactoryAsync(CancellationToken.None);
        }, "恢复出厂命令已下发", reloadAfter: false), () => Host.IsConnected);

        SwitchSimCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            if (p is not SimRow row) return;
            if (row.IsActive) throw new RouterException(RouterErrorKind.Device, "该卡位已是当前使用中的卡。");
            if (!row.Present) throw new RouterException(RouterErrorKind.Device, "该卡位没有检测到卡片。");
            var warn = MessageBox.Show(
                $"切换到「{row.Name}」？\n\n注意：本软件尚未在真机上验证过此切换流程（原厂页面提供该功能，接口字段为 switch_mode / sole_sim_id）。" +
                "切换会重新注册网络，期间网络会中断。",
                "切换 SIM 卡位", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (warn != MessageBoxResult.Yes) return;
            await Host.Api!.SwitchSimAsync("1", row.Id, CancellationToken.None);
        }, "SIM 切换命令已下发"), _ => Host.IsConnected);

        ChangePasswordCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            ValidatePasswordChange();
            var users = new List<UserAccount>();
            foreach (var u in Users)
            {
                var isCurrent = string.Equals(u.Username, CurrentUser, StringComparison.OrdinalIgnoreCase);
                users.Add(new UserAccount
                {
                    Username = u.Username,
                    Password = isCurrent ? NewPassword : "",
                    Authority = u.Authority ?? "1",
                });
            }
            await Host.Api!.SetAccountsAsync("2", users, CancellationToken.None);
            MessageBox.Show("密码修改命令已下发。若旧密码仍可登录，说明该操作需要设备端确认或原厂流程不同，请以实际登录结果为准。",
                "修改管理员密码", MessageBoxButton.OK, MessageBoxImage.Information);
            OldPassword = NewPassword = ConfirmPassword = "";
        }, "已提交管理员密码修改"), () => Host.IsConnected);
    }

    private void ValidatePasswordChange()
    {
        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
            throw new RouterException(RouterErrorKind.Device, "两次输入的新密码不一致。");
        if (string.IsNullOrEmpty(NewPassword))
            throw new RouterException(RouterErrorKind.Device, "新密码不能为空。");
        if (NewPassword.Length is < 5 or > 32)
            throw new RouterException(RouterErrorKind.Device, "新密码长度需在 5–32 位之间。");
        if (OldPassword != Host.Password)
            throw new RouterException(RouterErrorKind.Device, "当前密码与本次登录所用密码不一致。");
    }

    // ---------------- 设备信息 ----------------

    public string DeviceName { get; private set; } = "—";
    public string Hardware { get; private set; } = "—";
    public string Firmware { get; private set; } = "—";
    public string FirmwareDate { get; private set; } = "—";
    public string Serial { get; private set; } = "—";
    public string Mac { get; private set; } = "—";
    public string Imei { get; private set; } = "—";
    public string Imsi { get; private set; } = "—";
    public string Iccid { get; private set; } = "—";
    public string TpbVersion { get; private set; } = "—";
    public string TwoPartition { get; private set; } = "—";
    public string TotalSimCount { get; private set; } = "—";
    public string CurrentUser { get; private set; } = "admin";
    public string MultiAccount { get; private set; } = "—";

    public string FirmwareNote =>
        "原厂固件升级通过 /xml_action.cgi?Action=Upload&file=upgrade 上传镜像完成。" +
        "升级会擦写设备固件，失败可能导致设备无法启动，且本机没有可用的固件包可供验证，" +
        "因此本版本不提供升级按钮（未实现），以免出现无法验证、无法回滚的高风险操作。";

    public bool HasAuthWarning => Host.HasAuthWarning;
    public string AuthWarning => Host.AuthWarning;

    // ---------------- SIM ----------------

    public ObservableCollection<SimRow> Sims { get; } = new();
    public string SimSwitchMode { get; private set; } = "—";
    public string SimNote =>
        "设备支持 eSIM 卡位切换（接口 mss_support_info / mss_support_set，字段 switch_mode、sole_sim_id）。" +
        "本软件已实现切换入口，但该流程尚未在真机验证，界面会给出风险提示。";

    // ---------------- 账号 ----------------

    public ObservableCollection<UserAccount> Users { get; } = new();

    private string _oldPassword = "";
    public string OldPassword { get => _oldPassword; set => Set(ref _oldPassword, value); }

    private string _newPassword = "";
    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value); }

    private string _confirmPassword = "";
    public string ConfirmPassword { get => _confirmPassword; set => Set(ref _confirmPassword, value); }

    public string AccountNote =>
        "设备的账号管理接口支持多账号与权限（标准/受限）。新增、删除账号的 account_action 取值未能从原厂前端完全确认，" +
        "本版本只提供「修改当前管理员密码」与账号列表查看，其余账号操作标注为未实现。";

    public ICommand RestartCommand { get; }
    public ICommand RestoreFactoryCommand { get; }
    public ICommand SwitchSimCommand { get; }
    public ICommand ChangePasswordCommand { get; }

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;

        var home = await api.GetHomepageAsync(CancellationToken.None);
        DeviceName = home.DeviceName ?? "—";
        Hardware = home.HwVersion ?? "—";
        Firmware = home.SwVersion ?? "—";
        Serial = home.SerialNumber ?? "—";
        Mac = home.Mac ?? "—";
        Imei = home.Imei ?? "—";
        Imsi = home.Imsi ?? "—";
        Iccid = home.Iccid ?? "—";
        TpbVersion = home.TpbVer ?? "—";

        var fw = await api.GetFirmwareAsync(CancellationToken.None);
        FirmwareDate = fw.VersionDate ?? "—";
        TwoPartition = J.Bool(fw.TwoPartition) switch { true => "支持（A/B 双分区）", false => "不支持", null => "—" };

        var mss = await api.GetMssSupportAsync(CancellationToken.None);
        SimSwitchMode = mss.SwitchMode switch { "1" => "手动切换", "0" => "自动", null => "—", var v => $"原始值 {v}" };
        Sims.Clear();
        foreach (var s in mss.SimList ?? new List<SimCard>())
        {
            var type = s.SimType switch { "1" => "eSIM", "2" => "实体 SIM", null => "—", var v => $"类型 {v}" };
            Sims.Add(new SimRow(s.SimId ?? "", s.SimName ?? "未命名", type,
                J.Bool(s.SimPresent) ?? false, J.Bool(s.SimBanned) ?? false,
                s.SimIccid ?? "", s.SimImsi ?? "", s.SimId == mss.CurrentSimId));
        }

        var status = await api.GetMssStatusAsync(CancellationToken.None);
        TotalSimCount = status.TotalSimCount ?? "—";

        var acc = await api.GetAccountsAsync(CancellationToken.None);
        CurrentUser = acc.RouterUsername ?? "admin";
        MultiAccount = J.Bool(acc.MultiAccount) switch { true => "支持多账号", false => "单账号", null => "—" };
        Users.Clear();
        foreach (var u in acc.UserList ?? new List<UserAccount>()) Users.Add(u);

        foreach (var n in new[]
                 {
                     nameof(DeviceName), nameof(Hardware), nameof(Firmware), nameof(FirmwareDate), nameof(Serial),
                     nameof(Mac), nameof(Imei), nameof(Imsi), nameof(Iccid), nameof(TpbVersion), nameof(TwoPartition),
                     nameof(TotalSimCount), nameof(CurrentUser), nameof(MultiAccount), nameof(SimSwitchMode),
                     nameof(HasAuthWarning),
                 })
            OnPropertyChanged(n);
    }
}
