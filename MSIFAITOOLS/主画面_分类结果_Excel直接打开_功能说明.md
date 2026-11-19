# 【分类结果】Excel 行：直接用本机 Excel 打开并置前 功能说明

> 所属项目：`MSIFAITOOLS`（.NET Framework 4.8 / WinForms / x64）
> 源文件：`Form1.cs`、`Services\ExcelLauncher.cs`（新建）、`Services\ExcelFileCatalog.cs`、
> `Services\UiText.Japanese.cs`、`MSIFAITOOLS.csproj`
> 更新日期：2026-11-18

---

## 1. 一句话概要

【分类结果】一览里的 Excel 行（`.xlsx / .xlsm / .xlsb / .xls`，淡蓝底、分类列「Excel」）**不再弹出本工具的 Excel 预览画面**。
双击 / Enter / 【预览 PDF】/ 右键「预览」/ 【部品信息】时，**直接调起本机 Excel 打开该工作簿，并把 Excel 窗口放到最前**；
该工作簿**已经在 Excel 里打开**时**只激活那个窗口**（不打开第二份、不新起 `EXCEL.EXE`）。

```
图纸文件夹\
  A-1001.pdf    → PDF 行  → 【预览】= PdfPreviewForm（原有 PDF 预览窗口）
  部品表.xlsx   → Excel 行 → 【预览】= 本机 Excel 打开 部品表.xlsx 并置前（没有预览窗口了）
```

---

## 2. 行为一览

| 操作 | PDF 行 | Excel 行 |
|------|--------|----------|
| 勾选「选中即预览」后选行 | 复用 `PdfPreviewForm` 显示（不抢焦点） | **只更新状态栏提示**，不打开 Excel（避免每换一行 Excel 就跳到最前，表格没法用方向键浏览） |
| 双击行 / 【预览 PDF】/ 右键「预览」/ Enter | 预览窗口前置 | **用 Excel 打开并置前**（已打开 → 只激活） |
| 按 Enter | 打开部品基本信息画面（原有行为） | 用 Excel 打开（Excel 没有部品基本信息） |
| 右键「查看部品基本信息」 | 可用 | 置灰 |
| 点【部品信息】按钮 | 打开部品基本信息画面 | 【运行日志】提示「Excel 文件没有部品基本信息：已用 Excel 打开」，并用 Excel 打开 |
| CMD 分析 / AI 分析 | 照旧 | 完全不参与（Excel 不在 `_classifications` 里） |

状态栏 / 日志的三种成功提示（`ExcelOpenOutcome`）：

| Outcome | 何时 | 状态栏 / 日志（中文键） |
|---------|------|------------------------|
| `Activated` | 该工作簿已经在 Excel 里打开 | `该文件已在 Excel 中打开，已把它放到最前：{0}` |
| `Opened` | 运行中的 Excel 里没有这个文件 → `Workbooks.Open` | `已用 Excel 打开：{0}` |
| `ShellOpened` | 没有可控制的 Excel（未安装 / 权限级别不同）→ 系统文件关联 | `已用系统默认程序打开：{0}` |

失败时：`打开 Excel 失败：{0}`（同时写进【运行日志】并弹一次对话框；自动预览时只写状态栏）。

---

## 3. 实现流程

### 3.1 分派（`Form1.ShowPreview` → `Form1.OpenExcelFile`）

```
选中 / 双击 / Enter / 【预览 PDF】/ 右键「预览」/ 【部品信息】
        → ShowPreview(item, bringToFront)
            ├─ ExcelFileCatalog.IsExcelFile(item.FilePath) == true → OpenExcelFile(item, bringToFront)
            └─ 否则                                             → ShowPdfPreview（PDF，逻辑不变）

OpenExcelFile:
  1. 路径为空 / 文件不存在 → 状态栏（显式操作再弹提示）
  2. bringToFront == false（「选中即预览」）→ 只写状态栏「Excel 文件：双击 / Enter /【预览】用 Excel 打开：{0}」
  3. bringToFront == true → ExcelLauncher.OpenOrActivate(path)
       成功 → 按 Outcome 写状态栏 + 【运行日志】
       失败 → 状态栏 + 日志 + 对话框
```

`_lastAutoPreviewKey` 在 `OpenExcelFile` 里也会更新：同一行不会重复处理（与 PDF 预览共用同一个字段）。

### 3.2 `Services\ExcelLauncher.cs`：用 Excel 打开 / 激活

```csharp
ExcelOpenResult result = ExcelLauncher.OpenOrActivate(@"D:\\図面\\部品表.xlsx");
// result.Success / result.Outcome（Activated / Opened / ShellOpened）/ result.Error
```

内部三种情况：

1. **Excel 正在运行**（`Marshal.GetActiveObject("Excel.Application")` 成功）
   - `Workbooks` 里有 `FullName` 与目标路径相同的（`Path.GetFullPath` + 不区分大小写比较）
     → `book.Activate()` + `Application.Hwnd` → `IsIconic` ? `ShowWindowAsync(SW_RESTORE)` → `SetForegroundWindow`
     → `ExcelOpenOutcome.Activated`（**不会打开第二份**）
   - 没有这个文件 → `excel.Workbooks.Open(fullPath)` → `Activate()` → 置前 → `ExcelOpenOutcome.Opened`
   - `Application.Visible = true`（隐藏的自动化实例也能显示出来）
2. **没有运行中的 Excel**（ROT 里找不到 / `COMException`）→
   `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })`
   → `ExcelOpenOutcome.ShellOpened`（Excel / WPS 都行；Windows / Excel 的 DDE 会激活已打开的那个工作簿）
3. **COM 打开抛异常**（用户在 Excel 的只读 / 修复提示里点了取消、格式不支持…）→
   记下原因，退回第 2 步；两步都失败才返回 `false`，`Error` 里带两边的原因。

全部 **late binding**（`dynamic` + `Marshal.GetActiveObject`），**不引用 `Microsoft.Office.Interop.Excel`**：
没装 Excel 的机器照样能启动本程序，只有真的点 Excel 行时才提示「打开 Excel 失败」。

> 只能在 **UI 线程（STA，有消息泵）** 调用：`GetActiveObject` 要求 STA，Excel 自己弹的对话框也需要消息泵。
> 因此 `OpenExcelFile` 是同步调用（不放到 `Task.Run` 里），和「双击文件」一样由 Excel 自己处理提示。

### 3.3 「选中即预览」为什么不打开 Excel

勾选「选中即预览」后，用 ↑↓ 换行本来是在一览里连续浏览；如果每换一行都把 Excel 调到最前，
焦点跑到 Excel，一览反而没法用键盘操作（原来的 Excel 预览窗口是「不抢焦点」显示的，才有这个体验）。
所以：

- 自动预览（`bringToFront == false`）→ **只写状态栏**，不打开 Excel；
- 显式操作（双击 / Enter / 【预览 PDF】/ 右键 / 【部品信息】，`bringToFront == true`）→ 打开并置前。

如果想改成「自动预览也打开 Excel」，把 `Form1.OpenExcelFile` 里 `if (!bringToFront) { … return; }` 这一段删掉即可
（状态栏提示也会随之消失）。

---

## 4. 停用的文件（不参与编译）

| 文件 | 状态 |
|------|------|
| `XlsxPreviewForm.cs` | **已从 `MSIFAITOOLS.csproj` 的 `<Compile>` 列表移除**，文件头有「已停用」说明。不再有 Excel 预览窗口 |
| `Services\ExcelPdfConverter.cs` | **已从 `<Compile>` 移除**（旧【PDF 视图】用：Excel → PDF → pdfium 渲染），文件头有说明 |
| `Services\XlsxImageCatalog.cs` | 仍登记在 csproj（不引用上面两个已停用的类型，编译无影响）；现在没有调用方 |

`Form1.cs` 里与旧预览窗口相关的东西已全部删除：

- 字段 `private XlsxPreviewForm _xlsxPreviewForm;`
- 方法 `ShowExcelPreview(...)`（改为 `OpenExcelFile(...)`）
- `XlsxPreviewForm_FormClosed(...)`
- `OnFormClosed` 里取 / 关 / 置空 `_xlsxPreviewForm` 的代码
- `ShowPartInfo` / `ShowPreview` 里的 Excel 分派改调 `OpenExcelFile`

PDF 预览、部品基本信息画面、`TransferPreviewFormToPartInfo`（只移交 `PdfPreviewForm`）**都不受影响**。

---

## 5. csproj / 日语对照表

`MSIFAITOOLS.csproj`：

```xml
<!-- Excel 行：直接用本机 Excel 打开并放到最前（COM late binding，没装 Excel 也能启动本程序） -->
<Compile Include="Services\ExcelLauncher.cs" />
```

`Services\UiText.Japanese.cs` 新增的条目（键 = 中文原文，必须与代码一字不差）：

| 中文键 | 日本語 |
|--------|--------|
| `Excel 文件：双击 / Enter 用 Excel 打开（已经打开时只把窗口放到最前，不参与 CMD / AI 分析）` | 一览「判定依据」列（Excel 行） |
| `Excel 文件没有部品基本信息：已用 Excel 打开` | 【部品信息】按钮对 Excel 行的【运行日志】提示 |
| `Excel 文件：双击 / Enter /【预览】用 Excel 打开：{0}` | 「选中即预览」时的状态栏提示 |
| `该文件已在 Excel 中打开，已把它放到最前：{0}` | 已打开 → 只激活 |
| `已用 Excel 打开：{0}` | 在运行中的 Excel 里打开 |
| `已用系统默认程序打开：{0}` | 交给系统关联（没有可控制的 Excel） |
| `打开 Excel 失败：{0}` | COM / 系统打开都失败 |
| `已开启自动预览：选中一行即预览该 PDF（Excel 行只在状态栏提示，双击 / Enter 用 Excel 打开）。` | 勾选「选中即预览」的日志 |
| `已关闭自动预览：可双击行或点击「预览 PDF」按钮显示（Excel 行同样可用 Excel 打开）。` | 取消「选中即预览」的日志 |

> 旧条目（`Excel 预览：…`、`Excel 文件：选中 / 双击时用 Excel 预览显示内容…`、`已打开 Excel 预览`、Excel 预览窗口自己的按钮文字等）
> 已没有任何代码引用，留在表里也不会出错（查不到键就显示中文，多的键不会被用到）。

---

## 6. 恢复旧的 Excel 预览窗口

1. 把 `XlsxPreviewForm.cs`、`Services\ExcelPdfConverter.cs` 重新加回 `MSIFAITOOLS.csproj` 的 `<Compile>` 列表；
2. `Form1.cs` 恢复 `_xlsxPreviewForm` 字段 + `ShowExcelPreview(...)`（旧实现可参考本文件的 Git 历史 /
   `主画面_分类结果_Excel预览_功能说明.md` 的 3.2 节）；
3. `ShowPreview` / `ShowPartInfo` 的 Excel 分派改回 `ShowExcelPreview(item, bringToFront)`。

---

## 7. 排查

| 现象 | 原因 / 处理 |
|------|-------------|
| 点 Excel 行没有任何 Excel 窗口出现 | 状态栏看 `已用系统默认程序打开`（Excel 启动慢一点）或 `打开 Excel 失败：…`；失败原因会同时写进【运行日志】 |
| `打开 Excel 失败：… / …` 两个原因 | 前一个是 Excel COM（例如用户在只读提示里按了取消），后一个是系统关联打开 |
| Excel 已经开着同一文件，却又打开了一份 | 不是同一路径（短路径 / 网络盘映射 / 大小写不同的 UNC）；`SamePath` 用 `Path.GetFullPath` 比较，映射盘与 UNC 会被当成两个文件 |
| Excel 在另一块屏幕 / 最小化，点了没反应 | 先 `ShowWindowAsync(SW_RESTORE)` 再 `SetForegroundWindow`；若被 Windows 前台锁挡住，点一下 Excel 任务栏按钮即可 |
| 选 Excel 行只看到状态栏提示 | 「选中即预览」不打开 Excel（见 3.3）；双击 / Enter / 【预览 PDF】才会打开 |
| 日语界面下状态栏仍是中文 | 文件名是变量，整句按 `{0}` 模板翻译；确认 `UiText.Japanese.cs` 里的键与代码完全一致 |
