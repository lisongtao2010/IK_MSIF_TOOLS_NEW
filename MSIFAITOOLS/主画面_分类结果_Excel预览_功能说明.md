# 【分类结果】一览显示 Excel + Excel 预览画面 功能说明（⚠ 已停用）

> **⚠ 本文档描述的「Excel 预览画面（`XlsxPreviewForm`）」已经停用**（2026-11-18）。
> 现在 Excel 行改为**直接用本机 Excel 打开并把窗口放到最前**（该文件已经打开时只激活，不再打开第二份）。
> 最新行为见 **`主画面_分类结果_Excel直接打开_功能说明.md`**。
> 本文档保留作历史记录：第 3.1 节「一览追加 Excel 行」仍然有效，其余「预览窗口」部分已不适用。

> 所属项目：`MSIFAITOOLS`（.NET Framework 4.8 / WinForms / x64）
> 源文件：`Form1.cs`、`XlsxPreviewForm.cs`（新建）、`Services\ExcelFileCatalog.cs`（新建）、`Services\ExcelPdfConverter.cs`（新增）、`Form1.Localization.cs`、`Services\UiText.Japanese.cs`、`MSIFAITOOLS.csproj`
> 更新日期：2026-11-18

---

## 1. 一句话概要

主画面【分类结果】一览以前**只显示 PDF**；现在**同一个图纸文件夹第一层的 Excel 文件也会列出来**
（`.xlsx / .xlsm / .xlsb / .xls`，淡蓝底、分类列写「Excel」）。选中 / 双击 / Enter / 点【预览】时，
**PDF 走原来的 PDF 预览窗口，Excel 弹出新的 Excel 预览窗口**（只读表格，可切换工作表）。

```
图纸文件夹\
  A-1001.pdf   → 一览第 1 行（PDF，分类来自 PdfInfoList.txt）→ 预览：PDF 预览窗口
  部品表.xlsx  → 一览最后一行（Excel，淡蓝底）               → 预览：Excel 预览窗口
```

---

## 2. 行为一览

| 操作 | PDF 行 | Excel 行 |
|------|--------|----------|
| 打开【分类结果】标签 | 不自动选中第一行（原样） | 同左 |
| 勾选「选中即预览」后选行 | 复用 `PdfPreviewForm`，不抢焦点 | 复用 `XlsxPreviewForm`，不抢焦点 |
| 双击行 / 点【预览（PDF / Excel）】/ 右键「预览」 | 预览窗口前置 | Excel 预览窗口前置 |
| 按 Enter | 打开部品基本信息画面（原有行为） | 打开 Excel 预览（Excel 没有部品基本信息） |
| 右键「查看部品基本信息」 | 可用 | **置灰** |
| 点【部品信息】按钮 | 打开部品基本信息画面 | 状态栏提示「Excel 文件没有部品基本信息：已打开 Excel 预览」，并打开 Excel 预览 |
| 分类统计标签 | 原有「基本図 / 加工図 / … / 未知」 | 末尾追加「 / Excel N」（Excel **不计入未知**） |
| CMD 分析 / AI 分析 | 照旧 | **完全不参与**（Excel 不在 `_classifications` 里） |

行颜色：PDF 白底、未知 PDF 淡黄底、**Excel 淡蓝底**（`Color.FromArgb(232, 240, 254)` / 文字 `21,101,192`）。

---

## 3. 实现流程

### 3.1 一览追加 Excel 行（`Form1.PopulateClassification` → `PopulateExcelRows`）

1. `PopulateClassification(items)` 先按原样填 PDF 行（`items` 来自 `PdfClassifier.LoadInfoList` / 分解结果）。
2. 然后调 `PopulateExcelRows(items)`：
   - `ResolveClassifyFolder(items)`：用第一个 PDF 的 `Path.GetDirectoryName` 反推「图纸文件夹」；
     没有 PDF 时退回 ① 输入设置里的 `txtFolder`。文件夹不存在 → 不追加任何行。
   - `ExcelFileCatalog.FindExcelFiles(folder)`：**只扫第一层**、不区分大小写、按 `StringComparer.Ordinal` 排序。
   - 每个文件建一个 `PdfClassification(path, PdfCategory.Unknown, "Excel 文件：…", false)`，
     行号接在 PDF 后面，`Tag` 指向它，再由 `ApplyRowStyle` 上淡蓝色、把判定依据写进「判定依据」列。
3. Excel 行放在**单独的 `_excelClassifications` 列表**里（不是 `_classifications`）：
   `_classifications` 是 CMD 分析 / AI 分析 / 「未知 PDF」统计的数据源，不能被 Excel 污染。

### 3.2 预览分派（`Form1.ShowPreview`）

```
选中 / 双击 / Enter / 【预览】/ 右键「预览」
        → ShowPreview(item, bringToFront)
            ├─ ExcelFileCatalog.IsExcelFile(item.FilePath) == true → ShowExcelPreview
            └─ 否则                                             → ShowPdfPreview（原有逻辑不变）
```

`ShowExcelPreview`：

1. 文件不存在 → 状态栏「文件不存在：xxx」，不开窗。
2. 复用 `_xlsxPreviewForm`（为空 / 已释放才 `new`），`FormClosed` 挂 `XlsxPreviewForm_FormClosed` 清引用。
3. 首次创建时 `WindowLayoutHelper.ArrangePreview(...)`（多屏放到主画面以外的屏幕，单屏占左侧），
   `Show()` 显示（窗口重写 `ShowWithoutActivation = true`，不抢焦点）。
4. `ShowExcel(path, activate)` 读文件；`bringToFront = true` 时 `BringToFront() + Activate()`，
   否则 `WindowLayoutHelper.PlaceBehindMainWindowIfOverlapping(...)`。

### 3.3 Excel 预览窗口（`XlsxPreviewForm`，无 Designer，界面用代码建）

- 文件读取：`ExcelDataReader`（工程已引用；`Services\XlsxSheetReader` 也用它），
  `FileStream(..., FileShare.ReadWrite | FileShare.Delete)` 共享只读打开 —— 预览期间 Excel 自己仍可保存。
- 工作表：`do { names.Add(reader.Name); } while (reader.NextResult())` 取全部表名填进下拉框；
  换表时重新打开文件、`NextResult()` 跳过前面几张（`ExcelDataReader` 只能往前读）。
- 显示：`DataGridView` 只读；**列头 = Excel 列名（A / B / C …）**、**行头 = Excel 行号（1 起）**，
  所以单元格位置与 Excel 里能对得上。最多显示 `2000 行 × 64 列`，超限时状态栏注明。
- 工具条：【刷新】（重读当前表 + 重取图片 + 重做 PDF）、【用 Excel 打开】（`Process.Start` 交给系统默认程序）、
  **【PDF 视图】/【表格视图】**（见下节）、【显示图片】（勾选框，默认勾选）、【关闭】；`Esc` 也关闭窗口。
- 状态栏：`工作表：<表名>（2/5）／ 显示 30 行 × 8 列 （含 3 张图片，已叠在对应的单元格上）`。
- 单元格文本格式化与 `XlsxSheetReader.ToText` 一致（整数不带 `.0`、日期 `yyyy-MM-dd`）。

#### 图片（`Services\XlsxImageCatalog.cs`）

`ExcelDataReader` 只读**单元格的值**，图片 / 图表这类「浮在单元格上的对象」不在它的读取范围里
（存在包里的 `xl/drawings/*.xml` + `xl/media/*`，与单元格数据是两套东西），
所以预览窗口另外按 OpenXML 包结构把图片挖出来：

1. `xl/workbook.xml` 的 `<sheets>` 顺序 → 取各工作表部件（`r:id` 经 `xl/_rels/workbook.xml.rels` 解析），
   这个顺序与 `ExcelDataReader` 的表索引一致；
2. 工作表 → `xl/worksheets/_rels/sheetN.xml.rels` 里 `Type` 以 `/drawing` 结尾的关系 → `xl/drawings/drawingN.xml`；
3. `drawing` 里 `twoCellAnchor` / `oneCellAnchor` 的 `<xdr:from><xdr:col>/<xdr:row>` 就是**锚点单元格**，
   其下的 `<a:blip r:embed="rIdN">` 经 `xl/drawings/_rels/drawingN.xml.rels` 指到 `xl/media/imageN.png`；
4. 预览窗口按「工作表 + 锚点单元格」把图片用 `PictureBox` 叠到表格对应的单元格上：
   位置 = `DataGridView.GetCellDisplayRectangle(col, row)`，尺寸 = 图片原始尺寸（超过 480 × 360 等比缩小）；
   滚动 / 缩放窗口 / 改列宽时重新定位（`_grid.Scroll` / `SizeChanged` / `ColumnWidthChanged`），
   锚点滚出数据区时收起，避免盖住列头；换表 / 刷新 / 关闭窗口时 `Dispose`（不泄漏）。

限制：`.xls` / `.xlsb` 是二进制格式，取不出图片（状态栏提示用【用 Excel 打开】）；
图表（`graphicFrame`）、形状、OLE 对象（嵌入的 PDF / Word）、`absoluteAnchor` 定位的图片不渲染；
Excel 365 的「图片放在单元格中」（`xl/richData/`，不走 drawing）也不解析 —— 检测到时状态栏同样提示用【用 Excel 打开】。
上限：200 张 / 单张 16 MB / 合计 96 MB，超出部分不显示；读包失败一律当作「没有图片」，不影响单元格预览。

#### PDF 视图（`Services\ExcelPdfConverter.cs`）

**为什么不用 WebBrowser 嵌 Excel**：`WebBrowser` 是 IE（MSHTML）内核，本身没有 Excel 的渲染器；
老的 OWC（Office Web Components）ActiveX 从 Office 2007 起不再随 Office 分发、还被 IE 的 killbit 屏蔽，
而且本程序是 x64（32 位 OWC 加载不了）。用 `SetParent` 把 Excel 自己的窗口塞进本窗口，
则会碰到 Excel 单实例抢窗口 / 关窗后残留 `EXCEL.EXE` 的问题。所以走「让 Excel 自己排版成 PDF，
再用工程里已有的 pdfium 显示」这条路：**本机装了 Excel 时视觉上与 Excel 完全一致**
（合并单元格、颜色、图表、条件格式、分页、打印区域都对），**没装 Excel 时自动退回表格视图**
（原来的 `ExcelDataReader` 表格不受影响）。

- 转换：`ExcelPdfConverter.TryConvert`。`Type.GetTypeFromProgID("Excel.Application")` + `dynamic`（late binding，
  不引用 `Microsoft.Office.Interop.Excel`，没装 Excel 也能正常启动程序）；
  `Workbooks.Open(path, 0, true)` **只读**打开、`ExportAsFixedFormat(xlTypePDF, …)` 导出整本工作簿，
  然后 `Close(false)` + `Quit()` —— 从不写回源文件。`DisplayAlerts = false`、不显示 Excel 窗口。
  万一连到的是用户正在用的 Excel 实例（`Workbooks.Count > 0`）就不隐藏、不 `Quit`，免得关掉用户打开的文件。
- 缓存：`%TEMP%\MSIFAITOOLS\ExcelPreview\<sha256(路径 + 大小 + 最后写入时间) 前 32 位>.pdf`。
  源文件一改（大小 / 时间变）缓存路径就变，不会显示旧内容；同一文件第二次起直接读缓存（秒开）。
  临时文件先写成 `.tmp` 再 `File.Move`，中途失败不会留下半个 PDF。
- 显示：`PdfDocument.Load` + `Render(page, width, height, 96, 96, false)`（与 `PdfPreviewForm` 同一套 pdfium）。
  页面按窗口宽度自适应（也可【放大】/【缩小】/【适合宽度】），`PageUp` / `PageDown` 翻页；
  单页最大 2500 万像素，超出按比例收缩。
- 视图切换：窗口新加一条 PDF 工具条（上一页 / 下一页 / 页码 / 缩小 / 放大 / 适合宽度），只在 PDF 视图显示；
  表格视图时 PDF 容器隐藏、图片叠加层恢复。【显示图片】只在表格视图有意义（PDF 里图片由 Excel 自己画）。
- 时序：换文件时若已有缓存 → 直接进 PDF 视图；没有缓存 → 留在表格视图，
  用户点【PDF 视图】才启动 Excel（「选中即预览」用方向键换行很频繁，不能每次都等 Excel 启动）。
  失败（没装 Excel / 文件被占用 / 磁盘满 …）只写状态栏一行原因，不弹对话框；
  【刷新】会清掉缓存重新转换（正在看 PDF 时刷新后自动重做并回到 PDF 视图）。
- 限制：PDF 是整本连续的（所有工作表依次排页），所以「工作表下拉框」在 PDF 视图里只影响表格视图；
  转换期间界面会等待 1～3 秒（Excel 启动 + 排版），这是 COM 同步调用的固有代价。

---

## 4. 边界 / 注意

| 情况 | 行为 |
|------|------|
| 文件夹里有很多 Excel | 全部列出（第一层），按文件名序数排序 |
| 文件被 Excel 打开着 | 预览仍可读（共享读，见上） |
| 文件损坏 / 不是真正的 Excel | 状态栏「无法读取该 Excel：…」；用户主动打开时再弹一次对话框 |
| 大表（> 2000 行 / > 64 列） | 只显示前 2000 行 / 64 列，状态栏提示用【用 Excel 打开】看全部 |
| 合并单元格 / 公式 / 颜色 | 不还原（只显示值）；要看格式用【用 Excel 打开】 |
| Excel 里的图片（.xlsx / .xlsm） | 叠在锚点单元格上显示；状态栏注明「含 N 张图片」；取消勾选【显示图片】可只看文字 |
| Excel 里的图片（.xls / .xlsb） | 二进制格式取不出来 → 状态栏提示用【用 Excel 打开】 |
| 图表 / 形状 / 嵌入对象 | 不渲染（没有可以画图表的库）；用【用 Excel 打开】看 |
| 图片锚点超出预览范围（> 64 列 / > 2000 行） | 该图片不显示（单元格本身也没显示） |
| 切换图纸文件夹重新分解 | `PopulateClassification` 会清空并重扫，不会残留上一份文件夹的 Excel |
| 文件夹里**只有** Excel 没有 PDF | 一览不会出现（一览只在分解 / 读取现有结果时填充）；这是原有流程限制 |
| 本机**没装** Excel（PDF 视图） | 「PDF 视图」按钮点下去 → 状态栏「本机没有安装 Excel。」，继续用表格视图（不弹对话框） |
| 本机装了 Excel（PDF 视图） | 首次点【PDF 视图】等 1～3 秒生成 PDF，之后同一文件秒开（缓存） |
| Excel 文件正被占用 / 只读 / 加密 | 只读方式打开导出；真正打不开时状态栏说明原因，退回表格视图 |
| 转换失败后反复点【PDF 视图】 | 不再重复启动 Excel（缓存失败状态），点【刷新】才重试 |
| `pdfium.dll` 缺失 / 版本不对（PDF 视图） | 状态栏提示「PDF 渲染引擎不可用（pdfium.dll）」；表格视图不受影响 |
| 工作表下拉框与 PDF 视图 | PDF 是整本连续排页，换下拉框只切换表格视图的内容（状态栏注明「PDF 里包含整个工作簿的所有工作表」） |
| 同时看 PDF 和 Excel | 两个预览窗口各自独立、互不关闭；主窗口关闭时一起关闭（`Form1.OnFormClosed`） |

---

## 5. 验证要点

1. 选一个同时有 PDF 和 `.xlsx` 的图纸文件夹 →【开始分解】→ 打开【分类结果】：
   PDF 行在前、Excel 行在后（淡蓝底、分类列「Excel」），统计标签末尾有「 / Excel N」。
2. 勾选「选中即预览」后点 Excel 行 → 弹出 Excel 预览窗口（不抢焦点）；换另一份 Excel → 同一窗口切换内容。
3. 点 PDF 行 → PDF 预览窗口照旧（两个窗口可同时存在）。
4. Excel 行双击 /【预览（PDF / Excel）】/ 右键「预览」/ Enter → Excel 预览窗口前置并显示该文件。
5. Excel 行右键「查看部品基本信息」应置灰；点【部品信息】按钮 → 状态栏提示 + 打开 Excel 预览。
6. Excel 预览里切换工作表下拉框、点【刷新】、点【用 Excel 打开】、按 `Esc` 关闭均正常。
7. 表名 / 数字 / 日期显示与 Excel 一致（整数不带 `.0`，日期 `yyyy-MM-dd`）。
8. 关闭主画面 → Excel 预览、PDF 预览、部品基本信息窗口一起关闭，无残留进程。
9. 切到日本語：菜单「预览（PDF / Excel）」、Excel 预览窗口的按钮 / 状态栏为日语。
10. 打开一个带图片的 `.xlsx`：图片出现在 Excel 里所在的单元格位置；状态栏有「含 N 张图片」；
    滚动表格图片跟着走、滚出表头前收起；取消勾选【显示图片】图片全部收起，重新勾选又出现。
11. 换工作表：图片按工作表切换（别的表上的图片不显示）；点【刷新】后单元格与图片一起重读；
    关闭窗口再打开别的 Excel，没有报错、没有残留图片控件。
12. 点【PDF 视图】（本机装了 Excel 时）：等 1～3 秒出现与 Excel 版面一致的页面（合并单元格 / 颜色 / 图表都对）；
    再点【PDF 视图】同一个文件 → 秒开（走缓存）。同一文件改名 / 保存一次后再看 → 重新生成（内容变了）。
13. PDF 视图里：上一页 / 下一页 / `PageUp` / `PageDown` / 放大 / 缩小 / 适合宽度均正常；
    点【表格视图】回到表格，图片叠加显示仍在，`ExcelDataReader` 的表格内容不变。
14. 在**没装 Excel** 的机器上点【PDF 视图】→ 状态栏「本机没有安装 Excel。」，表格视图照常；
    程序启动、打开 Excel 预览、刷新都不报错（不引用 Interop 的 late binding 起的作用）。
15. 在 PDF 视图状态下点【刷新】→ 缓存被删掉重新生成，画面回到第 1 页；
    关闭窗口后 `%TEMP%\MSIFAITOOLS\ExcelPreview` 里没有残留 `.tmp`，任务管理器里没有多出来的 `EXCEL.EXE`。

---

## 6. 变更文件

| 文件 | 内容 |
|------|------|
| `Services\ExcelFileCatalog.cs` | 新增：Excel 扩展名判断 + 文件夹第一层枚举 |
| `Services\XlsxImageCatalog.cs` | 新增：从 .xlsx / .xlsm 的 `xl/media` 取图片 + 锚点单元格（`XlsxImageInfo`） |
| `Services\ExcelPdfConverter.cs` | 新增：用本机 Excel（COM，late binding，只读）把工作簿导出成 PDF，按「路径 + 大小 + 时间」缓存到 `%TEMP%\MSIFAITOOLS\ExcelPreview`；`TryGetCachedPdf` / `TryConvert` / `DeleteCachedPdf` |
| `XlsxPreviewForm.cs` | 新增：Excel 预览窗口（只读表格、多工作表、刷新、用 Excel 打开）；图片叠加显示 + 【显示图片】勾选框；**【PDF 视图】/【表格视图】切换 + PDF 翻页 / 缩放工具条（`_pnlPdf` / `_picPdf` / `EnsurePdfDocument` / `RenderPdfPage`）** |
| `Form1.cs` | 字段 `_xlsxPreviewForm` / `_excelClassifications`；`PopulateClassification` 追加 Excel 行；新增 `PopulateExcelRows` / `ResolveClassifyFolder` / `ShowPreview` / `ShowExcelPreview`；`ApplyRowStyle` Excel 配色；`RefreshClassifySummary` 追加 Excel 数量；`ShowPartInfo` / Enter / 右键菜单对 Excel 的分支；`OnFormClosed` 关闭 Excel 预览 |
| `Form1.Localization.cs` | 菜单 / 按钮文字改为「预览（PDF / Excel）」 |
| `Services\UiText.Japanese.cs` | Excel 预览相关日语条目 |
| `MSIFAITOOLS.csproj` | 登记 `XlsxPreviewForm.cs`、`Services\ExcelFileCatalog.cs`、`Services\XlsxImageCatalog.cs`、`Services\ExcelPdfConverter.cs` |
