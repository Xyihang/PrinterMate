# PrinterMate 需求规格文档

- 版本:0.2.0(驱动库中心化修订)
- 日期:2026-10-13
- 技术栈:C# / .NET 8 / WPF / Win32(winspool.drv、SetupAPI、WinVerifyTrust)/ PrintManagement / pnputil / SharpIppNext

---

## 1. 概述

### 1.1 背景
用户在陌生机器上连接打印机时,最大障碍不是"找不到设备",而是"没有驱动":
型号五花八门、厂商驱动站难找、第三方"驱动站"充满捆绑与病毒风险。

### 1.2 定位(最高优先级,不可偏离)
**本软件自带多品牌打印机通用驱动(UPD),用户无需上网找驱动即可连接打印机并打印。**
一切功能围绕「驱动供给能力 + 连接成功率」展开;打印测试页是验证手段,不是产品重点。

### 1.3 范围
| 范围 | 内容 |
|---|---|
| Must Have | 驱动库(内置 UPD)、型号→驱动匹配引擎、L0/L1/L2 分层供给与降级、驱动安装状态机、设备发现、测试页验证 |
| Should Have | 在线补齐(L2)、队列管理、状态监控、诊断报告、审计日志 |
| Won't Have | 全型号驱动打包、非官方源下载、静默重启、常驻管理员、绕过 UAC、修改系统打印安全策略 |

---

## 2. 核心设计:驱动供给分层

| 层 | 名称 | 触发条件 | 是否需要安装 | 目标耗时 |
|---|---|---|---|---|
| **L0** | 系统免驱 | 设备为 Mopria / IPP Everywhere | 否(用 Microsoft IPP Class Driver 直接建队列) | < 30s |
| **L1** | 内置 UPD | 品牌命中内置驱动库且 `redistributable: true` | 本地 INF 安装,全程离线 | ≤ 60s |
| **L2** | 在线补齐 | L0/L1 均未命中 | 白名单官方源下载 + 三重校验后安装 | 依赖下载 |

逐级降级:任一成功即停止,失败必须给出**明确原因 + 可操作建议**,不得静默跳过。

### 2.1 品牌 → 通用驱动映射(内置库目标)
| 品牌 | 通用驱动 | 备注 |
|---|---|---|
| HP | HP Universal Printing PCL 6 / PS | Samsung 打印业务并入 HP,优先复用 |
| Canon | Canon Generic Plus PCL6 / UFR II | |
| Epson | EPSON Universal Print Driver (EUP) | |
| Brother | Brother Universal Printer Driver | |
| Kyocera | Kyocera Universal Print Driver (KX) | |
| Ricoh | PCL6 Driver for Universal Print | |
| Lexmark | Lexmark Universal v2 | |
| Fujifilm(Fuji Xerox) | FX Universal Print Driver | |
| Konica Minolta | Universal PCL / PS Driver | |
| Sharp | SHARP Universal Print Driver | |
| Toshiba | TOSHIBA Universal Print Driver | |
| OKI | OKI Universal Driver | |
| 奔图 Pantum / 联想 Lenovo / 得力 | 优先复用底层品牌 UPD | 贴牌机型,匹配不到走 L2 |

### 2.2 体积与授权
- 每品牌按 30~40MB 估算,12 品牌 ≈ 400~600MB。**驱动库与主程序分离存储**。
- 首次安装可选「精简(常用 6 品牌)/ 完整(全部品牌)」。
- **授权红线**:分发前逐一核对厂商再分发授权;不允许的品牌在清单标 `redistributable: false`,
  该品牌自动降级为 L2 在线源,并且**代码层面禁止从本地磁盘安装该品牌驱动**。

---

## 3. 数据规格:驱动库清单 `Catalog/drivers.json`

```json
{
  "schemaVersion": "1.0",
  "libraryVersion": "2026.10.1",
  "entries": [
    {
      "brand": "HP",
      "family": "Universal Print Driver",
      "pdl": "PCL6",
      "driverName": "HP Universal Printing PCL 6",
      "infRelPath": "Drivers/HP/upd-pcl6/hpcu*.inf",
      "arch": ["x64", "arm64"],
      "os": ["win10", "win11"],
      "version": "7.x",
      "signerSubject": "CN=HP Inc, O=HP Inc, L=Palo Alto, S=California, C=US",
      "redistributable": true,
      "modelPatterns": ["LaserJet*", "OfficeJet*", "PageWide*", "DesignJet*"],
      "hardwareIdPatterns": ["USBPRINT\\HP*", "WSDPRINT\\*HP*"],
      "sha256": { "x64": "", "arm64": "" },
      "priority": 100
    }
  ]
}
```

- `pdl`:`PCL6 | PS | GDI | ClassDriver`。同品牌同时存在 PCL6/PS 时默认 PCL6。
- `driverName` 必须与 INF `[Models]` 段名称**逐字一致**;由 `DriverLibrary` 在导入时解析确认并回填。
- `sha256` 按架构分列;为空表示该架构驱动包尚未入库(匹配时按不可用处理)。

---

## 4. 功能规格

### 4.1 型号 → 驱动匹配引擎
- **输入**:型号字符串、厂商、连接方式、PnP `HardwareID`/`CompatibleID`、是否 Mopria。
- **处理**:
  1. 归一化型号(去空格、去括号后缀、统一大小写)。
  2. Mopria/IPP → 直接输出 L0。
  3. `hardwareIdPatterns` 精确匹配(最高优先级);否则 `modelPatterns` 品牌模糊匹配。
  4. 同品牌 PCL6/PS 并存 → 默认 PCL6;设备仅支持 PostScript → PS。
  5. 命中内置且 `redistributable` → L1;否则 → L2。
  6. 输出 `DriverMatch { driverName, source(builtin|online|classDriver), infPath, confidence, reason }`。
  7. 多个候选或置信度低 → UI 列出候选供人工选择。
- **异常**:型号无法识别 / 品牌受授权限制 / WPP 模式禁止安装 → 给出对应中文原因。

### 4.2 驱动安装状态机
| 步骤 | 操作 | 失败分支 |
|---|---|---|
| 预检 | 检测 WPP 模式、管理员权限 | WPP → 禁用 L1/L2,降级 L0 并提示 |
| 1 | SHA256 比对 + WinVerifyTrust Authenticode | 任一失败 → 中止、删除临时文件、审计记录 |
| 2 | `pnputil /add-driver "<inf>" /install` | `0xE0000247` 签名问题 / 已存在 / INF 不匹配 → 分别提示 |
| 3 | `Add-PrinterDriver -Name -InfPath` | 名称不符 → `Get-PrinterDriver` 回读实际名重试 |
| 4 | 创建端口(IPP/RAW/USB/共享) | 端口已占用 → 复用或改名 |
| 5 | `Add-Printer -Name -DriverName -PortName` | 同名队列已存在 → 复用或改名 |
| 6 | `Get-Printer` + `Get-PrinterDriver` 回读校验 | 不一致 → 报错并回滚 |
| 7 | 打印验证(测试页) | 失败 → 保留日志,提示检查链路 |
- 需要重启时使用 `/reboot` 并明确告知,**禁止静默重启**。
- 全流程写入审计日志:时间、来源、SHA256、签名主体、每步命令与返回码、结果。

### 4.3 设备发现
- 本机:PowerShell `Get-Printer`、`Get-PnpDevice -Class Printer`;Win32 `EnumPrinters` 兜底。
- 局域网:SNMP v2c 广播(161)、mDNS(`_ipp._tcp`/`_printer._tcp`)、WS-Discovery、IPP(631)。
- 手动:IP / 主机名 + 协议(IPP / LPD / RAW / 共享路径)。
- 列表字段:名称、品牌、型号、连接方式、驱动状态(已装/需装/免驱)。
- 性能:局域网发现 10s 内出结果。

### 4.4 打印与测试页
- 系统测试页:`rundll32 printui.dll,PrintUIEntry /k /n "<打印机名>"`。
- 自绘测试页:Win32 Spooler 链路(OpenPrinter → StartDocPrinter → StartPagePrinter →
  WritePrinter → EndPagePrinter → EndDocPrinter → ClosePrinter),内容含打印机名/IP/驱动/端口/
  PDL/时间戳 + 色彩条 + 对齐网格。
- IPP 直发(免驱):SharpIppNext `Print-Job`,`ipp://<ip>:631/ipp/print`。
- 文件打印:PDF(系统 PDF 渲染)、图片、纯文本;不支持类型明确报错。
- RAW:允许投递 PCL/RAW 字节流到指定队列。

### 4.5 队列与状态
- `Get-PrintJob` / `EnumJobs`;操作:暂停、恢复、取消、清空。
- 状态映射:在线/脱机/缺纸/卡纸/低墨/错误 + 可操作建议。
- 诊断报告:设备信息 + 匹配驱动 + 端口 + 队列状态 + 错误日志(JSON/TXT)。

### 4.6 安全
- 白名单:仅 Microsoft Update Catalog 与厂商官方 HTTPS 域名。
- 下载后三重校验:HTTPS 证书 → SHA256 → WinVerifyTrust(Authenticode)。
- 明示风险 + 二次确认;提权走独立辅助进程,主程序不常驻管理员。
- 审计日志本地留存,可导出。

---

## 5. 非功能需求
| 项 | 要求 |
|---|---|
| 离线可用 | 内置驱动库覆盖范围内**完全离线**工作,零外网请求 |
| 性能 | 局域网发现 ≤ 10s;驱动安装 ≤ 60s(不含下载) |
| 可维护 | 驱动库与清单可独立更新,不依赖重装主程序 |
| 兼容 | Windows 10 1809+ / 11;x64 与 ARM64 双架构 |
| UI | WPF,**不追求视觉设计**,要求清晰、中文、错误可操作、不崩溃 |
| 稳定性 | 任何未处理异常不得导致程序退出 |

---

## 6. 工程结构
```
PrinterMate.sln
/src/PrinterMate.Interop     Win32 P/Invoke(winspool、WinVerifyTrust)
/src/PrinterMate.Catalog     drivers.json 与驱动库(DriverLibrary、匹配候选)
/src/PrinterMate.Security    白名单、SHA256、WinVerifyTrust 封装、审计、信任账本
/src/PrinterMate.Printing    Spooler、IPP、测试页渲染、文件打印
/src/PrinterMate.Core        PowerShell/进程执行器、设备发现、匹配引擎、安装状态机、编排
/src/PrinterMate.App         WPF 界面
/src/PrinterMate.Elevated    按需提权的安装辅助进程
/Drivers/<Brand>/...         随包分发的 UPD 驱动二进制(受授权约束)
/tests/UnitTests, /tests/IntegrationTests
```

---

## 7. 里程碑(每步独立验收)
1. 设备发现:本机队列 + 局域网 SNMP/mDNS,列表展示型号/品牌/连接方式。
2. 免驱通道 L0:Mopria/IPP 设备用 IPP Class Driver 建队列 + 系统测试页。
3. 匹配引擎:型号→品牌→UPD 匹配 + 置信度 + 候选人工选择。
4. 内置驱动安装 L1:≥6 常用品牌 UPD,跑通 `pnputil → Add-PrinterDriver → Add-Printer` 全链路。
5. 自绘测试页 + 文件打印(PDF/图片/文本)。
6. 在线补齐 L2:白名单官方源下载 + 签名校验 + 安装。
7. 队列管理、状态监控、诊断报告、审计日志。
8. 安全加固与降级:WPP 检测、提权隔离、`redistributable` 开关、异常场景全覆盖。

---

## 8. 验收标准
- [ ] 断网环境下,内置品牌机型从发现到打印测试页 ≤ 3 次点击,全程无外网请求。
- [ ] 非内置机型可从白名单官方源检索、校验、安装,全程可追溯。
- [ ] 非白名单 / 哈希不符 / 签名无效的驱动一律拒绝,日志可定位。
- [ ] 开启 Windows Protected Print Mode 时不崩溃,自动降级 L0 并提示。
- [ ] 未处理异常不导致退出;错误提示均含可操作建议。
- [ ] x64 与 ARM64 均可运行。

---

## 9. 边界与异常场景(必须覆盖)
型号无法识别 / 品牌授权禁用内置 / INF 与架构版本不匹配 / 驱动已存在(更新-复用) /
端口占用 / 同名队列已存在(改名-复用) / 打印机离线无响应 / IPP 端口被防火墙拦截 /
WPP 模式 / 域策略禁止安装驱动 / 用户拒绝提权或提权失败 / 下载中断 / 哈希不一致 / 证书过期。

---

## 10. 风险与依赖
| 风险 | 说明 | 应对 |
|---|---|---|
| **UPD 再分发授权** | 多数厂商许可禁止随第三方软件分发驱动 | 清单 `redistributable` 开关;不允许的品牌强制走 L2;随包仅放已确认授权的驱动 |
| **驱动包体积** | 12 品牌 ≈ 400~600MB | 驱动库与主程序分离,安装时选择精简/完整 |
| **驱动二进制无法在开发环境取得** | 本仓库不包含厂商二进制 | `/Drivers` 提供目录结构与导入工具;代码在"库为空"时明确提示并自动降级 L2 |
| WPP 策略推进 | 微软逐步默认启用 WPP | L0 通道优先;WPP 下禁用 L1/L2 并提示 |
