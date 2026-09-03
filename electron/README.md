# 利亚方舟海螺云 Electron 网络诊断工具

这是与现有 `.NET 8 WinForms` 版本并行维护的跨平台实现，原有 `src/LYFZ.NetDiag` 未被替换。

## 支持范围

- 现代版：Windows 10/11、macOS（Intel x64 与 Apple Silicon arm64）
- 遗留版：Windows 7 SP1/8/8.1，固定使用 Electron 22，仅为存量设备兼容，不再获得 Chromium 安全更新

Windows 和 macOS 必须分别打包；应用自带 Electron/Node.js，用户无需安装 Node.js 或 .NET。

## 检测内容

- 网络接口、地址、DNS、默认网关、系统代理和公网出口IP
- 系统DNS及指定DNS服务器查询（含TTL）
- TCP 443、TLS/SNI、证书、HTTPS状态码和CDN追踪响应头
- 当前DNS节点与配置健康IP对照
- 历史异常CDN节点 `113.215.230.101` 识别
- 对每个诊断域名执行内置原生ICMP Ping与最多15跳路由跟踪，不调用系统命令
- 固定保留13个默认域名，并允许用户临时追加最多20个域名（自动校验和去重）
- 快速诊断一次、连续5分钟、连续10分钟
- TXT原始日志、CSV时间序列和离线HTML分析报告
- `api.lyfz.net` 根路径5xx按网络连通正常处理

## 安全设计

- 界面只加载随应用打包的本地文件
- `contextIsolation` 与 Chromium sandbox 开启，`nodeIntegration` 关闭
- 渲染进程只能通过最小化、参数校验后的IPC接口启动/停止检测和打开本次报告
- 不执行 `ping`、`tracert`、`traceroute`、`nslookup`、`curl`、PowerShell或Shell

## 开发

```powershell
pnpm install
pnpm test
pnpm start
```

无界面快速测试：

```powershell
pnpm start:auto -- --output=D:\temp\lyfz-electron-test --domains=example.com,example.net
```

## 原生探测器

Windows需要在对应架构的 Visual Studio Developer PowerShell 中运行：

```powershell
.\scripts\build-native-windows.ps1 -Architecture x64
.\scripts\build-native-windows.ps1 -Architecture x86
```

macOS运行：

```bash
./scripts/build-native-macos.sh
```

输出必须位于 `native/bin/<platform>-<arch>/`，打包脚本会把它作为应用资源一起发布。

## 打包

Windows开发机在仓库根目录直接双击：

```text
build-electron-release.cmd
```

也可以在 PowerShell 中运行（如果依赖已经安装，可附加 `-SkipInstall`）：

```powershell
.\build-electron-release.ps1
```

脚本会自动检查 Node.js、pnpm、x64/x86 原生探测器，安装锁定依赖，运行测试，然后生成 Windows 10/11 x64、Windows 7/8 x64 和 Windows 7/8 x86 三个客户分发 ZIP。最终文件位于 `release/final-<package.json版本号>/`，同时生成 `SHA256SUMS.txt`。

以后发布新版本时，只需修改 `package.json` 的 `version`，再运行上述脚本。报告、文件名、Win7兼容构建和客户说明会读取同一个版本号。如果修改了 `native/windows/netdiag_native.c`，需要先在对应 Visual Studio Developer PowerShell 中重新编译 x64 与 x86 原生探测器。

Windows打包开发环境要求 Node.js 20/22 LTS 和 pnpm 11。客户运行打包后的工具时仍然不需要安装 Node.js、pnpm 或 .NET。

macOS应在macOS构建机上执行：

```bash
sh ./scripts/build-release-macos.sh
```

脚本会生成 Intel x64 与 Apple Silicon arm64 产物、客户说明和 SHA256 校验文件，输出到 `release/final-macos-<版本号>/`。也可以手动触发仓库提供的 GitHub Actions 工作流 `.github/workflows/build-electron-macos.yml`。

底层单独打包命令仍然保留：

```powershell
pnpm dist:win
pnpm dist:legacy:win
pnpm dist:mac
```

本地 Windows 构建使用安装包内的 Electron 目录，macOS 使用独立配置按目标架构下载对应运行时，避免混用架构。

正式分发前，Windows产物应使用公司代码签名证书签名；macOS产物应完成Developer ID签名和Apple公证。
