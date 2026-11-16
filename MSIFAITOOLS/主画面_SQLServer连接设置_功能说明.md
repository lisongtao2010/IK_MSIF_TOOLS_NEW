# 主画面 · SQL Server 连接设置（① genpin / ② MSIF）功能说明

> 所属项目：`MSIFAITOOLS`（.NET Framework 4.8 / WinForms / AnyCPU）
> 源文件：`Form1.cs`、`Form1.Designer.cs`（主画面按钮）、
> `MemberMappingDbSettingsForm.cs`、`MemberMappingDbSettingsForm.Designer.cs`（连接设置画面）、
> `Services/MemberMappingDbSettings.cs`（解析 / 保存）
> 相关：`Services/MemberNameReplacementRepository.cs`（① 置换表）、
> `Services/BuhinBuzaiMappingRepository.cs`（② 部材不材 mapping）、
> `Services/MemberNameReplacementCatalog.cs`（启动预读）
> 更新日期：2026-11-16

---

## 1. 一句话概要

主画面标题栏第二行新增两个按钮 **接続① genpin** / **接続② MSIF**：这两个按钮分别对应工具用到的
两个 SQL Server 连接，用户可以在画面上改接続先 / 端口 / DB 名 / ID / 密码，**两个连接各存一份，互不影响**，
保存后立即生效（不用改 `App.config`，也不用重启程序）。

---

## 2. 两个连接分别给谁用

| 按钮 | 具名连接 | 默认接続先 | 用到的表 | 影响的功能 |
|------|----------|------------|----------|------------|
| 接続① genpin | `connJP1` | `10.103.126.208` / `genpin` | `dbo.T_M_MergeName`（部材名置换表）<br>`dbo.T_M_PartsMaster`（CMD 分析第 6 步） | 【部材名面板】整表读取、程序启动预读、<br>【CMD 分析】第 6 步的分类表 |
| 接続② MSIF | `connJP2` | `10.103.126.209` / `MSIF` | `dbo.DL_BuhinBuzaiMapping`（部材不材 mapping） | 部品基本信息画面点【保存】时写入<br>「部品名 / 部材名 / 部品コード」 |

按钮上有鼠标悬停提示（ToolTip），写明各自对应的库和表。

---

## 3. 画面操作

| 操作 | 结果 |
|------|------|
| 点【接続① genpin】 | 打开「SQL Server 接続設定 ① · genpin」：维护 `connJP1` |
| 点【接続② MSIF】 | 打开「SQL Server 接続設定 ② · MSIF」：维护 `connJP2` |
| 【测试连接】 | 真的连一次库；连上后还会检查对应的表是否存在，并显示现有行数（只测试，不保存） |
| 【保存】 | 只更新当前这个连接的配置；成功后关闭画面，主画面日志输出新的接続先 |
| 【取消】 | 不改动已保存的连接信息 |

- ① 保存后：**立即**让内存里的置换表按新连接重读（下一次分析 / 保存部品信息马上用新规则）。
- ② 保存后：下一次在部品基本信息画面点【保存】时按新连接写入（那边不需要预读，也不用重启）。
- 保存成功后主画面底部状态栏显示「SQL Server 接続 connJP1 / connJP2 已更新」，
  运行日志里输出 `connJP1 = SQL Server 10.103.126.208,1433 / DB genpin / 登录 …（来源：…）`。

> 部材名置换画面（`MemberNameReplacementForm`）里的「连接设置」按钮打开的也是 ①（默认 = `connJP1`）。

---

## 4. 配置保存位置 / 格式

位置：`%APPDATA%\MSIFAITOOLS\mapping-db-settings.json`（密码用 DPAPI 加密，存成 `dpapi:…`）。

两个连接各一节；`connJP1` 同时再写一份旧版本的扁平键（旧版本程序只认扁平键，这样它也能读到同样的接続先）：

```json
{
  "connJP1": { "Server": "10.103.126.208", "Port": 1433, "Database": "genpin", "UserId": "…", "Password": "dpapi:…" },
  "connJP2": { "Server": "10.103.126.209", "Port": 1433, "Database": "MSIF",   "UserId": "…", "Password": "dpapi:…" },

  "Server": "10.103.126.208", "Port": 1433, "Database": "genpin", "UserId": "…", "Password": "dpapi:…"
}
```

- 只有扁平键（升级前保存过）的文件照样能用：整份配置当作 `connJP1`。
- 保存一个连接时，另一个连接的分节原样保留。
- 密码解密失败 / 文件损坏时，日志会输出「连接信息文件读取失败：…」，并回退到下一级配置。

---

## 5. 取值优先级（两个连接各自独立解析）

```
环境变量 MSIF_MAPPING_DB_*（逐项覆盖）
  → 本画面保存的该连接（connJP1 / connJP2 分节）
    → App.config <connectionStrings> 里的同名连接
      → appSettings MappingDb*（旧配置）
        → 内置默认值
```

- 现场应急可以只设环境变量（`MSIF_MAPPING_DB_SERVER` / `_PORT` / `_DATABASE` / `_USER` / `_PASSWORD`），
  不用开画面，也不用改 `App.config`。
- `App.config` 里没有 `connJP2` 且画面也没保存过时，② 会退回 `connJP1` 那一套 ——
  这时保存部品基本信息会在画面上提示「部材不材 mapping 登录失败」（部品基本信息本身照样保存成功），
  在【接続② MSIF】里填好 MSIF 库保存即可恢复。

---

## 6. 涉及代码（排查用）

| 位置 | 作用 |
|------|------|
| `Form1.BtnDbJp1Settings_Click` / `BtnDbJp2Settings_Click` | 主画面两个按钮的入口 |
| `Form1.ShowDbConnectionSettings` | 打开设置画面，保存成功后写日志 / 状态栏 |
| `MemberMappingDbSettingsForm(string connectionName)` | 同一个画面按构造参数切换 ① / ②（无参构造 = ①） |
| `MemberMappingDbSettingsStore.ResolveByName` | 按具名连接解析（环境变量 → 画面配置 → App.config → 默认值） |
| `MemberMappingDbSettingsStore.ResolveJp1` / `ResolveJp2` | ① / ② 的固定入口 |
| `MemberMappingDbSettingsStore.Load(name, out error)` / `Save(settings, name, out error)` | 分节读写 |
| `MemberNameReplacementRepository.TestConnection` / `BuhinBuzaiMappingRepository.TestConnection` | 两边各自的【测试连接】 |
