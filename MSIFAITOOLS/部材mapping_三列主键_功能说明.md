# 部材 mapping（DL_BuhinBuzaiMapping）三列主键 功能说明

> 所属项目：`MSIFAITOOLS`（.NET Framework 4.8 / WinForms / x64）
> 源文件：`Services\BuhinBuzaiMappingRepository.cs`、`Services\LocalBuhinBuzaiMappingRepository.cs`、
> `Services\BuhinBuzaiMappingCatalog.cs`
> 更新日期：2026-11-19

---

## 1. 一句话概要

SQL Server 的表 `[MSIF].[dbo].[DL_BuhinBuzaiMapping]` 的主键从
**`BuhinName` + `BuzaiName`（两列）** 改成 **`BuhinName` + `BuzaiName` + `BuhinCd`（三列）**。
本工具的**登录 / 更新 / 去重 / 本机缓存**全部改成按这三列走：

```
1 行 = (部品名 BuhinName, 部材名 BuzaiName, 部品CD BuhinCd)
```

同一个「部品名 + 部材名」按**不同的部品CD** 现在可以存成多行（以前会被当成同一行覆盖）。

---

## 2. 表结构（本工具使用到的列）

| 列 | 含义 | 类型 | 说明 |
| --- | --- | --- | --- |
| `BuhinName` | 部品名 | `nvarchar(100)` | **主键（1/3）**，不能为空 |
| `BuzaiName` | 部材名 | `nvarchar(100)` | **主键（2/3）**，不能为空 |
| `BuhinCd` | 部品CD（画面上的「部品コード」） | `nvarchar(100)` | **主键（3/3）**，不能为空；画面上没填时写**空串 `''`** |
| `Remarks` | 備考（本表维护侧的备注） | `nvarchar(255)` | 本工具新建行时留 `NULL`，更新时不改动 |
| `UpdatedAt` | 更新日時 | `datetime2(7)` | 本工具每次登录 / 更新都写当前时间 |

> 主键列不能是 `NULL`，所以「部品コード 没填」用**空串**表示（以前写入的是 `NULL`）。

---

## 3. 写入规则（部品基本信息画面【保存】时）

`BuhinBuzaiMappingRepository.Register` → `CollectEntries` → `Upsert`：

1. **挑行**：部品名 为空、部材名 为空 / 「その他」/ 含 `|`（多个候选、未确定）的行不登录
   （判断规则与「部材部品」的 `MemberPartCatalogLoader.RegisterRules` 同一套）。
2. **去重**：按 **三列**（`BuhinName` + `BuzaiName` + `BuhinCd`）去重 ——
   同一次保存里主键完全相同的行只写一次。部品CD 不同的行算**不同的** mapping。
3. **更新优先**：先按主键三列 `UPDATE`（只刷新 `UpdatedAt`），**更新 0 行时才 `INSERT`**。
   - 主键列本身就是定位条件，所以不会再出现以前那种「本次没填部品コード就把已有值保留 / 冲掉」的判断。
   - `BuhinCd` 用 `ISNULL([BuhinCd], N'')` 比较：加主键前留下的老数据这一列可能是 `NULL`，
     空串也要能命中那些行，避免多登录一条重复行。
4. **新增行**：`INSERT` 时 `BuhinCd` 直接写参数值（空串就是空串），`Remarks` 写 `NULL`。
5. **画面上看到的提示**不变：「部材 mapping（…）：新增 X 条 / 更新 Y 条（本次确认 Z 条）」。

---

## 4. 读出与推荐（「选择部材名」窗口）

- 读出仍是整表 `SELECT BuhinName, BuzaiName, BuhinCd`（按 `BuhinCd` → `BuhinName` → `BuzaiName` 排序）。
- `BuhinBuzaiMappingCatalog` 建两份索引（部品CD → 部材名 / 部品名 → 部材名），
  **建索引时按部材名去重**（`AddName`），所以三列主键带来的「同一组 部品名 + 部材名 有多行」
  **不会**让推荐项出现重复的部材名。
- 推荐顺序不变：**第 1 优先 = 部品CD 一致**，第 2 优先 = 部品名 一致。

---

## 5. 本机缓存（SQLite `DL_BuhinBuzaiMapping`）

本机表（`LocalBuhinBuzaiMappingRepository`，与「部材部品」マスタ同一个库文件）同步改成三列主键：

| 情况 | 行为 |
| --- | --- |
| 本机没有这张表 | `CREATE TABLE` 直接建**三列主键**，`BuhinCd TEXT NOT NULL DEFAULT ''` |
| 本机是老的两列主键表 | 打开时**自动升级**：`ALTER TABLE ... RENAME` → 建新表 → `INSERT OR REPLACE ... IFNULL(BuhinCd, '')` → 删老表 |
| 已经是三列主键 | 什么都不做（只补建「部品CD」索引） |

- 判断依据：`PRAGMA table_info` 里主键列的数量（`pk > 0` 的列数）< 3 就升级。
- 升级在**一个事务**里完成；中途失败时本机表还是老结构，下次打开再试（服务器那份是准的，
  整表读服务器成功后会把本机表整表镜像过来）。
- 本机表的读 / 写失败都**不抛异常**（`TryUpsert` / `TryReplaceAll` 只返回原因文字），不影响画面操作。

---

## 6. 现场升级注意事项

1. **服务器表要先能建出三列主键**：`BuhinCd` 里如果还有 `NULL`，需先
   `UPDATE [dbo].[DL_BuhinBuzaiMapping] SET [BuhinCd] = N'' WHERE [BuhinCd] IS NULL;`
   再改主键（本工具的 `UPDATE` 虽然用 `ISNULL` 兼容老 `NULL` 行，但 `INSERT` 写的是空串）。
2. **新旧版本工具不要混用**：旧版（两列主键）的 `INSERT` 会把空部品CD 写成 `NULL`，
   在三列主键表上会因 `NOT NULL` 失败（只是多一句提示，部品基本信息照样保存）。
   现场请统一换成新版之后再用【保存】。
3. **新增行会变多**：以前「部品名 + 部材名」相同、部品CD 不同的行会互相覆盖；
   现在会各存一行。推荐项不受影响（按部材名去重），但表行数会比以前多一点。
4. 部品CD 为空的行（`BuhinCd = ''`）不会进「部品CD → 部材名」索引（键为空时跳过），
   只按「部品名 → 部材名」参与推荐。

---

## 7. 相关文件

| 文件 | 作用 |
| --- | --- |
| `Services\BuhinBuzaiMappingRepository.cs` | 服务器表的仓储：`Register` / `Upsert` / `Query` / `CollectEntries`，主键三列 |
| `Services\LocalBuhinBuzaiMappingRepository.cs` | 本机 SQLite 副本：三列主键 + 老表自动升级 |
| `Services\BuhinBuzaiMappingCatalog.cs` | 内存缓存（推荐项），建索引时按部材名去重 |
| `Services\MemberPartCatalogLoader.cs` | 同一套「能登录的部材名」判断规则（`RegisterRules`） |
| `PartBasicInfoForm.cs` | 调用方：部品基本信息【保存】、打开时预读 |
