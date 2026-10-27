# NRadio C2000 Pro 高级管理器

面向 **NRadio C2000 Pro / NRadio C2000-515** 的 Windows 桌面管理器，并保留原 NRadio（Marvell Digest）适配能力。

软件直接访问局域网内的原厂 NROS/LuCI 后台，不经过第三方中转服务器。设备数据来自当前固件的真实接口；不支持或尚未确认的字段会显示说明，不使用模拟数据。

## 当前设备基线

- 管理地址：`http://192.168.66.1`
- 登录用户名：NROS 页面固定为 `root`
- HTTPS：当前设备未监听 443；管理后台使用 HTTP
- 已验证型号：`C2000-515`
- 已验证主固件：`NROS-2.2.5.0.1`
- 已验证蜂窝模式：`NR SA`

登录密码仅通过软件界面输入。勾选“记住密码”后使用 Windows DPAPI 保存；源码、日志、测试报告和发布包中均不包含管理密码。

## 快速开始

1. 电脑连接 C2000 Pro 的 Wi-Fi 或有线局域网。
2. 运行 `NRadioManager.exe`。
3. 确认地址为 `http://192.168.66.1`，用户名 `root`，输入当前管理密码。
4. 连接成功后使用左侧导航查看蜂窝、信号、锁频、SIM、Wi-Fi、终端、短信和维护功能。

C2000 Pro 使用 Cookie 会话认证，不是 NRadio 的 Digest。重复输入错误密码不会触发无限重试。

## 已实现功能

### v4.2 / v4.2.1 交互与流量口径升级

- 概览固定为 4×2 紧凑磁贴，在当前窗口高度下一屏显示全部 8 块磁贴。
- 月套餐总量、已用流量可编辑，输入时实时计算剩余流量和占用百分比；自动模式改为取“接入终端”可信设备显示累计的合计，并随终端轮询实时更新。
- 不再把 C2000 SIM 页面易混淆的 `threshold_smds` 当作近期实际用量；该值仍可在原生 SIM 页面语义确认后作为套餐阈值参考。
- 概览“本地清零”安全地重置终端显示基线、整机计数基线与本地历史，不修改设备 SIM 统计或网络配置；清零后终端合计从零继续增长。
- 信号、CPU、模块温度改为可悬停的环形指标；信号按优秀=绿、良好=蓝、一般=黄、较差=红分级。
- 信号分析页改为 RSRP 质量分区、RSRQ 与 SINR 同时间轴的三段式交互图，悬停显示时间、三项指标、连接状态、Band、PCI 与 ARFCN。
- 概览中的月流量、信号、CPU、内存、温度、流量柱状图和实时速率图均提供悬停详情或点击明细。
- 磁贴改为从标题区域拖动；拖动中只显示被拖磁贴和落点高亮，其他磁贴保持稳定，松开后一次性排序并落盘。
- 修复编辑状态直接关闭软件时磁贴顺序可能未保存的问题；关闭、点击“完成”和正常松手均走同一持久化路径。
- 保存设置前会清理异常的 `NaN/Infinity` 数值，避免一个无效界面数值导致整份配置无法写入。
- 锁频页改为高对比度小区卡片，显示质量等级、信号条、推荐小区、推荐理由和 3GPP 指标依据。
- 2.4 GHz 与 5 GHz Wi-Fi 分别提供启用、SSID、密码、信道和带宽修改。
- 接入终端页突出显示每台终端的累计用量，详情支持手动累计值、清零和恢复原始设备值，并实时刷新。
- 已移除重复的独立“流量统计”导航，统计保留在概览磁贴中。
- 短信页重新排版，并在无消息时显示可用条件、当前判断和隐私说明。
- 套餐值和终端手动累计值属于本机显示配置；终端值以“设备计数器 − 本机基线”继续实时增长。

### 真实只读功能

- 设备型号、NROS 固件、硬件、主板、MAC、运行时间。
- 蜂窝模式、运营商、SIM 运营商、注册/连接状态、APN、IMEI、IMSI、ICCID。
- RSRP、RSRQ、SINR、CQI、PCI、NCI/ECI、TAC、频段、带宽、NR-ARFCN/EARFCN。
- WAN IPv4/IPv6、DNS、WAN 连接时间。
- 双频 Wi-Fi 的 SSID、加密方式、信道、频率、在线数。
- 当前接入终端、IP、MAC、主机名、RSSI、关联时间、设备侧计数器。
- 设备流量计数器、本机采样上下行速率、小时/日历史、日/月流量阈值。
- 设备上报邻居小区和当前 Band & Cell Lock 状态。
- SMS 服务、容量、未读数和消息列表。
- 本机网络质量检测、通信日志、终端别名、CSV 导出、设置备份。

### 已实现但未执行真机写入验证

以下操作都使用原厂真实 CBI 表单或专用接口，实施两次确认，并在写入后回读；没有把它们标成“真机写验证通过”：

- 邻居小区锁定/解锁（`earfcn_lock`）。
- SIM1/SIM2 手动切换与恢复自动选卡。
- 网络制式：自动 / 仅 5G SA / 仅 4G。
- Wi-Fi SSID、密码、信道修改。
- 已启用短信服务时的短信发送。
- 设备重启。

回读失败、连接中断或设备响应不明确时，界面标记为“结果待确认”，不会只凭 HTTP 200 显示成功。

### 明确未开放

- 固件升级：未取得受信任固件来源、版本校验与失败恢复方案。
- 配置恢复/恢复出厂：高风险且当前任务未授权。
- MAC 黑白名单、WPS、自定义防火墙、DMZ、UPnP、端口转发：专业版页面存在，但本版本尚未完成逐项载荷验证。
- 电池趋势：当前 C2000 返回 `percent=-1`，JSON 接口未提供电池字段，因此不显示电池模块。
- 终端设备侧“本月/近 3 日”计数器：C2000 JSON 接口未提供，使用“—”和说明，不按整机流量拆分猜测。

## 源码构建

需要 Windows 10/11 x64 和 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet build src\NRadioManager\NRadioManager.csproj -c Release
```

生成 Windows x64 发布包：

```powershell
dotnet publish src\NRadioManager\NRadioManager.csproj `
  -c Release -r win-x64 --self-contained false -o dist
```

运行环境需要 .NET 10 Desktop Runtime。项目不增加第三方 NuGet 依赖。

## 测试

离线协议与错误处理：

```powershell
dotnet run --project tests\NRadioManager.SelfTest\SelfTest.csproj -c Release -- --offline
```

C2000 Pro 真机只读回归（密码通过当前进程环境注入）：

```powershell
$sec = Read-Host "Device password" -AsSecureString
$env:NRADIO_TEST_PASSWORD = [System.Net.NetworkCredential]::new("", $sec).Password
dotnet run --project tests\NRadioManager.SelfTest\SelfTest.csproj -c Release -- `
  http://192.168.66.1 root --c2000
Remove-Item Env:NRADIO_TEST_PASSWORD
```

UI 自检：

```powershell
dotnet run --project src\NRadioManager\NRadioManager.csproj -c Release -- --ui-smoke
dotnet run --project src\NRadioManager\NRadioManager.csproj -c Release -- --ui-smoke --live
```

联机 UI 自检同样从 `NRADIO_TEST_PASSWORD` 读取密码，不把密码写入测试报告。

## 数据与安全

- 所有 HTTP 局域网访问关闭系统代理，避免 Clash 等代理把本地地址带出直连网段。
- 默认超时 10 秒；状态轮询有重入保护，上一轮未完成时不会堆积新请求。
- Cookie 失效只重建会话一次；写请求会话失效时不会自动重放，避免重复提交。
- 配置写入共用一个互斥门，禁止并发写。
- 日志脱敏密码、Cookie、Authorization、Token、短信正文和电话号码。
- 密码只在用户明确选择“记住”时用 DPAPI 保存。
- 高风险操作二次确认，写后回读；不确定结果按失败/待确认处理。

详细证据和限制见：

- [C2000 Pro 审计与能力矩阵](docs/08-C2000Pro审计与能力矩阵.md)
- [C2000 Pro 适配架构与真机验证](docs/09-C2000Pro适配架构与验证.md)
- [历史 NRadio 接口分析](docs/01-原厂接口逆向分析.md)
