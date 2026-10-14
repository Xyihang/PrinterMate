# PrinterMate 打印机工具

Windows 桌面端打印机助手:**发现打印机 → 判断驱动来源 → 装好驱动 → 打一张测试页**。
面向的主场景是**USB 线直连**,局域网 / IPP 作为补充路径。

界面全中文。目标:让使用者不必自己去网上翻找驱动。

---

## ⚠️ 请先读这一段:本仓库不包含任何厂商驱动

> **本仓库不含任何打印机厂商的驱动二进制文件**(INF / CAT / DLL / CAB / SYS)。
> 发布产物中同样不含。

仓库里只有一份 **元数据清单** —— [`src/PrinterMate.Catalog/Data/drivers.json`](src/PrinterMate.Catalog/Data/drivers.json),
约 32 KB,记录 30 个品牌的**名称、型号特征、预期驱动文件名**。它的作用相当于一本"电话簿":
告诉你"这台机器该配哪个通用驱动、驱动包应该放在哪里"。**它不是驱动,也装不出驱动。**

任何驱动文件的获取与安装,都由**使用者本人**完成,并**自行承担相应的许可义务**:

1. 通过 Microsoft Update Catalog 或打印机厂商官方网站获取驱动;
2. 自行阅读并接受该厂商的最终用户许可协议(EULA);
3. 本软件在安装前会校验官方公布的 SHA256 与 Authenticode 数字签名 ——
   但**签名有效不等于授权使用**,授权范围以厂商许可条款为准。

本软件**不重新打包、不修改**任何厂商驱动文件;拿到的就是厂商原始包。

**分发提醒**:若你分发本软件(或其修改版),**不得连同任何厂商驱动文件一起分发**。
本软件把驱动存放在 `%LOCALAPPDATA%\PrinterMate`(用户级目录)而非程序目录,
以从结构上避免误分发。

---

## 功能

| 功能 | 说明 |
|---|---|
| 设备发现 | USB(PnP)、网络(WSD/IPP)、本地队列、虚拟打印机 |
| 驱动决策链 | **L0 免驱**(Microsoft IPP Class Driver)→ **L1 内置驱动清单**→ **L2 在线补齐** |
| 一键连接 | 认设备 → 选驱动来源 → 提权安装 → 建队列,全过程可解释 |
| 驱动管理 | 内置库状态、各品牌可用性统计、人工指定驱动包、批量导入文件夹 |
| 在线补齐 | 从 Microsoft Update Catalog 检索官方驱动包,下载并三重校验后入库 |
| 第三方驱动站检索 | **只打开官方搜索页**,不下载、不安装(风险由使用者自担) |
| 打印与测试 | 打印系统测试页、自绘测试页、任意文件打印、队列状态与诊断报告 |
| 新手必读 | 内置教程页,讲清"驱动从哪来""各状态列什么意思" |

---

## 安全模型

这是本项目最花功夫的部分。**所有安全校验都在本机完成,不存在"跳过校验"的开关。**

```
来源白名单 → HTTPS 强制 → 逐跳重定向复验 → 大小上限(800 MB)
        → SHA256 校验(优先采用官方公布值)
        → Authenticode 数字签名校验 → 审计日志留痕
```

- **来源白名单** `DownloadSourceAllowlist`:按**域名标签边界**匹配,防止 `evil-microsoft.com` 之类伪装。
- **官方哈希优先** `MicrosoftUpdateCatalogClient`:直接采用 Microsoft Update Catalog 页面公布的 SHA256/SHA1,
  **不依赖 TOFU**。没有公布哈希的微软来源**一律拒绝**(不做 TOFU 兜底)。
- **信任锚点三方一致**:厂商官方域名 → 域名品牌 = 签名主体品牌 = 预期品牌。
  微软官方更新服务走"微软域名 + 微软公布的 SHA256 + 可信证书链",厂商身份延后到包内 `.cat` 上核验。
- **提权隔离**:主程序以 `asInvoker` 运行,**不以管理员身份运行**;
  仅安装那一步通过 `ShellExecute(verb=runas)` 拉起 `PrinterMate.Elevated.exe`,
  通过 `%TEMP%\PrinterMate` 下的 JSON 请求/响应通信,并且**提权进程会独立重新校验一次驱动包**。
- **审计日志** `AuditLog`:JSONL 格式,记录每一次下载、校验、安装、许可确认。
- **分发策略** `DistributionPolicy`:清单中 `redistributable` 是发行方的红线,**默认全部关闭**;
  使用者可在本机显式开启某品牌的内置驱动安装(仅影响"是否允许从本地驱动库安装",
  **不会放宽** SHA256 与签名要求)。

---

## 项目结构

```
PrinterMate.slnx                解决方案(.slnx 格式)
src/
  PrinterMate.Interop/          Win32 / SetupAPI / 打印后台接口
  PrinterMate.Catalog/          驱动清单、匹配引擎、分发策略、驱动库读写
  PrinterMate.Security/         来源白名单、安全下载器、签名策略、审计日志
  PrinterMate.Printing/         打印后台队列操作、IPP 客户端
  PrinterMate.Core/             设备发现、驱动决策链、安装状态机、在线补齐
  PrinterMate.Elevated/         按需提权的安装辅助进程
  PrinterMate.App/              WPF 界面(MVVM,手写 ObservableObject/RelayCommand)
tests/
  PrinterMate.UnitTests/        单元测试
  PrinterMate.IntegrationTests/ 真机集成测试(含联网检索、真实驱动包解包)
tools/
  Import-DriverPackage.ps1      命令行导入厂商驱动包
```

### 技术栈

- .NET 8(`net8.0-windows`)、C# 12、WPF
- 依赖:[SharpIppNext](https://github.com/danielklecha/SharpIppNext) 5.0.0(**MIT**)
- MVVM 为手写实现,未引入额外 MVVM 框架

---

## 构建与运行

```powershell
# 还原与构建
dotnet build PrinterMate.slnx

# 单元测试
dotnet test tests/PrinterMate.UnitTests/PrinterMate.UnitTests.csproj

# 真机集成测试(会读取本机打印机、审计日志,并可能联网检索)
dotnet test tests/PrinterMate.IntegrationTests/PrinterMate.IntegrationTests.csproj

# 运行
dotnet run --project src/PrinterMate.App
```

### 打包为单文件便携版

```powershell
dotnet publish src/PrinterMate.Elevated/PrinterMate.Elevated.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist/elevated

dotnet publish src/PrinterMate.App/PrinterMate.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
```

`dist` 目录需整体分发:`elevated\PrinterMate.Elevated.exe` **不能缺失**,按需提权靠它。

### 运行时目录

```
%LOCALAPPDATA%\PrinterMate\
  Catalog\drivers.json        本机驱动清单(与随包清单合并)
  Drivers\<品牌>\<标识>\      已导入的驱动包
  Downloads\                  在线补齐下载的驱动包
  distribution-policy.json    本机分发授权开关
  logs\                       审计日志(JSONL)与界面日志
```

清单加载规则:**随包清单为基线,本机清单按「品牌 + PDL」覆盖同键条目**。
这样程序升级后新增的品牌/型号一定会生效,同时不会丢掉使用者自己导入的驱动包。

### 可选依赖

解包 `.cab` / `.zip` / `.exe` / `.msi` 需要 **7-Zip**(可选);
未安装时回落到系统自带的 `expand.exe`。程序**从不静默执行**厂商安装器。

---

## 关于 Microsoft Update Catalog

在线补齐功能通过解析 Microsoft Update Catalog 的公开页面获取下载直链。
**该站点没有公开 API**,使用者需自行遵守其使用条款。
本项目的设计取向是**逐品牌、由使用者显式确认许可条款后下载**,而非静默批量抓取。

---

## 许可证

源代码以 **MIT** 许可发布,见 [LICENSE](LICENSE)。

**该许可仅适用于本仓库中的源代码,不适用于任何第三方打印机驱动文件**
(见文首声明)。
