using System;
using System.IO;

namespace CadPartSplitter
{
    /// <summary>
    /// 零件检测与分割流程的全部可调参数。
    /// 每个参数均注明调优方向，适配不同 DPI 或图纸版式。
    /// </summary>
    public class DetectorOptions
    {
        // ── 输入预处理：PDF 批注 / 图章清理 ──────────────────────────────────

        /// <summary>
        /// 识别 PDF 之前，先把页面上的「批注 / 图章」等注释删掉，另存一份干净 PDF
        /// （<c>&lt;输出目录&gt;\&lt;图纸名&gt;_clean.pdf</c>，即 AiAppData 下该图纸的文件夹里），
        /// 渲染（位图化）时用这份新 PDF（默认开启）；文字 / 表格 / 圈号仍读原 PDF ——
        /// 批注是 annotation，不在页面内容流里，两者的文字层与矢量图形完全一致。
        ///
        /// 为什么需要：图纸在会签 / 承认流程里常被盖上「承認済」「DRAFT」图章、贴便签批注、
        /// 画红色修订框。这些注释会渲染进位图（PDFium 按 FPDF_ANNOT 绘制），
        /// 变成二值图里的粗线条，把原本分开的视图连成一片、干扰框线检测与文字归属。
        ///
        /// 实现见 <see cref="PdfAnnotationStripper"/>：用 PDFium（Docnet.Core 随包自带）
        /// 逐页 <c>FPDFPage_RemoveAnnot</c> 删注释，再 <c>FPDF_SaveAsCopy</c> 另存；
        /// 另存后会把生成的文件重新打开复核注释数（有残留就打告警，见日志）。
        /// 原 PDF <b>不会被改动</b>；一点注释都没有时不生成副本，直接用原文件。
        /// 清理失败（加密 PDF、pdfium 调用出错等）只告警并退回原 PDF，不影响识别。
        ///
        /// 关闭：命令行 `--no-strip-annot`。
        /// </summary>
        public bool StripPdfAnnotations { get; set; } = true;

        // ── 输出：把文字层补画回零件图 ────────────────────────────────────────

        /// <summary>
        /// 把 PDF 文字层里的文字「烧」回零件图 PNG（默认开启，命令行 <c>--no-burn-text</c> 关闭）。
        ///
        /// 为什么需要：有些图纸的日文用内嵌 MS-PGothic 子集（Type0 / Identity-H）写成，
        /// PDFium 渲染时画不出这些字形 —— 页面上的「鴨居 / 三方枠 / G部」整块空白，
        /// 而同一位置的圈内数字（④4、⑧8）和圆圈却正常。于是裁出来的 Part_XX.png 只有线条、
        /// 没有文字，人眼分不清「哪张图是哪个部位」，下游只看图片时也读不到部位名。
        ///
        /// 文字层本身是好的（<see cref="PartTextExtractor"/> 用 PdfPig 读得到词与紧贴词框），
        /// 所以导出 PNG 之后按词框把字补画回去（<see cref="PartTextBurner"/>）：
        ///   · 位置 / 宽高严格按 <see cref="PartText"/> 的页面像素坐标减零件框左上角；
        ///   · 字形用系统字体（MS Gothic 优先）等比拉伸到词框，视觉上与原图接近；
        ///   · 渲染里<b>已经画出字</b>的词（圈内数字、英数字）按框内墨迹占比跳过，不会画两遍。
        /// 只改 PNG 像素，<c>parts_info.json</c> / <c>parts_text.txt</c> 的内容与坐标不变。
        ///
        /// 关闭：命令行 `--no-burn-text`（界面无开关）。
        /// </summary>
        public bool BurnTextIntoParts { get; set; } = true;

        /// <summary>
        /// 补画文字时判定「渲染里已经有字，不用补」的框内墨迹占比阈值（默认 0.04，即 4%）。
        ///
        /// 计算方式：把词框裁到图内 → 反向二值化（≤160 的像素算墨迹）→ 统计墨迹像素占比。
        /// 有字的框（一次印刷的汉字 / 数字）一般在 8%~20%；只有线条穿过词框时不到 2%。
        ///
        /// 调大（如 0.10）→ 更容易判成「缺字」，补画更积极，但可能给已经渲染好的字加一层重影；
        /// 调小（如 0.01）→ 只补完全空白的词框，压在细线上的标注可能补不上。
        /// </summary>
        public double BurnTextSkipInkRatio { get; set; } = 0.04;

        // ── 区域屏蔽 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 右侧 BOM 表 / 说明文字区域的遮蔽比例（0.0 ~ 1.0）。
        /// 典型值 0.48~0.52；右侧干扰零件检测时适当增大，左侧零件被切掉时减小。
        ///
        /// 注意：开启 <see cref="AutoMaskRightColumn"/>（默认开启）时，
        /// 本值仅作为「自动检测失败时的兜底比例」，不再直接生效。
        /// </summary>
        public double RightMaskRatio { get; set; } = 0.50;

        /// <summary>
        /// 是否自动检测右侧表格 / 标题栏的起始列作为遮蔽边界（默认开启）。
        ///
        /// 固定比例遮蔽（如 0.50）会把「图幅较宽、右侧还画有零件视图」的图纸误切，
        /// 典型现象：图纸上明明画了两个相同的零件（例如左右各一个「戸当り」），
        /// 分割结果却只有一个——右侧那个整块落在 50% 遮蔽区内被抹掉了。
        ///
        /// 自动检测依据「列墨迹」：表格 / 标题栏区每一列都有表格线或文字，
        /// 列墨迹稳定高于阈值；零件区列与列之间必然存在细窄空白列。
        /// 于是从最右侧向左延伸，遇到第一处低于阈值的列即为表格区左边界。
        /// 关闭本开关可退回 <see cref="RightMaskRatio"/> 的固定比例行为。
        /// </summary>
        public bool AutoMaskRightColumn { get; set; } = true;

        /// <summary>
        /// 自动遮蔽时判定「该列属于表格 / 标题栏」的列墨迹阈值（图高比例）。
        /// 默认 0.010（即一列里要有 1% 图高的前景像素才算“密实列”）。
        /// 300 DPI 的 A1 图纸（高约 7000 px）对应约 70 px。
        /// 阈值偏大 → 边界落在表格内部（右侧表格残留）；偏小 → 边界吃进零件区。
        /// </summary>
        public double AutoMaskColumnInkRatio { get; set; } = 0.010;

        /// <summary>
        /// 自动遮蔽检测结果的最小遮蔽宽度（占图宽比例）。
        /// 低于此值说明右侧没有成片的表格区，退回 <see cref="RightMaskRatio"/>。
        /// </summary>
        public double AutoMaskMinMaskedRatio { get; set; } = 0.05;

        /// <summary>
        /// 自动遮蔽检测结果的最大遮蔽宽度（占图宽比例）。
        /// 超出此值通常是误判（例如整页被零件/填充图元占满，找不到分界列），
        /// 此时退回 <see cref="RightMaskRatio"/> 的固定比例。
        /// </summary>
        public double AutoMaskMaxMaskedRatio { get; set; } = 0.60;

        // ── 图框擦除 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 是否在二值化后擦除图纸四周的图框（默认开启）。
        ///
        /// 图框是一条贴近页边、几乎贯穿整幅的矩形线。它有两个害处：
        ///   ① 与内部视图通过尺寸线粘连后，整个框会被当成一个「零件」（框的内部全被带进来）；
        ///   ② 框被遮蔽线截断后，残片面积占比降到 <see cref="MaxBboxFillRatio"/> 以下，
        ///      反而躲过「外框过滤」，输出一张「半页图」。
        /// 擦掉后图框不再参与连通域，内部视图各自独立成块。
        /// </summary>
        public bool RemovePageFrame { get; set; } = true;

        /// <summary>
        /// 图框线允许出现的「页边带」宽度比例（默认 0.10，即只擦除顶部 / 底部 / 左侧 / 右侧各 10% 内的贯穿线）。
        /// 调大会擦掉靠近页边的零件长边线；调小可能漏掉内缩较多的图框。
        /// </summary>
        public double PageFrameEdgeRatio { get; set; } = 0.10;

        /// <summary>
        /// 判定为图框线所需的贯穿比例（默认 0.70，即一行 / 一列要有 70% 幅面有墨迹）。
        /// 调大会漏擦（图框被文字打断）；调小会把零件的长中心线也擦掉。
        /// </summary>
        public double PageFrameSpanRatio { get; set; } = 0.70;

        // ── 表格块遮蔽（代替整列遮蔽）────────────────────────────────────────

        /// <summary>
        /// 是否用「文字层推断出的表格块」精确遮蔽，而不是把右侧整列涂黑（默认开启）。
        ///
        /// 整列遮蔽的前提是「表格区与零件区在 X 方向可分」。当图纸的表格在右上、
        /// 零件视图在下方中部（两者 X 投影重叠）时，密实列带会一路吃进零件区，
        /// 把中部视图整块抹掉（典型现象：输出图只剩左侧一半）。
        /// 开启后只涂黑真正的表格 / 表題欄矩形，其余像素原样保留。
        /// 文字层不可用或检测不到表格块时，自动回退为整列遮蔽。
        /// </summary>
        public bool MaskTableBlocks { get; set; } = true;

        /// <summary>
        /// 表格块遮蔽时向外扩展的像素（默认 30），用来吃掉表格线框（文字外接框到表格线通常 10~30px）。
        /// 调大会连表格外的图元一起擦掉；调小会残留表格线，可能形成「表格残片」伪零件。
        /// </summary>
        public int TableMaskPadding { get; set; } = 30;

        // ── 二值化 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 自适应阈值化的邻域块大小（奇数，≥3）。
        /// 扫描件光照不均时建议 51~101；纯矢量图导出建议 31~51。
        /// </summary>
        public int AdaptiveBlockSize { get; set; } = 51;

        /// <summary>
        /// 自适应阈值化的 C 常量（从邻域均值中减去）。
        /// 正值使二值化更严格（去更多噪点），负值更宽松。典型值 5~15。
        /// </summary>
        public int AdaptiveC { get; set; } = 10;

        // ── 形态学膨胀 ────────────────────────────────────────────────────────

        /// <summary>
        /// 膨胀核宽度（像素）。横向长条零件需宽远大于高。
        /// 300 DPI 建议 60~120；DPI 降低时等比缩减。
        ///
        /// 只在 <see cref="ClusterGapMm"/> ≤ 0 时生效；默认开启毫米聚合规则后，
        /// 聚合容差由 <see cref="ClusterGapMm"/> 给出（与 DPI 无关、横纵一致）。
        /// </summary>
        public int DilateKernelWidth { get; set; } = 60;

        /// <summary>
        /// 膨胀核高度（像素）。控制纵向聚合范围。
        /// 若相邻零件纵向粘连则减小；同一零件内部断开则增大。
        ///
        /// 只在 <see cref="ClusterGapMm"/> ≤ 0 时生效（见 <see cref="DilateKernelWidth"/>）。
        /// </summary>
        public int DilateKernelHeight { get; set; } = 10;

        /// <summary>
        /// 形态学操作迭代次数。越多斑块越大越连通，但更易粘连相邻零件。
        /// 仅在 <see cref="ClusterGapMm"/> ≤ 0（关闭毫米聚合规则）时生效。
        /// </summary>
        public int DilateIterations { get; set; } = 2;

        // ── 视图聚合规则（墨迹间距 / 单链聚类）────────────────────────────────

        /// <summary>
        /// 视图聚合的「墨迹间距」阈值（<b>毫米</b>，默认 3.0）：两个黑点之间的距离
        /// ≤ 此值就认为属于<b>同一张零件图</b>，超过就分属两张。
        ///
        /// <para>
        /// 为什么按毫米而不是像素：<see cref="DilateKernelWidth"/> / <see cref="DilateKernelHeight"/>
        /// 是像素核（默认 60 × 10），换 DPI 就变容差 —— 300 DPI 下横向能跨 10 mm 粘连、
        /// 纵向只跨 1.5 mm；同一个 3 mm 的间隙，横向并得起来、纵向并不起来。
        /// 图纸本身是按毫米画的：CAD 的线是连续的，文字离最近的线一般不超过 3 mm，
        /// 而两个视图之间通常隔 10 mm 以上，所以「≤ 3 mm 算一张图」正好把
        /// 视图本体 + 它的尺寸标注 / 引出线收进同一块，又不会把两个视图糊在一起。
        /// </para>
        ///
        /// <para>
        /// 实现 = <b>单链聚类</b>（single-linkage）：把二值图用「边长 = 阈值」的圆形核做闭运算，
        /// 再把每个连通域膨胀半个阈值；两个黑点之间只要存在一条「步长 ≤ 阈值」的
        /// 黑点链，就归到同一块。半径换算见 <see cref="ClusterGapRadiusPx"/>。
        /// 注意单链聚类<b>有传递性</b>：A-B 2 mm、B-C 2 mm ⇒ A、B、C 全并（A、C 之间可以超过 3 mm）。
        /// </para>
        ///
        /// <para>
        /// <b>局限</b>：尺寸线 / 引出线本身是连续墨迹（间距 = 0），靠它连着的两个视图
        /// 依然会并成一块 —— 这靠的不是本参数，而是「不规则分割」
        /// （填实闭合轮廓 → 开运算剥细桥 → 各视图认领自己的线框，见 <see cref="IrregularSplitLargeBlocks"/>）。
        /// 本参数只管「碎块归并」：让同一个视图断成几节的外轮廓、飘在旁边的标注重新聚到一起。
        /// </para>
        ///
        /// <para>
        /// 调大（如 5.0）→ 更倾向糊成一张（视图分的块变少、标注带得更全）；
        /// 调小（如 1.5）→ 切得更碎（抠得干净，但一个视图可能被拆成几块）。
        /// 设 0（命令行 <c>--no-cluster-gap</c>）→ 关闭本规则，退回
        /// <see cref="DilateKernelWidth"/> / <see cref="DilateKernelHeight"/> 的像素核行为（旧版结果）。
        /// </para>
        /// </summary>
        public double ClusterGapMm { get; set; } = 3.0;

        /// <summary>
        /// 把 <see cref="ClusterGapMm"/> 换算成形态学核半径（像素）：
        /// <c>r = round(阈值 / 2 × dpi / 25.4)</c> —— 两侧各外扩 r，两点距离 ≤ 2r ≈ 阈值即并成一块。
        ///
        /// 300 DPI、3.0 mm ⇒ r = 18 px，核长大 37 × 37；150 DPI 同阈值 ⇒ 9 px（容差仍是 3 mm）。
        /// 返回 0 表示「不启用毫米规则」（阈值为 0 或 dpi 非法），调用方退回像素核。
        /// </summary>
        /// <param name="dpi">当前位图对应的 DPI（PDF 渲染用 <see cref="RenderDpi"/>，位图输入同样取它）。</param>
        public int ClusterGapRadiusPx(double dpi)
        {
            if (ClusterGapMm <= 0 || dpi <= 0)
                return 0;

            double pxPerMm = dpi / 25.4;
            int radius = (int)Math.Round(ClusterGapMm / 2.0 * pxPerMm);

            return Math.Max(1, radius);
        }

        // ── 轮廓过滤 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 轮廓最小面积阈值（平方像素）。
        /// 小于此值视为噪点或孤立数字，直接丢弃。
        /// 300 DPI 建议 50000~150000；零件越小可适当降低。
        /// </summary>
        public double MinContourArea { get; set; } = 25000;

        /// <summary>
        /// 外接框最小宽度（像素）。过滤孤立细长噪线。
        /// </summary>
        public int MinBoundingWidth { get; set; } = 200;

        /// <summary>
        /// 外接框最小高度（像素）。过滤水平方向孤立刻度线。
        /// </summary>
        public int MinBoundingHeight { get; set; } = 80;

        // ── 裁切边距 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 外接框四周扩展的像素边距，确保尺寸标注和引出线不被裁断。
        /// 300 DPI 建议 20~40 像素。
        /// </summary>
        public int Padding { get; set; } = 30;

        // ── 矩形合并 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 矩形合并的重叠率阈值（0.0~1.0）。
        /// 两矩形交集面积占较小矩形面积之比超过此值时合并为一个。
        /// 过低会误合并不同零件；过高会导致同一零件被拆分。
        /// </summary>
        public double MergeOverlapThreshold { get; set; } = 0.30;

        /// <summary>
        /// 外框过滤阈值：外接矩形面积 / 整图面积 超过此值时视为图纸边框而丢弃。
        /// 图纸通常有包围所有内容的外框矩形，其面积占比可达 40%~50%；
        /// 单个零件区域一般不超过 25%，故默认 0.25 可稳定过滤外框。
        /// 若零件本身非常大（全幅宽度），可适当调高至 0.40。
        /// </summary>
        public double MaxBboxFillRatio { get; set; } = 0.25;

        // ── 不规则分割兜底（矩形分割只出 1 块时）─────────────────────────────

        /// <summary>
        /// 矩形分割<b>恰好只得到 1 块</b>时，是否改用「不规则分割」把它重新拆开（默认开启）。
        ///
        /// 典型情形：一張組立図上并排着正面 / 侧面 / 断面 / 部分詳細等几个视图，它们之间由尺寸线、
        /// 引出线、中心线连着（或膨胀核把相邻视图连成一排），连通域只有一个 —— 外接矩形自然也只有一个。
        /// 这时把核调小会把零件切碎，调大更糊成一张，**矩形分割无解**。
        ///
        /// 不规则分割的做法见 <see cref="IrregularSplitter"/>：填实闭合轮廓 → 开运算剥细桥 →
        /// 每个视图向外扩 <see cref="IrregularSplitReach"/> 认领自己的线框 → 用多边形掩膜裁剪导出
        /// （掩膜外涂白，所以两个块的矩形框重叠也不会互相带线）。
        ///
        /// 拆不出来（仍只有 1 块）时原样保留矩形结果，正常图纸的产物不受影响。
        /// 真的不想让它介入时关掉：命令行 `--no-irregular-split`。
        /// </summary>
        public bool IrregularSplitWhenSingle { get; set; } = true;

        /// <summary>
        /// 「大块再拆」：矩形分割已经出了多块时，对<b>够大的块</b>再试一次不规则分割（默认开启）。
        ///
        /// 原来不规则分割只在「矩形分割恰好只出 1 块」时兜底。但一张組立図上更常见的形态是
        /// 「大部分视图各自成块，只有一两个块里粘着好几个视图」（正面 + 侧面由尺寸线串着），
        /// 这种情况只出 1 块的兜底永远轮不到，那块就只能整块留下。
        ///
        /// 开启后：对整页（已擦图框、已遮蔽表格的）二值图做一次
        /// <see cref="IrregularSplitter.Split"/>，把拆出来的实体按「外接框中心落在哪个块里」
        /// 归到各块；某块里落进 ≥2 个实体、且每个实体自己的墨迹面积
        /// ≥ <see cref="MinContourArea"/> 时，用这些实体（多边形掩膜）替换该块，
        /// 其余块仍然整框裁切 —— 没有块被拆开时产物与改动前完全一致。
        ///
        /// 关闭：命令行 `--no-irregular-split-large`。
        /// </summary>
        public bool IrregularSplitLargeBlocks { get; set; } = true;

        /// <summary>
        /// 「大块再拆」的面积门槛：块的<b>外接框面积占整页的比例</b> ≥ 此值才尝试再拆（默认 0.02，即 2%）。
        ///
        /// 300 DPI 的 A1（约 9921 × 7008 px）对应约 140 万平方像素，即 2000×700 这样的条状块也算「大块」；
        /// 小块里塞不下两个视图，拆了只会把零件切碎。
        ///
        /// 调大（如 0.05）→ 只拆特别大的块，改动更保守；调小（如 0.01）→ 更多块参与再拆。
        /// </summary>
        public double IrregularSplitLargeAreaRatio { get; set; } = 0.02;

        /// <summary>
        /// 不规则分割时，每个视图实体向四周外扩的像素（默认 100）。
        ///
        /// 外扩范围内的线框、尺寸标注、引出线算这个视图的；两块的邻域重叠时按「面积大的先占」，
        /// 后来者避开已被占走的像素。
        /// 调大 → 视图的标注带得更全，但相邻视图之间容易互相抢线；
        /// 调小 → 各视图干净，但自己外侧的尺寸标注可能被切掉（通常 <see cref="Padding"/> 的 2~4 倍即可）。
        /// </summary>
        public int IrregularSplitReach { get; set; } = 100;

        // ── 伪零件筛除（文字面积占比）─────────────────────────────────────────

        /// <summary>
        /// 是否按「文字行面积占比」丢弃非零件图（默认开启）。
        ///
        /// 零件图上的文字只有尺寸、引线注记等零星标注，文字行加起来占自身外框很小一块；
        /// 表題欄 / 変更履歴表 / 部品表这类表格每一行都横跨整张表、整块几乎全是文字行。
        /// 表格残片被当成零件输出时，图里根本没有零件视图，只有一张表 —— 开启本开关后
        /// 这类块在导出 PNG 之前就被丢弃（编号顺延，不会留下空号）。
        ///
        /// 位图输入没有文字层，本开关不生效（不做 OCR）。
        /// </summary>
        public bool DropTextHeavyParts { get; set; } = true;

        /// <summary>
        /// 「文字行面积占比」判定阈值（默认 0.60）。
        ///
        /// 计算方式：块内所有「文字行」外接框与块的交集面积之和 ÷ 块面积，
        /// 即「文字行合计占了该块多大面积」。≥ 此值判定该块不是要分割的零件图。
        ///
        /// 实测（300 DPI，A1/A2 图纸）：正常零件图 0.03~0.46，表格残片（表題欄 / 変更履歴表）0.65~0.72。
        /// 调小 → 更容易丢弃（文字标注密集的零件视图也可能被丢）；调大 → 只丢文字几乎铺满的块。
        /// </summary>
        public double TextHeavyRatio { get; set; } = 0.60;

        // ── PDF 渲染 ──────────────────────────────────────────────────────────

        /// <summary>
        /// PDF 渲染目标 DPI。300 适合高精度识别；内存受限可降至 150。
        /// </summary>
        public double RenderDpi { get; set; } = 300.0;

        /// <summary>
        /// 要处理的 PDF 页码（从 0 开始）。
        /// </summary>
        public int PdfPageIndex { get; set; } = 0;

        // ── 圈号标注（①、②…）───────────────────────────────────────────────

        /// <summary>
        /// 是否识别「圈号标注」（圆圈 + 圈内数字，如 ①1）（默认开启）。
        ///
        /// 圆圈在 PDF 里是矢量图形、不在文字层，所以直接复制文字只能得到孤立数字。
        /// 开启后按「矢量路径找整圆 + 文字层找圆内数字」配对，输出 ①②… 与圆心 / 半径坐标，
        /// 结果写入 parts_info.json（每张零件图内）与 callouts.txt（全局清单）。
        /// 位图输入、无矢量路径或识别失败时只写日志，不影响分割结果。
        /// </summary>
        public bool DetectCallouts { get; set; } = true;

        /// <summary>
        /// 圈号圆的最小半径（毫米，默认 0.8）。小于此值的圆（尺寸线圆点、螺纹牙底圆角）不当圈号。
        /// 300 DPI 下 0.8mm ≈ 9.4px。
        /// </summary>
        public double CalloutMinRadiusMm { get; set; } = 0.8;

        /// <summary>
        /// 圈号圆的最大半径（毫米，默认 12.0）。图纸上圈号直径通常 2~8mm，
        /// 但 A0/A1 大图幅会把圈号画到 20mm 以上，故默认放到 12mm（直径 24mm）。
        /// 超过此值的圆（放大镜视图、基准符号外圈）不当圈号。
        /// 另有「圈内数字必须装得进圈」的校验（数字高 ≤ 1.3 倍半径），大圆里没有数字不会被误认。
        /// </summary>
        public double CalloutMaxRadiusMm { get; set; } = 12.0;

        /// <summary>
        /// 圆度容差（默认 0.15）：圆上各点到圆心的最大 / 最小半径之比允许超出 1 的比例。
        /// 调大能容忍被引线打断的不圆整的圈；调小可排除椭圆、圆弧。
        /// 另有两项硬性条件不在此调节：包围盒宽高比 ≥ 0.90、八个方位必须全被覆盖。
        /// </summary>
        public double CalloutRadiusTolerance { get; set; } = 0.15;

        // ── 文字清单粒度 ──────────────────────────────────────────────────────

        /// <summary>
        /// 文字清单（`parts_text.txt` / `outside_text.txt` 的「其它未归属」段）是否
        /// **一个词一行**输出（默认开启）。
        ///
        /// 合并成一句虽然读起来像原图上的文字，但几个词挤在一条记录里、只带一个并集坐标
        /// （如 `①1 鴨居 G部`），下游按行解析时无法知道每个词各自的框与位置。
        /// 开启后一个 PDF 词就是一行：坐标是该词自己的外接框（y / x / w / h + 局部坐标），
        /// 文本是该词自身的文字。顺序仍是自上而下、自左而右，同一水平线上的词依旧相邻显示，
        /// 所以「原本是同一行」照样看得出来。
        ///
        /// 需要旧行为（同一水平线上的词拼成一句 + 并集坐标）时设为 false，
        /// 命令行对应 `--merge-text-lines`。表格区（`outside_text.txt` 第一段）
        /// 本来就是「表 → 行 → 单元格」，不受本开关影响。
        /// </summary>
        public bool SplitTextToWords { get; set; } = true;

        // ── 有线表格内容提取（单元格四面都有框线的表格）─────────────────────

        /// <summary>
        /// 是否从 PDF 矢量路径提取「有线表格」（单元格四面都被框线围住的表）内容并落盘
        /// （默认开启，仅 PDF 生效；位图没有矢量路径，自动跳过）。
        ///
        /// 与 <see cref="MaskTableBlocks"/>（按文字层推断表格块、只用于遮蔽）不同：
        /// 本开关走的是「只认框线」的准确路径 —— 横 / 竖框线围出的最小闭合矩形就是单元格，
        /// 相邻单元格按共用边界并成表，行列号与合并跨度都由几何位置决定，
        /// 因此空单元格不会错位、合并单元格也不会被拆列。
        ///
        /// 产物（写在 <see cref="OutputDirectory"/>）：
        ///   · <c>tables_info.json</c> ── 机器可读，与 <c>outside_info.json</c> 同构；
        ///   · <c>tables_text.txt</c>  ── 人眼可读，含框线画的二维表 + 单元格明细；
        ///   · <c>tables\table_NN.csv</c> ── Excel 可直接打开的表格内容。
        /// 只读，不影响零件框选 / 合并 / 命名。命令行对应 `--no-tables`。
        /// </summary>
        public bool ExtractRuledTables { get; set; } = true;

        /// <summary>
        /// 判定为表格框线所需的最短长度（毫米，默认 2.0）。
        ///
        /// 用来筛掉不是框线的短边：填充细矩形在线宽方向的两条短边（0.2~0.5mm）、
        /// 文字笔画、箭头、标注短线。表格线每一段至少有一个单元格那么长
        /// （格子边长 ≥ <see cref="RuledTableMinCellMm"/>），所以 2mm 不会误伤真框线。
        /// 图上有特别细密的表格（格子 &lt; 2mm）时才需要调小，但要同时调小
        /// <see cref="RuledTableMinCellMm"/>，否则形状闭合了也会被当噪声丢掉。
        /// </summary>
        public double RuledTableMinLineMm { get; set; } = 2.0;

        /// <summary>
        /// 共线容差（毫米，默认 0.6）：固定坐标（横线的 y / 竖线的 x）相差在此范围内、
        /// 且区间重叠或间隙也在容差内的线段，视为同一条框线并合并。
        ///
        /// CAD 导出的图纸上，同一条表格线常被画两遍（上一格的底边 + 下一格的顶边），
        /// 粗线还会变成「0.2mm 高的填充四边形」（两条长边相距 0.2mm）。
        /// 调大能把略有错位的框线并到一起；过大（&gt; 行高的 1/4）会把相邻两行并成一行。
        /// </summary>
        public double RuledTableLineToleranceMm { get; set; } = 0.6;

        /// <summary>
        /// 单元格最小边长（毫米，默认 2.0）：横 / 竖两边间距小于此值的候选矩形不认。
        /// 用来忽略重叠框线之间、粗框线内部被围出来的窄缝（0.2~1mm 宽），
        /// 避免一个格子被拆成一串细条。
        /// </summary>
        public double RuledTableMinCellMm { get; set; } = 2.0;

        /// <summary>
        /// 上下两条横线能配成一个单元格的最大间距（毫米，默认 120）。
        ///
        /// 合并单元格（特别是纵向跨多行的表題欄栏杆）只有上下两条长横线，
        /// 中间没有横线可依，所以这个上限要大于最高的合并单元格。
        /// 调大能容纳更高的合并格，但枚举量随带内横线数增长，过大只是变慢、不影响正确性。
        /// </summary>
        public double RuledTableMaxRowGapMm { get; set; } = 120.0;

        // ── PDF 信息清单（PdfInfoList.txt）────────────────────────────────────

        /// <summary>
        /// 是否生成「PDF 信息清单」<c>classify\PdfInfoList.txt</c>（默认开启，仅 PDF 生效）。
        ///
        /// 整批共用一份，放在输出根目录下的 <c>classify</c> 里：
        /// 单张是 <c>&lt;图纸目录&gt;\AiAppData\classify\PdfInfoList.txt</c>，
        /// 批量是 <c>&lt;输出根目录&gt;\classify\PdfInfoList.txt</c>（<b>不是</b>每张图纸各一份）。
        /// 一张图一行，固定四列：図番号 / 文件名 / 图面分类（基本図 / 加工図 / アッセンブリ図 / 要領図）/
        /// 分类依据；同一张图重跑时就地更新那一行。分类判定与 <c>drawing_list.csv</c> 同一套规则
        /// （在 <c>tables\*.csv</c> 的单元格里找「図番号前 4 位 + 关键字」）。
        ///
        /// 生成时机：<c>tables\*.csv</c> 落盘之后、<see cref="BuildLtStep1Table"/> 与
        /// <see cref="BuildLtStep2Table"/> 之前 —— 后两者要读它，<see cref="BuildLtStep1Table"/>
        /// 只有分类是「アッセンブリ図」时才写 <c>01_アッセンブリ図_部品機能情報.csv</c>，
        /// <see cref="BuildLtStep2Table"/> 只有分类是「要領図」时才写 <c>02_要領図_部品機能情報.csv</c>。
        /// 命令行对应 `--no-pdf-info-list`。
        /// </summary>
        public bool BuildPdfInfoList { get; set; } = true;

        /// <summary>
        /// 是否在表格 CSV 生成之后，再合成下游 AI 环节约定的
        /// <c>01_アッセンブリ図_部品機能情報.csv</c>（默认开启；旧文件名 <c>LT_STEP1.csv</c>）。
        ///
        /// 做法：遍历 <c>tables\table_NN.csv</c> ——
        ///   · 表头同时含「番号」「部品名」的是部品表（主表），取它的明细行；
        ///     「部品コード」可有可无：没有这一列的部品表也收，输出的「部品コード」格留空
        ///     （输出表头末尾会补一列，下游按固定列取数不会错位）；
        ///   · 明细行里「部品コード」写「下表参照」/「左上表参照」的，**先按表头找参照表**：
        ///     表头里同含「見込み」与品番列名（部品コード / 部品番号 / 品番 / 型式 / 型番）的
        ///     那张小表就是参照表 —— 图纸上它的标题就写着「見込み / 部品コード」；
        ///     **与它排在主表前面还是后面无关**：H467U10001 的参照表就排在主表 <c>table_01</c>
        ///     **后面**（<c>table_02</c>），按「左上 = 往前翻」永远找不到，那一行会原样留着
        ///     <c>左上表参照</c>；有多张这种表时取离主表最近的那张（同距离优先取排在后面的）；
        ///   · 找不到这种「見込み / 部品コード」表时才退回按方向翻（旧行为）：
        ///     「下表参照」往后、「左上表参照」往前，取第一张「表头含部品コード、且有明细」的表；
        ///   · 参照表有几条明细就把这一行拆成 N 条：只把「部品コード」换成参照表里的值，
        ///     其余列（番号 / 部品名 / 枠区分 / 員数 / 基本寸法 / 切断寸法）沿用原行；
        ///   · 合成结果按 <see cref="OutputDirectory"/> 写成
        ///     <c>01_アッセンブリ図_部品機能情報.csv</c>（带 BOM 的 UTF-8）。
        ///   · **只有图面分类为「アッセンブリ図」的图纸才写**：分类读自
        ///     <c>classify\PdfInfoList.txt</c>（<see cref="BuildPdfInfoList"/>），基本図 / 加工図 / 要領図 /
        ///     未识别 一律跳过，并清掉上一次留下的同名产物。
        /// 生成时机在 <see cref="BuildLtStep2Table"/> 之前（先出 <c>01_…</c>，再出 <c>02_…</c>；
        /// 只读 <c>tables\*.csv</c>）。
        /// 找不到部品表、或没有「下表参照 / 左上表参照」行时什么都不写（只记日志）。命令行对应 `--no-lt-step1`。
        /// </summary>
        public bool BuildLtStep1Table { get; set; } = true;

        /// <summary>
        /// 是否把「品種 + 多列重复」的表格摊平，合成下游 AI 环节约定的
        /// <c>02_要領図_部品機能情報.csv</c>（默认开启；旧文件名 <c>LT_STEP2.csv</c>）。
        ///
        /// 图纸上这类表把同一个品種的几组部品横向排开，表头重复出现：
        /// <code>
        /// 品種,部品名 ： 縦枠上部スペーサー,,部品名 ： 小縦枠上部スペーサー,,
        /// ,部品コード,員数,部品コード,員数
        /// 片引戸,MEACC428,1,MEACC428,2
        /// </code>
        /// 做法：遍历 <c>tables\table_NN.csv</c> ——
        ///   · 表头区里有含「品種」的单元格，且同一列名重复出现 ≥ 2 次的是品種表；
        ///   · 重复列名每次出现的列 = 一组部品ブロック，块标题（上方单元格里的「部品名 ： …」）就是品名；
        ///   · 摊平成 品種 / 部品名 / 部品コード / 員数 四列，品種按合并单元格往下填满；
        ///   · 另一种「品種 × 部品名 マトリクス表」（表头一排部品名、每行一个品種、格子里是品番，
        ///     <c>－</c> = 该品種不用这个部品，`H325U10011.pdf` 就是）也收：**一个非 `－` 格子一条明细**，
        ///     部品名取列名，員数 / 寸法留空 —— 一张 7 行 × 6 列的表就是 22 条明细；
        ///   · 另外还认「単純部品表」（<c>部品名 / 枠区分</c>）与「部品情報表」（<c>部品名 / 部品コード</c> 两列小表）；
        ///   · 结果按 <see cref="OutputDirectory"/> 写成 <c>02_要領図_部品機能情報.csv</c>（带 BOM 的 UTF-8）。
        ///   · **只有图面分类为「要領図」的图纸才写**：分类读自 <c>classify\PdfInfoList.txt</c>
        ///     （<see cref="BuildPdfInfoList"/>），基本図 / 加工図 / アッセンブリ図 / 未识别 一律跳过，
        ///     并清掉上一次留下的同名产物。
        /// 找不到这几种表时什么都不写（只记日志）。生成时机在 <see cref="BuildLtStep1Table"/> 之后
        /// （两张成品清单里最后出）。命令行对应 `--no-lt-step2`。
        /// </summary>
        public bool BuildLtStep2Table { get; set; } = true;

        /// <summary>
        /// 是否把「項目 / 要領図漢字 / 図番」这种表抽成下游 AI 环节约定的
        /// <c>01_アッセンブリ図_要領図.csv</c>（默认开启）。
        ///
        /// 图纸上这张小表列的是「这张組立図用到了哪些要領図」，样子（表头被拆散、中间那列没有标题）：
        /// <code>
        /// ,,,項目,,,,,,,,,,,,図番,
        /// ,,,"A,M",,枠 縦枠/小縦枠スペーサー取付要領図,,,,,,H310U10022,
        /// ,,,B,,枠 ロットNO.ラベル 貼付要領図,,,,,,H310U10034,
        /// </code>
        /// 做法：遍历 <c>tables\table_NN.csv</c> ——
        ///   · 表头（含「項目」与「図番」的那一行）同时出现这两个词的才是对象文件，其余表格跳过；
        ///   · 「項目」「図番」按列名找；「要領図漢字」多数图纸上没有标题，就取「項目」与「図番」
        ///     之间第一条有内容的列（表头里写了这个列名时优先按列名找）；
        ///   · 表头下面一行一条，读到「図番」为空或不是 10 位（<c>H310U10022</c> 这类）就停 ——
        ///     再往下是表題欄、変更履歴表之类的杂行；
        ///   · 有些图纸**不写表头**，把 項目 与 図番 连在同一格里（<c>A部詳細：H325U10011</c>）、
        ///     用点线隔开（<c>A部・・・H325U10011</c>）、或折成两行（<c>A部・・・</c> + 下一行 <c>H325U10011</c>）——
        ///     这类「内联写法」逐格扫 10 位 図番，図番 前面剥掉「部 / 詳細 / ： / ・・・」后剩下的
        ///     <c>A</c> / <c>B</c>（或 <c>A,M</c>）就是 項目，<b>要領図漢字 留空</b>；
        ///     只有 項目 没有 図番 的格子会跟紧跟着的 図番 配对。光秃秃的 図番（表題欄等）不收，
        ///     结构化路径已经收过的 図番 不重复收（内联只补漏）；
        ///   · **没画表格**的組立図（页面上只写着「下記に示す図面を参照のこと。/ A部詳細：H325U10011 …」）
        ///     改读整页文字行（<see cref="PartTextExtractor.ReadTextLines"/>）再抽一遍：`項目` 按图纸上
        ///     写的**保留到「部」**（`A部` / `B部` …），`要領図漢字` 留空；抽法是「先分别找出 `～部` 与
        ///     10 位 図番、再按位置配对」，两者之间写什么（`詳細` / `：` / `・・・` / 空格 / 注释）都不影响，
        ///     `A部詳細：` 与 図番 折成两行也配得上（往上 3 行内）；全角字母数字先按 Unicode FormKC
        ///     化成半角再认。同一个 図番 挂在两个 項目 上时两条都留，表格路径收过的 図番 不再收；
        ///     配上几条 / 重复合并几条 / 几条没配上 項目（连原文）都写进日志，收少了能一眼看出原因；
        ///   · 摊平成 項目 / 要領図漢字 / 図番 三列，按 <see cref="OutputDirectory"/> 写成
        ///     <c>01_アッセンブリ図_要領図.csv</c>（带 BOM 的 UTF-8 + CRLF）。
        ///   · **只有图面分类为「アッセンブリ図」的图纸才写**：分类读自 <c>classify\PdfInfoList.txt</c>
        ///     （<see cref="BuildPdfInfoList"/>），基本図 / 加工図 / 要領図 / 特注 一律跳过，
        ///     并清掉上一次留下的同名产物。**「未识别」是例外**：没画表格的图纸判不出分类，
        ///     此时页面上有「A部詳細：H…」这种要領図索引就按アッセンブリ図 处理（没有才跳过）。
        /// 生成时机在 <see cref="BuildLtStep1Table"/> 之后、<see cref="BuildLtStep2Table"/> 之前。
        /// 两条路都没收到时不生成文件（只记日志）。命令行对应 `--no-assembly-yoryo`。
        /// </summary>
        public bool BuildAssemblyYoryoList { get; set; } = true;

        /// <summary>
        /// 是否在流水线最后，把「图纸文件夹里的 Excel 部品表」抽成
        /// <c>classify\特注品.json</c>（默认开启）。
        ///
        /// 扫「图纸所在文件夹」下的所有 Excel（<c>.xls</c> / <c>.xlsx</c> / <c>.xlsm</c>，
        /// <c>~$</c> 临时文件与 <c>AiAppData</c> 里的产物跳过），读工作簿里「カメラ」表的部品表，
        /// 取这五列：<c>品種 / 部品名 / 枠区分 / 部品コード / 追加対応特注寸法</c>；
        /// 原表另有 <c>員数 / 数量 / 入数 / 個数</c> 列时一并取 <c>員数</c>（没有就留空）。
        /// 参考版式：<c>H310Z10004.XLS</c> 的「カメラ」sheet。
        ///
        /// 输出位置与 <see cref="BuildPdfInfoList"/> 的清单同一个目录、同样是整批共用一份：
        ///   · 单张：<c>&lt;图纸目录&gt;\AiAppData\classify\特注品.json</c>；
        ///   · 批量：<c>&lt;输出根目录&gt;\classify\特注品.json</c>（<b>不是</b>每张图纸各一份）。
        ///
        /// 读取用 ExcelDataReader（只读，不要求机器上装 Excel）。没有「カメラ」表的工作簿、
        /// 或表里凑不齐那 5 个列名时跳过并记日志；整批一张都没凑齐时不写文件，也不删旧文件。
        /// 命令行的 <c>--no-tz</c> 关掉这一步。
        /// </summary>
        public bool BuildTzJson { get; set; } = true;

        // ── Excel 部品表的 AI 判定与补足（特注品.json）─────────────────────────

        /// <summary>
        /// Excel 部品表（<c>classify\特注品.json</c>）什么时候请 AI 判定与补足（默认 <see cref="TzAiMode.Off"/>）。
        ///
        /// <para>
        /// Excel 的版式各家不同（表头拆行 / 列名改写 / 分块版式 / 合并单元格 / 多块表并排），
        /// <see cref="BuildTzJson"/> 的规则（5 列标题 + 分块版式）覆盖不到时一条明细都收不到。
        /// 打开后把工作表摊成「#行号 C列=值」的文本交给 AI 判「哪些行是明细、每个字段是什么」，
        /// 再把每个字段值<b>回原表逐字核对</b>（核对不上的丢掉，见 <see cref="TzAiExtractor"/>），
        /// 所以 AI 只负责「看懂版式」，不会凭空造出表里没有的字。
        /// </para>
        ///
        /// <para>
        /// 取值：<see cref="TzAiMode.Off"/>（默认，不调 AI）/ <see cref="TzAiMode.Fallback"/>
        /// （规则一条都没收到时才调，省钱）/ <see cref="TzAiMode.Always"/>（每张表都复核一遍，
        /// 规则漏掉的明细补进来）。命令行 <c>--tz-ai</c> / <c>--tz-ai always</c> / <c>--no-tz-ai</c>。
        /// </para>
        ///
        /// <para>
        /// 需要一个 OpenAI 兼容接口的密钥：环境变量 <c>MSIF_AI_API_KEY</c>、MSIFAITOOLS 的
        /// 【AI 设置】（<c>%APPDATA%\MSIFAITOOLS\ai-settings.json</c>）、或 App.config 的
        /// <c>AiApiKey</c>，任一处配好即可（与 MSIFAITOOLS 共用同一份也可）。
        /// 没配密钥时不会报错中断，只在日志里说明「AI 判定已跳过」。
        /// </para>
        /// </summary>
        public TzAiMode TzAiMode { get; set; } = GetDefaultTzAiMode();

        /// <summary>
        /// <see cref="TzAiMode"/> 的默认值：环境变量 <c>MSIF_TZ_AI</c>（或 <c>CADTZ_AI</c>）为
        /// <c>fallback</c> / <c>always</c> 时默认打开，否则 <see cref="TzAiMode.Off"/>。
        ///
        /// <para>
        /// 图形界面没有这个开关（避免给批量流程加一个会花时间 / 费用的选项），
        /// 想在界面里用时设环境变量最省事；命令行则直接用 <c>--tz-ai</c>。
        /// </para>
        /// </summary>
        private static TzAiMode GetDefaultTzAiMode()
        {
            try
            {
                string text = Environment.GetEnvironmentVariable("MSIF_TZ_AI");

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = Environment.GetEnvironmentVariable("CADTZ_AI");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return TzAiMode.Off;
                }

                text = text.Trim().ToLowerInvariant();

                if (text == "always" || text == "all")
                {
                    return TzAiMode.Always;
                }

                if (text == "fallback" || text == "1" || text == "true" || text == "on")
                {
                    return TzAiMode.Fallback;
                }
            }
            catch (Exception)
            {
                // 读环境变量失败按默认关闭处理
            }

            return TzAiMode.Off;
        }

        /// <summary>
        /// 一个工作簿最多请 AI 判几张表（默认 6，0 = 不限）。
        /// 工作簿里表很多时，只判前面几张（先「カメラ」，再按表序），避免一次读一堆无关表。
        /// </summary>
        public int TzAiMaxCallsPerFile { get; set; } = 6;

        /// <summary>
        /// 一次最多送给 AI 的工作表内容字符数（默认 24000，最小按 2000 处理）。
        /// 表格很长时后面的行会被截断（提示词里会注明），只影响 AI 看到的范围，不影响行号。
        /// 调大能覆盖更长的表，但调用更慢、费用更高。
        /// </summary>
        public int TzAiMaxGridChars { get; set; } = 24000;

        // ── 输出 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// AI 流程约定的输出文件夹名，放在所选图纸所在目录下；其内每张图纸再单独占一个
        /// 「图纸名」子文件夹（<c>&lt;图纸目录&gt;\AiAppData\&lt;图纸名&gt;</c>）。
        /// 下游 AI 环节按「目录 + 图纸名」直接取分割结果，不用再问「结果输出到哪儿了」。
        /// </summary>
        public const string AiAppDataFolderName = "AiAppData";

        /// <summary>
        /// 由输入文件推导默认输出目录（按第 1 页算）：
        /// <c>&lt;输入文件所在目录&gt;\AiAppData\&lt;主文件名&gt;</c>。
        /// </summary>
        public static string GetDefaultOutputDirectory(string inputFile)
        {
            return GetDefaultOutputDirectory(inputFile, 0);
        }

        /// <summary>
        /// 由输入文件推导默认输出目录：<c>&lt;输入文件所在目录&gt;\AiAppData\&lt;主文件名&gt;</c>。
        ///
        /// 图形界面选好文件后自动填这个路径，命令行不写输出目录时也用它，
        /// 这样「选了 <c>D:\图纸\H310A20009.pdf</c>」结果就固定落在
        /// <c>D:\图纸\AiAppData\H310A20009\</c>，同一目录下的多张图纸互不覆盖。
        ///
        /// 多页 PDF 逐页分割时，第 2 页起自动加 <c>_P页号</c> 后缀（如
        /// <c>H310A20009_P2</c>），避免后一页覆盖前一页的结果；
        /// 取不到文件名时（路径以 <c>\</c> 结尾、输入框里还是半截路径）退回 <c>AiAppData</c> 本身。
        /// </summary>
        /// <param name="inputFile">输入文件路径。</param>
        /// <param name="pageIndex">PDF 页码（0 起）；0 即第 1 页，不加后缀。</param>
        public static string GetDefaultOutputDirectory(string inputFile, int pageIndex)
        {
            string dir = null;

            if (!string.IsNullOrWhiteSpace(inputFile))
            {
                try
                {
                    // 界面上可能还在输入半截路径，取不到绝对路径就退回当前目录
                    dir = Path.GetDirectoryName(Path.GetFullPath(inputFile));
                }
                catch (ArgumentException)
                {
                    dir = null;
                }
                catch (NotSupportedException)
                {
                    dir = null;
                }
                catch (PathTooLongException)
                {
                    dir = null;
                }
            }

            if (string.IsNullOrEmpty(dir))
                dir = Environment.CurrentDirectory;

            string root = Path.Combine(dir, AiAppDataFolderName);
            string drawingFolder = BuildDrawingFolderName(inputFile, pageIndex);
            return string.IsNullOrEmpty(drawingFolder) ? root : Path.Combine(root, drawingFolder);
        }

        /// <summary>
        /// 批量模式：由输入文件夹推导默认的输出根目录 <c>&lt;输入文件夹&gt;\AiAppData</c>。
        ///
        /// 与单文件的 <see cref="GetDefaultOutputDirectory(string, int)"/> 落在同一个上级目录，
        /// 区别只是不再拼「图纸名」—— 批量时每张图纸各自一个子文件夹
        /// （<c>&lt;输入文件夹&gt;\AiAppData\&lt;图纸名&gt;</c>），由
        /// <see cref="BatchRunner"/> 按 <see cref="GetDrawingFolderName"/> 逐个补齐。
        /// </summary>
        /// <param name="inputDirectory">输入文件夹路径。</param>
        public static string GetDefaultBatchOutputDirectory(string inputDirectory)
        {
            string dir = null;

            if (!string.IsNullOrWhiteSpace(inputDirectory))
            {
                try
                {
                    dir = Path.GetFullPath(inputDirectory);
                }
                catch (ArgumentException)
                {
                    dir = null;
                }
                catch (NotSupportedException)
                {
                    dir = null;
                }
                catch (PathTooLongException)
                {
                    dir = null;
                }
            }

            if (string.IsNullOrEmpty(dir))
                dir = Environment.CurrentDirectory;

            return Path.Combine(dir, AiAppDataFolderName);
        }

        /// <summary>
        /// 取「图纸名」子文件夹名（批量模式给每个 PDF 分目录时用）：
        /// 主文件名（不含扩展名）；页码 &gt; 0 时补 <c>_P&lt;页码+1&gt;</c>。
        /// 取不到文件名时返回 null，调用方直接用输出根目录。
        /// </summary>
        /// <param name="inputFile">输入文件路径。</param>
        /// <param name="pageIndex">PDF 页码（0 起）；0 即第 1 页，不加后缀。</param>
        public static string GetDrawingFolderName(string inputFile, int pageIndex)
        {
            return BuildDrawingFolderName(inputFile, pageIndex);
        }

        /// <summary>
        /// 取「图纸名」子文件夹名：主文件名（不含扩展名）；页码 &gt; 0 时补 <c>_P&lt;页码+1&gt;</c>。
        /// 非法文件名字符替换为下划线（正常路径不会出现，防御手工拼的字符串）。
        /// 取不到文件名时返回 null，调用方退回 <see cref="AiAppDataFolderName"/> 本身。
        /// </summary>
        private static string BuildDrawingFolderName(string inputFile, int pageIndex)
        {
            string name;

            try
            {
                name = Path.GetFileNameWithoutExtension(inputFile);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
                return null;

            name = name.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            return pageIndex > 0 ? name + "_P" + (pageIndex + 1) : name;
        }

        /// <summary>
        /// 分割零件图的输出目录，不存在时自动创建。
        /// 留空（null / 空串）时取 <see cref="GetDefaultOutputDirectory(string, int)"/>，
        /// 即 <c>&lt;输入文件所在目录&gt;\AiAppData\&lt;主文件名&gt;</c>。
        /// </summary>
        public string OutputDirectory { get; set; }

        /// <summary>
        /// 是否保存中间处理图（二值图、膨胀图）用于参数调试。
        /// </summary>
        public bool SaveDebugImages { get; set; } = false;

        // ── 日志 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 日志输出回调。为 null 时写入控制台（命令行模式）；
        /// 图形界面模式下由 WinForms 窗体注入，将日志显示到界面日志面板。
        /// </summary>
        public Action<string> Logger { get; set; }

        /// <summary>
        /// 输出一行处理日志：优先调用 <see cref="Logger"/> 回调，否则写入控制台。
        /// </summary>
        public void Log(string message)
        {
            Action<string> logger = Logger;
            if (logger != null)
            {
                logger(message);
                return;
            }

            Console.WriteLine(message);
        }
    }
}
