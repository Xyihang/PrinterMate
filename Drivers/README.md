# Drivers — 内置通用驱动(UPD)库

本目录是 PrinterMate「自带驱动」能力的实体。**只放各品牌的通用驱动(UPD),绝不放全型号驱动。**

## 目录约定

```
Drivers/
  HP/upd-pcl6/            hpcu*.inf, hpcu*.cat, ...
  HP/upd-ps/
  Canon/generic-plus-pcl6/
  Epson/eup/
  Brother/upd/
  Kyocera/kx-udp/
  Ricoh/upd-pcl6/
  Lexmark/upd-v2/
  Fujifilm/fx-upd/
  KonicaMinolta/upd-pcl6/
  Sharp/upd/
  Toshiba/upd/
  OKI/upd/
  Pantum/upd/
  Lenovo/upd/
  Deli/upd/
  Jolimark/upd/
```

每个子目录必须包含完整的驱动包:`*.inf` + `*.cat` + 依赖文件。
`Catalog/drivers.json` 中的 `infRelPath` 相对**仓库根目录**(即本目录的上一级)。

## 当前状态

本仓库**不包含任何厂商驱动二进制**(受再分发授权约束)。因此 `Catalog/drivers.json` 中:

- `Microsoft` 的 `ClassDriver` 条目为系统内置,永远可用(免驱通道 L0);
- 各厂商条目的 `sha256` 为空、`redistributable` 为 `false`。

程序对这两种情况的行为是明确的:

| 情况 | 程序行为 |
|---|---|
| `sha256` 为空 | 该架构的驱动包视为**未入库**,匹配引擎自动降级到 L2 在线源 |
| `redistributable=false` | **禁止**从本地磁盘安装,给出中文原因,自动降级到 L2 |

> 这样设计的目的:在没有完成授权核对之前,程序不会分发也不会安装厂商驱动;
> 一旦驱动包入库且授权确认,同一个二进制立刻可以在**完全离线**的环境下工作。

## 导入驱动包

```powershell
# 1. 从厂商官方网站下载通用驱动(例如 HP Universal Print Driver for Windows PCL6)
# 2. 导入到驱动库
.\tools\Import-DriverPackage.ps1 `
    -Brand HP `
    -Family "Universal Print Driver" `
    -Pdl PCL6 `
    -Source C:\Download\hp-upd-pcl6 `
    -ConfirmRedistribution
```

导入脚本会:

1. 定位 `.inf` 与 `.cat`;
2. 用 `Get-AuthenticodeSignature` 校验 **`.cat`** 的 Authenticode 签名(签名在目录文件上,不在 INF 上);
3. 解析 INF 的 `[Models]` 段列出可用驱动名;
4. 计算 INF 的 SHA256 并回填 `Catalog/drivers.json`;
5. 复制文件到 `Drivers/<Brand>/<slug>/`;
6. **只有在显式传入 `-ConfirmRedistribution` 时才把 `redistributable` 置为 `true`**。

## 授权红线

向第三方分发厂商驱动前,必须逐一核对各厂商的再分发许可。未确认的品牌:

- 保持 `redistributable: false`;
- 程序会在 UI 中说明「该品牌驱动受授权限制,改用官方在线源」;
- 用户仍可通过 L2 在线通道正常使用,只是需要联网。

## 校验规则(为什么 INF 的 SHA256 就够了)

- **完整性**:INF 的 SHA256 与清单比对,可发现驱动包被替换或篡改;
- **真实性**:`.cat` 目录文件经 `WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)` 校验,
  保证包内所有文件与签名主体一致;
- **主体一致性**:签名主体必须与清单中的 `signerSubject` 匹配。

三者缺一不可,任一失败都会拒绝安装并写入审计日志。
