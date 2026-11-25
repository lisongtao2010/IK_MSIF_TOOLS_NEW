using System.Reflection;
using System.Runtime.InteropServices;

// 有关程序集的常规信息通过以下
// 特性集控制。更改这些特性值可修改
// 与程序集关联的信息。
[assembly: AssemblyTitle("MSIFAITOOLS")]
[assembly: AssemblyDescription("MSIF AI Tools - PDF 分解工具")]
[assembly: AssemblyConfiguration("")]
// 公司名不能留空：留空时 exe 的「文件属性 → 详细信息」看起来就像来历不明的野包，
// 安全软件/SmartScreen 对这类文件评分更差。建议填公司或部门的正式名称，
// 并尽量与代码签名证书的主体（Subject）保持一致。
[assembly: AssemblyCompany("MSIF")]
[assembly: AssemblyProduct("MSIFAITOOLS - MSIF AI Tools")]
[assembly: AssemblyCopyright("Copyright © 2026 MSIF")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// 将 ComVisible 设置为 false 使此程序集中的类型
// 对 COM 组件不可见。如果需要从 COM 访问此程序集中的类型，
// 则将该类型上的 ComVisible 特性设置为 true。
[assembly: ComVisible(false)]

// 如果此项目向 COM 公开，则下列 GUID 用于类型库的 ID
[assembly: Guid("C9B682EF-105F-4478-81DD-A78C865A2E6C")]

// 程序集的版本信息由下面四个值组成:
//
//      主版本
//      次版本
//      生成号
//      修订号
//
// 可以指定所有这些值，也可以使用"生成号"和"修订号"的默认值，
// 方法是按如下所示使用"*":
// [assembly: AssemblyVersion("1.0.*")]
// 每次对外发版请递增这里的版本号：客户/安全软件看到「版本号永远不变、文件哈希
// 却天天变」的 exe，更容易按可疑程序处理（信誉也无法累积）。
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
