# LYFZ NetDiag

利亚方舟海螺云网络故障现场采集工具。面向客户门店的 Windows 免安装诊断程序，发布为 .NET 8 self-contained 单文件 EXE。

## 两套实现

- 原有 .NET 8 WinForms 版本仍位于 `src/LYFZ.NetDiag`，保留现状。
- 新增 Electron 跨平台版本位于 `electron/`，支持 Windows 10/11、Windows 7 兼容通道和 macOS；使用方法和构建说明见 `electron/README.md`。

## 检测内容

- 网络接口、内网地址、网关、DNS、系统代理和公网出口地址
- 系统 DNS 结果，以及网络接口 DNS、`223.5.5.5`、`119.29.29.29` 的原生 UDP DNS 查询
- 每个解析节点的 Ping、TCP 443、TLS/SNI、证书和强制节点 HTTP 测试
- 使用系统 DNS/系统代理的 HTTP 对照测试
- 对每个诊断域名执行内置 ICMP 路由跟踪（最多15跳、每跳3次探测）
- 对利亚方舟域名执行配置健康 IP 对照，并识别历史异常节点 `113.215.230.101`
- 覆盖全部利亚方舟业务域名及腾讯验证码、微信开放平台依赖
- 默认保留全部内置域名，并允许临时增加最多20个额外诊断域名
- 快速诊断一次、连续1分钟、连续5分钟和连续10分钟四种模式
- 自有域名每10秒、第三方域名每60秒的CSV时间序列
- DNS IP变化、状态变化、故障恢复事件及变化时自动深度复测
- 默认网关基线、公网出口IP变化和网卡错误/丢弃计数差值
- 基于DNS、系统代理、CDN/直连域名、健康对照节点和网关状态的自动故障归因
- `EagleId`、`Ali-Swift-*`、`X-Swift-*`、请求ID等CDN追踪响应头
- 自动生成可离线打开、适合阅读和打印的HTML分析报告，并保留完整TXT原始证据

工具不调用 `curl`、`nslookup`、`tracert` 或 PowerShell 命令。

## 开发运行

```powershell
dotnet run --project .\src\LYFZ.NetDiag\LYFZ.NetDiag.csproj
```

无界面自动测试并把日志写到指定目录：

```powershell
dotnet run --project .\src\LYFZ.NetDiag\LYFZ.NetDiag.csproj -- --auto --output .\test-logs
```

增加临时诊断域名（可用逗号、分号、空格或换行分隔，内置域名不会被替换）：

```powershell
dotnet run --project .\src\LYFZ.NetDiag\LYFZ.NetDiag.csproj -- --auto --output .\test-logs --domains "example.com,https://service.example.com/path"
```

执行连续监测（`--monitor-minutes` 只接受 `1`、`5` 或 `10`）：

```powershell
dotnet run --project .\src\LYFZ.NetDiag\LYFZ.NetDiag.csproj -- --auto --output .\test-logs --monitor-minutes 5
```

## 发布 .NET 8 版本

```powershell
.\build-release.ps1
```

脚本生成 x64/x86 两个 self-contained 单文件版本和对应 ZIP。正式发给客户前，建议使用公司的代码签名证书为 EXE 签名，并向客户提供 ZIP 的 SHA-256。

## 发布 Electron 版本

Windows开发机直接双击根目录的 `build-electron-release.cmd`，或运行：

```powershell
.\build-electron-release.ps1
```

脚本会自动检查构建环境和原生探测器、安装依赖、运行测试，并生成 Windows 10/11 x64、Windows 7/8 x64、Windows 7/8 x86 三个客户分发 ZIP 及 SHA256 校验文件。输出位于 `electron/release/final-<版本号>/`。macOS请在macOS构建机运行 `sh electron/scripts/build-release-macos.sh`。
