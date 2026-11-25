#Requires -Version 5.1
<#
.SYNOPSIS
    MSIFAITOOLS 正式发布：Release 构建 → 去掉开发文件 → Authenticode 代码签名（带时间戳）→ 打 zip。

.DESCRIPTION
    为什么需要这个脚本：
      直接分发 bin\Debug 里的 exe 是「杀毒软件最不信任」的形态 —— 未优化、带 .pdb
      （里面还有编译机的源码绝对路径）、没有数字签名。SmartScreen 会弹「Windows 已保护
      你的电脑」，Defender 也可能报 Trojan:Win32/Wacatac.B!ml 之类的机器学习误报。
      本脚本一次做完下面几件事，尽量让发布包看起来/实际上就是一份正规软件：

        1) 用 Release 配置 + MSBuild 正式构建（/p:Configuration=Release），并把
           「零件分割」子程序 CadPartSplitter 一起构建（输出落在 bin\<配置>\CadPartSplitter\，
           客户机上【开始分解】第 1 步要用）；
        2) 复制到 artifacts\publish\<程序名>_<版本>_<时间>\，并剔除 *.pdb / *.xml 等
           只应留在开发机上的文件；
        3) 对包内自己的 exe（MSIFAITOOLS.exe / CadPartSplitter.exe）做 Authenticode 签名
           （SHA-256 + RFC3161 时间戳），并逐文件 signtool verify 校验；
        4) 生成「发布信息.txt」（版本、构建时间、签名主体、每个文件的 SHA-256，便于
           向安全厂商提交误报时附上）并打包成 zip。

    误报对策的完整说明（证书怎么选、怎么向 Microsoft / 各个杀毒厂商提交误报）见
    docs\病毒误报_对策.md。

.PARAMETER SecretMode
    发布包里 exe.config 的密钥处理方式（默认 BlankAiKey）：
      BlankAiKey = 清空 AiApiKey，使用者各自在【AI 设置】填自己的 Key，SQL 连接保留（内部同事）；
      BlankAll   = 再清空 SQL 密码（给公司外 / 演示环境）；
      Keep       = 原样不动（只在本机自用或完全可信的分发时用）。
    只改发布目录里的副本，仓库里的 App.config 不会被修改。

.PARAMETER Configuration
    构建配置，默认 Release。请勿用 Debug 对外发布。

.PARAMETER CertificatePath
    代码签名证书文件（.pfx）。不传密码时脚本会以安全方式提示输入。
    注意：工程里现存的 msif.pfx / cad.pfx 是 csproj 里的「强名称密钥」，不是 Authenticode
    代码签名证书（脚本会先校验 EKU 并直接拒绝），详见 docs\病毒误报_对策.md 第 4 章。

.PARAMETER CertificatePassword
    .pfx 密码（明文；仅用于本机命令行，注意命令行历史）。

.PARAMETER CertificateThumbprint
    也可以不传 pfx，改用「当前用户/本机证书存储」里的证书，按 SHA-1 指纹选择。

.PARAMETER TimestampUrl
    时间戳服务器，默认 http://timestamp.digicert.com。内网不通可换
    http://timestamp.sectigo.com、http://timestamp.digicert.com，或传空字符串跳过时间戳。

.PARAMETER OutputRoot
    发布根目录，默认 <工程目录>\artifacts\publish。

.PARAMETER SkipBuild
    不重新构建，直接用现有 bin\<Configuration> 的内容打包（调试脚本用）。

.PARAMETER SkipSplitter
    不构建「零件分割」子程序 CadPartSplitter。客户机上【开始分解】第 1 步要调用
    <程序目录>\CadPartSplitter\CadPartSplitter.exe，缺了它这一步会被直接跳过，
    所以对外发布不要加这个开关。

.PARAMETER SplitterProject
    分割工程的 csproj 路径，默认 <仓库根>\CadPartSplitter\CadPartSplitter.csproj。
    它的输出目录已在它自己的工程里指向 MSIFAITOOLS\bin\<配置>\CadPartSplitter\，
    因此先构建它，下面复制 bin\<配置> 时就会把整个分割程序一起带上。

.PARAMETER SkipSign
    不签名，只做「干净打包」。仅用于内网测试；对外发布必须去掉此开关。

.PARAMETER KeepPdb
    保留 .pdb（默认剔除）。仅在需要收集崩溃堆栈时给内部测试包使用。

.PARAMETER IncludeTrustedPublisher
    把「信任发布者」材料一起打进包里：`安装信任_双击我.bat`（接收人双击这个入口）+
    `Install-TrustedPublisher.ps1`（真正干活的脚本）+ `trusted-publisher.cer`（签名证书的公钥部分）+
    `先装信任_再运行.txt`（含主体 / 指纹 / cer 的 SHA-256，便于核对）。

    用途：用**自签证书**或内网 CA 证书签名、但接收方机器不信任该根证书时，
    接收人先双击一次 `安装信任_双击我.bat`（装进「当前用户\受信任的根证书颁发机构」，**不需要管理员**），
    之后 exe 的「发布者」才会显示证书主体名，`Get-AuthenticodeSignature` 才是 Valid。
    对公网用户没有意义（他们不该信任你的私有根），只在公司内部分发时使用。

    那个 .bat 是打包时按当次签名证书**现生成的**，指纹写死在命令行里：cer 被替换会被脚本拒绝。
    撤销信任：`安装信任_双击我.bat -Uninstall`。

    用 `-SelfSigned` 时会自动打开这个开关，不用手工加。

.PARAMETER SelfSigned
    不想记指纹时用：自动从 `Cert:\CurrentUser\My` 里挑「自签 + 有私钥 + 带 Code Signing EKU + 未过期」
    的证书（多张时取有效期最晚的），并自动附带「信任发布者」材料。

    本机还没有这种证书时先跑一次：`.\Tools\New-SelfSignedCodeSigningCert.ps1`。
    日常发布只需双击项目根目录的 `发布.bat`，它就是这个参数的最简入口。

.EXAMPLE
    # 标准发布：用代码签名证书（带 Code Signing EKU 的 pfx）签名，会提示输入密码
    .\Tools\Publish-Signed.ps1 -CertificatePath .\code-signing.pfx

.EXAMPLE
    # 证书已装在证书存储里，按指纹签名
    .\Tools\Publish-Signed.ps1 -CertificateThumbprint 1A2B3C4D5E6F...

.EXAMPLE
    # 先验证流水线是否正常（不签名，只打包）
    .\Tools\Publish-Signed.ps1 -SkipSign
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $CertificateThumbprint,
    [switch] $SelfSigned,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [ValidateSet('Keep', 'BlankAiKey', 'BlankAll')]
    [string] $SecretMode = 'BlankAiKey',
    [string] $OutputRoot,
    [string] $SplitterProject,
    [switch] $SkipBuild,
    [switch] $SkipSplitter,
    [switch] $SkipSign,
    [switch] $KeepPdb,
    [switch] $IncludeTrustedPublisher
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProjectName = 'MSIFAITOOLS'

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host "== $Text " -ForegroundColor Cyan
}

# PowerShell 5.1 的 Set-Content / Out-File 会按**控制台宽度**折行（长命令会被断成两行、
# 哈希清单复制时多出换行），所以生成文件一律走字节写入：写进去是什么，就是什么。
function Write-LinesFile {
    param(
        [string] $Path,
        $Lines,
        [ValidateSet('UTF8', 'ASCII')] [string] $Encoding = 'UTF8'
    )
    $text = (@($Lines) | ForEach-Object { [string]$_ }) -join "`r`n"
    $text = $text + "`r`n"
    $enc = if ($Encoding -eq 'ASCII') {
        New-Object System.Text.ASCIIEncoding
    } else {
        New-Object System.Text.UTF8Encoding($true)   # 带 BOM，老记事本也能正确识别
    }
    [System.IO.File]::WriteAllText($Path, $text, $enc)
}

# ---------------------------------------------------------------- 路径解析
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path      # <工程>\Tools
$projectDir  = Split-Path -Parent $scriptDir                        # <工程>
$repoRoot    = Split-Path -Parent $projectDir                       # 仓库根（含 CadPartSplitter\）
$projectPath = Join-Path $projectDir ($ProjectName + '.csproj')
$buildDir    = Join-Path $projectDir ('bin\' + $Configuration)

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "找不到工程文件：$projectPath"
}

if (-not $SplitterProject) {
    $SplitterProject = Join-Path $repoRoot 'CadPartSplitter\CadPartSplitter.csproj'
}

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $projectDir 'artifacts\publish'
}

# ---------------------------------------------------------------- 工具定位
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
                            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($found -and (Test-Path -LiteralPath $found)) {
            return $found
        }
    }

    # 兜底：.NET Framework 自带的 MSBuild（能编 .NET Framework 工程，但可能找不到 NuGet 目标）
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
    if (Test-Path -LiteralPath $framework) {
        return $framework
    }

    throw '找不到 MSBuild.exe。请安装 Visual Studio 2022（含「.NET 桌面开发」工作负载）后重试。'
}

function Find-SignTool {
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\8.1\bin'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\8.0\bin')
    )

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }

        # 10.x 的目录结构是 bin\<版本>\x64\signtool.exe，按路径倒序取最新版本；优先 x64
        $hits = Get-ChildItem -LiteralPath $root -Filter 'signtool.exe' -Recurse -File -ErrorAction SilentlyContinue

        $x64 = $hits | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($x64) { return $x64.FullName }

        $any = $hits | Sort-Object FullName -Descending | Select-Object -First 1
        if ($any) { return $any.FullName }
    }

    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    return $null
}

function Invoke-SignToolCommand {
    param(
        [string]   $SignTool,
        [string[]] $Arguments
    )

    # PS 5.1 里原生命令只要往 stderr 写了东西（signtool 的警告、校验失败提示都走 stderr），
    # 在 $ErrorActionPreference='Stop' 下就会被当成终止性错误（NativeCommandError），
    # 脚本会在检查 $LASTEXITCODE 之前就崩掉。这里临时关掉 Stop，把 stdout+stderr 一起收下来。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $text = (& $SignTool @Arguments 2>&1 | Out-String)
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

function ConvertTo-PlainText {
    param([System.Security.SecureString] $Secure)

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)

    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Assert-CodeSigningCertificate {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate
    )

    if (-not $Certificate) {
        throw '没能取到证书对象，无法判断它是否可以用来做代码签名。'
    }

    Write-Host ("    证书主体　：{0}" -f $Certificate.Subject)
    Write-Host ("    颁发者　　：{0}" -f $Certificate.Issuer)
    Write-Host ("    有效期　　：{0} 〜 {1}" -f $Certificate.NotBefore.ToString('yyyy-MM-dd'), $Certificate.NotAfter.ToString('yyyy-MM-dd'))

    # Authenticode（signtool）只接受带「扩展密钥用法 = Code Signing (1.3.6.1.5.5.7.3.3)」的证书。
    # 强名称（strong name）用的测试证书、EFS 证书、TLS 证书都不带这个 EKU；到了 signtool
    # 那一步只会得到一句看不懂的报错，所以在真正签名之前先自己判一次，直接说清原因。
    $ekuExtension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }

    $ekuText = ''
    if ($ekuExtension) {
        $ekuText = ($ekuExtension.Format($false) -replace '\s+', ' ').Trim()
    }

    if ($ekuText -notlike '*1.3.6.1.5.5.7.3.3*') {
        if (-not $ekuText) { $ekuText = '（没有 EKU 扩展）' }

        throw ("这个证书不能用于代码签名：缺少 EKU「Code Signing (1.3.6.1.5.5.7.3.3)」，实际是 $ekuText`n" +
               "      本工程里的 msif.pfx / cad.pfx 在 csproj 里是 <AssemblyOriginatorKeyFile> 指定的「强名称密钥」`n" +
               "      （而且 SignAssembly=false），不是 Authenticode 代码签名证书 —— 即使拿到密码，`n" +
               "      签出来的文件在客户机上依然显示「未知发布者」。`n" +
               "      可用路径见 docs\病毒误报_对策.md 第 4 章（公司内部 CA lixil-CA / OV / EV）。")
    }

    if ($Certificate.NotAfter -lt (Get-Date)) {
        throw ("证书已于 {0} 过期，用它签名的文件客户机不会信任。请先续期。" -f $Certificate.NotAfter.ToString('yyyy-MM-dd'))
    }
}

# ---------------------------------------------------------------- 1) 构建
if (-not $SkipBuild) {
    $msbuild = Find-MSBuild

    Write-Step "[1/5] 构建 $Configuration（$msbuild）"

    # Restore 与 Build 必须分成两次 MSBuild 调用。
    # 写成 /t:Restore;Build 时，项目求值（读取 obj\*.nuget.g.props / .targets）发生在
    # Restore 之前，用的还是「上一次」的依赖图：刚增删过 PackageReference 时，会按旧依赖
    # 复制文件（例如已经从 csproj 删掉的 OpenCvSharp 原生库仍被拷进输出目录）。
    & $msbuild $projectPath '/t:Restore' "/p:Configuration=$Configuration" '/v:m' '/nologo'

    if ($LASTEXITCODE -ne 0) {
        throw "NuGet 还原失败（退出码 $LASTEXITCODE）。请检查网络 / 私有源配置。"
    }

    & $msbuild $projectPath '/t:Build' "/p:Configuration=$Configuration" '/v:m' '/nologo'

    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild 构建失败（退出码 $LASTEXITCODE）。请先确认 VS 里能正常编译该工程。"
    }

    # 「零件分割」子程序必须一起构建：客户机上【开始分解】第 1 步就是执行
    # <程序目录>\CadPartSplitter\CadPartSplitter.exe（找不到就整步跳过）。
    # 它自己的工程把输出目录指到了 MSIFAITOOLS\bin\<配置>\CadPartSplitter\，
    # 所以先构建它，下面复制 bin\<配置> 时就会自然带上。
    if ($SkipSplitter) {
        Write-Host '    - 跳过 CadPartSplitter（-SkipSplitter）：包里可能不含分割程序' -ForegroundColor Yellow
    }
    elseif (-not (Test-Path -LiteralPath $SplitterProject)) {
        Write-Warning "找不到分割工程：$SplitterProject"
        Write-Warning '    包内将不含 CadPartSplitter，客户机上【开始分解】第 1 步会被跳过（可用 -SplitterProject 指定路径）。'
    }
    else {
        Write-Host "    - 构建分割程序：$SplitterProject"

        & $msbuild $SplitterProject '/t:Restore' "/p:Configuration=$Configuration" '/v:m' '/nologo'

        if ($LASTEXITCODE -ne 0) {
            throw "CadPartSplitter NuGet 还原失败（退出码 $LASTEXITCODE）。"
        }

        & $msbuild $SplitterProject '/t:Build' "/p:Configuration=$Configuration" '/v:m' '/nologo'

        if ($LASTEXITCODE -ne 0) {
            throw "CadPartSplitter 构建失败（退出码 $LASTEXITCODE）。请先确认分割工程能单独编译。"
        }
    }
}
else {
    Write-Step "[1/5] 跳过构建（-SkipBuild）：直接用现有 bin\$Configuration（含其中的 CadPartSplitter\）打包"
}

$exePath = Join-Path $buildDir ($ProjectName + '.exe')

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "找不到构建产物：$exePath"
}

$version = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
if (-not $version) { $version = '0.0.0.0' }
$version = $version.Trim()

# ---------------------------------------------------------------- 2) 复制/清理
Write-Step "[2/5] 生成发布目录（版本 $version）"

$publishDir = Join-Path $OutputRoot ('{0}_{1}_{2}' -f $ProjectName, $version, (Get-Date -Format 'yyyyMMdd-HHmmss'))

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# 只应留在开发机上的文件：pdb（含源码绝对路径）、xml 文档、临时/日志/数据库
$excludePattern = '\.(pdb|xml|log|tmp|cache|ilk|exp|iobj|ipdb)$|\.vshost\.|\.db$|\.db-journal$|\.db-wal$|\.db-shm$'
if ($KeepPdb) {
    $excludePattern = '\.(xml|log|tmp|cache|ilk|exp|iobj|ipdb)$|\.vshost\.|\.db$|\.db-journal$|\.db-wal$|\.db-shm$'
}

$sourceFiles = Get-ChildItem -LiteralPath $buildDir -Recurse -File |
               Where-Object { $_.Name -notmatch $excludePattern }

foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($buildDir.Length).TrimStart('\')
    $destination = Join-Path $publishDir $relative
    $destinationDir = Split-Path -Parent $destination

    if (-not (Test-Path -LiteralPath $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}

$published = Get-ChildItem -LiteralPath $publishDir -Recurse -File
$publishedBytes = ($published | Measure-Object -Property Length -Sum).Sum

Write-Host ("    文件 {0} 个，合计 {1:N1} MB → {2}" -f $published.Count, ($publishedBytes / 1MB), $publishDir)

if (-not (Test-Path -LiteralPath (Join-Path $publishDir ($ProjectName + '.exe')))) {
    throw '发布目录里没有主程序 exe，构建输出目录结构可能变了。'
}

# 分割程序自检：客户机上【开始分解】第 1 步要执行 <程序目录>\CadPartSplitter\CadPartSplitter.exe
$splitterExe = Join-Path $publishDir 'CadPartSplitter\CadPartSplitter.exe'
if (-not (Test-Path -LiteralPath $splitterExe)) {
    Write-Warning '发布包里没有 CadPartSplitter\CadPartSplitter.exe：客户机上【开始分解】第 1 步会被跳过。'
    Write-Warning '    请先构建分割工程（不加 -SkipSplitter），或把已构建好的 CadPartSplitter\ 目录放进 bin\<配置>\ 后重跑。'
}

# 体积自检（只统计主程序本体，不含 CadPartSplitter）：2026-11 删掉 OpenCvSharp4 后应在 40 MB 左右。
# 明显超标通常说明 csproj 里又引用了带巨大原生库的包，或者 bin\<配置> 里残留旧产物。
$splitterPrefix = Join-Path $publishDir 'CadPartSplitter'
$mainFiles = $published | Where-Object { -not $_.FullName.StartsWith($splitterPrefix, [System.StringComparison]::OrdinalIgnoreCase) }
$mainBytes = ($mainFiles | Measure-Object -Property Length -Sum).Sum

Write-Host ("    其中主程序本体 {0:N1} MB，分割程序 {1:N1} MB" -f ($mainBytes / 1MB), (($publishedBytes - $mainBytes) / 1MB))

if ($mainBytes -gt 60MB) {
    Write-Warning ("主程序本体 {0:N1} MB，明显大于预期（~40 MB）。最大的几个文件：" -f ($mainBytes / 1MB))

    foreach ($big in ($mainFiles | Sort-Object Length -Descending | Select-Object -First 5)) {
        Write-Warning ("    {0,7:N1} MB  {1}" -f ($big.Length / 1MB), $big.FullName.Substring($publishDir.Length).TrimStart('\'))
    }

    if ($mainFiles | Where-Object { $_.Name -match '^OpenCvSharpExtern\.dll$|^opencv_videoio' }) {
        Write-Warning '    检测到 OpenCvSharp 原生库（OpenCvSharpExtern.dll / opencv_videoio_*.dll）：'
        Write-Warning '    说明 MSIFAITOOLS.csproj 仍引用着 OpenCvSharp4* 包，或 bin\<配置> 里残留旧产物。'
        Write-Warning '    请删掉引用后执行 MSBuild /t:Rebuild 再重新打包（这 87 MB 既浪费体积也扩大误报面）。'
    }
}

# ---------------------------------------------------------------- 2.5) 配置脱敏（分发前必做）
# bin\<配置>\MSIFAITOOLS.exe.config 是 App.config 的副本，里面带着开发机用的 AiApiKey
# 和 SQL Server 明文密码，而这个文件会被一起打进发布包 —— 不处理就等于把密钥随程序送人。
# 这里只改发布目录里的副本，仓库里的 App.config 不动（开发机继续用自己的 Key）。
$configPath = Join-Path $publishDir ($ProjectName + '.exe.config')
$secretNote = '保留原样（-SecretMode Keep）'

if (-not (Test-Path -LiteralPath $configPath)) {
    $secretNote = '没有 exe.config（未处理）'
    Write-Warning '发布目录里没有 MSIFAITOOLS.exe.config：配置将全部走程序内置默认值。'
}
elseif ($SecretMode -eq 'Keep') {
    Write-Step '[2.5/5] 配置脱敏：-SecretMode Keep → 跳过，密钥会随包分发'
    Write-Warning '包里的 exe.config 仍含 AiApiKey / 数据库密码，只适合本机自用或完全可信的分发。'
}
else {
    Write-Step "[2.5/5] 配置脱敏（$SecretMode）：清空包内 exe.config 的密钥"

    $configText = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
    $originalText = $configText

    # AiApiKey 一定清空：使用者改用自己的 Key（【AI 设置】画面，存到各自的 %APPDATA%\MSIFAITOOLS）
    $configText = [regex]::Replace($configText, '(<add\s+key="AiApiKey"\s+value=")[^"]*(")', '$1$2')

    if ($SecretMode -eq 'BlankAll') {
        # 数据库密码：appSettings 的 MappingDbPassword + connectionStrings 里的 Password=
        $configText = [regex]::Replace($configText, '(<add\s+key="MappingDbPassword"\s+value=")[^"]*(")', '$1$2')
        $configText = [regex]::Replace($configText, '(?i)(password=)[^;"]*', '$1')
    }

    if ($configText -ne $originalText) {
        Set-Content -LiteralPath $configPath -Value $configText -Encoding UTF8
    }

    # 自检：确认真的清掉了（防止正则没匹配上却当成成功）
    $configText = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
    $leaks = @()

    if ($configText -match '<add\s+key="AiApiKey"\s+value="[^"]+"') {
        $leaks += 'AiApiKey'
    }

    if ($SecretMode -eq 'BlankAll' -and $configText -match '(?i)password=[^;"]+') {
        $leaks += 'SQL 密码'
    }

    if ($leaks.Count -gt 0) {
        throw ('配置脱敏失败：exe.config 里仍有 ' + ($leaks -join '、') + '，请手工检查 ' + $configPath)
    }

    if ($SecretMode -eq 'BlankAll') {
        $secretNote = 'AiApiKey / SQL 密码已清空（公司外 / 演示用）'
    }
    else {
        $secretNote = 'AiApiKey 已清空（使用者自己填 Key；DB 连接保留）'
    }

    Write-Host ('    ' + $secretNote)
}

# ---------------------------------------------------------------- 3) 代码签名
$signatureStatus = 'NotSigned'

# 「随包附带信任材料」是否启用：-IncludeTrustedPublisher 或 -SelfSigned 任一为真即启用。
$wantTrustBundle = [bool]$IncludeTrustedPublisher

if ($SkipSign) {
    Write-Step '[3/5] 跳过签名（-SkipSign）：此包只适合内部测试，不要对外分发'
}
else {
    $signTool = Find-SignTool

    if (-not $signTool) {
        throw ('找不到 signtool.exe。请安装 Windows 10/11 SDK（或 Windows 8.1 SDK）后重试；' +
               '只想先打包验证流程可加 -SkipSign。')
    }

    Write-Step "[3/5] Authenticode 签名（$signTool）"

    # -SelfSigned：不需要记指纹。挑「自签 + 有私钥 + Code Signing EKU + 未过期」的证书，
    # 多张时取有效期最晚的一张；同时自动打开「信任发布者」随包附带。
    if ($SelfSigned) {
        $selfSignedCandidates = @(
            Get-ChildItem -Path 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue |
            Where-Object {
                $_.HasPrivateKey -and
                $_.Subject -eq $_.Issuer -and
                $_.NotAfter -gt (Get-Date) -and
                ($_.Extensions | Where-Object {
                    $_.Oid.Value -eq '2.5.29.37' -and $_.Format($false) -match '1\.3\.6\.1\.5\.5\.7\.3\.3'
                })
            } |
            Sort-Object NotAfter -Descending
        )

        if ($selfSignedCandidates.Count -eq 0) {
            throw ('本机没有可用的自签代码签名证书（需要：有私钥 + 自签 + Code Signing EKU + 未过期）。' +
                   "先生成一张：.\Tools\New-SelfSignedCodeSigningCert.ps1")
        }

        $chosenCert = $selfSignedCandidates[0]
        if ($selfSignedCandidates.Count -gt 1) {
            Write-Host ('    本机有 {0} 张自签代码签名证书，自动取有效期最晚的一张' -f $selfSignedCandidates.Count)
        }

        $CertificateThumbprint = $chosenCert.Thumbprint
        Write-Host ('    -SelfSigned：{0}（{1}，{2} 到期）' -f $chosenCert.Subject, $chosenCert.Thumbprint, $chosenCert.NotAfter.ToString('yyyy-MM-dd'))

        if (-not $wantTrustBundle) {
            $wantTrustBundle = $true
            Write-Host '    自签证书对方计算机默认不信任 → 已自动附带「信任发布者」材料（要关掉请显式加 -IncludeTrustedPublisher:$false 且不用 -SelfSigned）'
        }
    }

    $certArgs = @()

    if ($CertificateThumbprint) {
        $certArgs = @('/sha1', $CertificateThumbprint)
        Write-Host "    证书来源：证书存储，SHA-1 指纹 $CertificateThumbprint"

        $storeCert = Get-ChildItem -Path ('Cert:\CurrentUser\My\' + $CertificateThumbprint) -ErrorAction SilentlyContinue
        if (-not $storeCert) {
            $storeCert = Get-ChildItem -Path ('Cert:\LocalMachine\My\' + $CertificateThumbprint) -ErrorAction SilentlyContinue
        }

        if (-not $storeCert) {
            throw "证书存储（当前用户 / 本机）里找不到指纹为 $CertificateThumbprint 的证书。"
        }

        if (-not $storeCert.HasPrivateKey) {
            throw ("证书 $CertificateThumbprint 没有可用的私钥（HasPrivateKey=false）：" +
                   '硬件 token 是否插好？导入 pfx 时是否漏勾了「导出私钥」？')
        }

        Assert-CodeSigningCertificate -Certificate $storeCert
    }
    elseif ($CertificatePath) {
        $resolvedCert = (Resolve-Path -LiteralPath $CertificatePath).Path

        if (-not $CertificatePassword) {
            $secure = Read-Host -Prompt ("  输入 {0} 的密码" -f [System.IO.Path]::GetFileName($resolvedCert)) -AsSecureString
            $CertificatePassword = ConvertTo-PlainText -Secure $secure
        }

        $certArgs = @('/f', $resolvedCert, '/p', $CertificatePassword)
        Write-Host "    证书来源：文件 $resolvedCert"

        # 先解出证书本体做用途校验：密码不对 / 不是代码签名证书 / 已过期，都在这里给出明确原因，
        # 不必等 signtool 抛一句看不懂的错。
        # 注意：PowerShell 5.1 的 Get-PfxCertificate 没有 -Password 参数（PS 7 才有），
        # 只能用 .NET 加载；EphemeralKeySet 表示私钥只在内存里用，不在用户配置里留密钥容器。
        try {
            $pfxCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
                $resolvedCert,
                $CertificatePassword,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        }
        catch {
            $reason = $_.Exception.Message
            if ($_.Exception.InnerException) { $reason = $_.Exception.InnerException.Message }

            throw ("打不开证书文件 {0}：密码可能不正确，或文件已损坏（{1}）" -f $resolvedCert, $reason)
        }

        Assert-CodeSigningCertificate -Certificate $pfxCert
    }
    else {
        $certArgs = @('/a')
        Write-Host '    证书来源：未指定，按「代码签名」用途从证书存储里自动挑选（/a）'
    }

    $timestampArgs = @()
    if ($TimestampUrl) {
        $timestampArgs = @('/tr', $TimestampUrl, '/td', 'sha256')
        Write-Host "    时间戳服务器：$TimestampUrl"
    }
    else {
        Write-Host '    未使用时间戳：证书一到期，已经发出去的签名就会失效（不建议）'
    }

    # 只签「自己的」可执行文件：主程序 MSIFAITOOLS.exe 和分割程序 CadPartSplitter.exe。
    # 第三方 dll（OpenCvSharp / PdfPig / System.Data.SQLite …）不是我们发布的产物，
    # 用本公司的证书去签它们没有意义；真正影响 SmartScreen 的是用户双击的那两个 exe。
    # （如需连第三方 dll 一起签，把 Where-Object 的条件去掉即可。）
    $signTargets = Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter '*.exe' |
                   Where-Object { $_.Name -eq ($ProjectName + '.exe') -or $_.Name -eq 'CadPartSplitter.exe' }

    foreach ($target in $signTargets) {
        Write-Host ("    签名 {0}" -f $target.Name)

        $signArgs = @('sign', '/fd', 'sha256', '/v') + $timestampArgs + $certArgs + @($target.FullName)
        $signResult = Invoke-SignToolCommand -SignTool $signTool -Arguments $signArgs
        Write-Host ($signResult.Output.TrimEnd())

        if ($signResult.ExitCode -ne 0) {
            $failureHint = "签名失败：{0}（signtool 退出码 {1}）。常见原因：`n" +
                           "      * 证书用途不符（必须带 EKU 1.3.6.1.5.5.7.3.3 Code Signing）——`n" +
                           "        脚本上一步已预检过，还能走到这里通常是私钥/权限问题；`n" +
                           "      * 证书已过期，或私钥没有可导出的权限；`n" +
                           "      * 时间戳服务器连不上（公司代理/内网），可换 -TimestampUrl 或传空字符串；`n" +
                           "      * signtool 版本太旧（Windows 8.0 SDK 不支持 /fd sha256）→ 装 Windows 10/11 SDK。"

            throw ($failureHint -f $target.Name, $signResult.ExitCode)
        }
    }

    # 校验分两层，结果不一样，别混：
    #   * 签名有没有真的写进去、文件有没有被改 → Get-AuthenticodeSignature（不看证书链）；
    #   * 证书链能不能在**这台**机器上走通 → signtool verify /pa（取决于本机受信商店）。
    # 自签证书、或内网 CA 但本机没装根证书时，第二层必然失败
    # （"terminated in a root certificate which is not trusted by the trust provider"）。
    # 这属于预期情况：包确实签过名，目标机装了根证书就能验通。所以只报警告、不中断打包，
    # 并把真实状态写进「发布信息.txt」，免得误以为「签了名就免 SmartScreen」。
    $chainWarnings = @()

    foreach ($target in $signTargets) {
        $verifyResult = Invoke-SignToolCommand -SignTool $signTool -Arguments @('verify', '/pa', '/q', $target.FullName)

        if ($verifyResult.ExitCode -eq 0) { continue }

        $signed = Get-AuthenticodeSignature -LiteralPath $target.FullName

        # HashMismatch = 签名被写坏/文件被改过，这个必须报错；UnknownError / NotTrusted 是「链不受信」
        if ($signed.SignerCertificate -and ($signed.Status -eq 'UnknownError' -or $signed.Status -eq 'NotTrusted')) {
            $chainWarnings += $target.Name
            Write-Warning ("{0} 已写入签名（主体：{1}），但证书链在本机不受信（状态 {2}）。" -f
                           $target.Name, $signed.SignerCertificate.Subject, $signed.Status)
            continue
        }

        throw "签名校验失败：$($target.FullName)（状态 $($signed.Status)）"
    }

    $mainStatus = (Get-AuthenticodeSignature -LiteralPath (Join-Path $publishDir ($ProjectName + '.exe'))).Status

    if ($mainStatus -eq 'Valid') {
        $signatureStatus = 'Valid'
        Write-Host "    主程序签名状态：Valid" -ForegroundColor Green
    }
    elseif (@($chainWarnings).Count -gt 0) {
        $signatureStatus = $mainStatus.ToString() + '（签名已写入，但根证书在本机不受信）'
        Write-Host "    主程序签名状态：$signatureStatus" -ForegroundColor Yellow
        Write-Host '    这是自签证书 / 内网 CA 且本机未安装根证书时的预期结果：' -ForegroundColor Yellow
        Write-Host '    目标机装上根证书（GPO）后才显示「发布者」；对外分发仍需正式证书（docs\代码签名_IT申请.md）。' -ForegroundColor Yellow
    }
    else {
        $signatureStatus = $mainStatus.ToString()
        Write-Warning ("签名状态不是 Valid（当前 $signatureStatus）：请确认证书链能在这台机器上通过校验，" +
                       '否则客户机上依然会被当成未签名程序。')
    }
}

# ------------------------------------------- 3.5) 随包附带「信任发布者」材料
$trustBundleNote = '未附带'

if ($wantTrustBundle) {
    Write-Step '[3.5/5] 附带「信任发布者」材料（接收人装一次信任后才显示发布者）'

    # 签名证书直接从包内主程序的签名里取：指纹 / pfx / 自动挑选 这三条路径都能拿到同一张证书，
    # 不必再按用户给的参数分情况处理。
    $packagedSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $publishDir ($ProjectName + '.exe'))

    if (-not $packagedSignature.SignerCertificate) {
        Write-Warning '    这个包没有签名（-SkipSign 或签名失败），无法附带信任材料：已跳过。'
        $trustBundleNote = '未附带（包未签名）'
    }
    else {
        $signerCert = $packagedSignature.SignerCertificate

        $cerPackagePath = Join-Path $publishDir 'trusted-publisher.cer'
        Export-Certificate -Cert $signerCert -FilePath $cerPackagePath -Type CERT | Out-Null

        $installerSource = Join-Path $scriptDir 'Install-TrustedPublisher.ps1'
        if (-not (Test-Path -LiteralPath $installerSource)) {
            throw ('缺少安装脚本：' + $installerSource + '（-IncludeTrustedPublisher 需要它随包分发）')
        }

        Copy-Item -LiteralPath $installerSource -Destination (Join-Path $publishDir 'Install-TrustedPublisher.ps1') -Force

        # 双击就能跑的入口：多数人不会「右键 → 使用 PowerShell 运行」（Win11 甚至藏起来了），
        # 所以这里生成一个 .bat。指纹直接写死进命令行：cer 被替换时 Install-TrustedPublisher.ps1
        # 会当场比对失败退出，而不是悄悄信任另一张证书。批处理内容全英文，避免代码页乱码。
        $trustBatPath = Join-Path $publishDir '安装信任_双击我.bat'
        $trustBatLines = @(
            '@echo off',
            'rem =====================================================================',
            'rem  Double-click this file: trust the code-signing certificate of',
            'rem  MSIFAITOOLS, then you can start the program normally.',
            'rem  It only writes Cert:\CurrentUser\Root  ->  NO administrator needed.',
            'rem  Generated by Publish-Signed.ps1, do not edit by hand.',
            'rem  Undo:  double-click again with an extra argument  -Uninstall',
            'rem =====================================================================',
            'setlocal',
            'title Install trusted publisher',
            'cd /d "%~dp0"',
            '',
            'if not exist "Install-TrustedPublisher.ps1" goto nops',
            'if not exist "trusted-publisher.cer" goto nocer',
            '',
            ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TrustedPublisher.ps1" -ExpectedThumbprint ' + $signerCert.Thumbprint + ' -NoPause %*'),
            'set "RC=%ERRORLEVEL%"',
            '',
            'echo.',
            'if not "%RC%"=="0" (',
            '    echo [FAILED] exit code %RC%',
            '    echo Scroll up and read the message above.',
            '    echo.',
            ') else (',
            '    echo [OK] Finished. You can start MSIFAITOOLS.exe now.',
            ')',
            'echo Press any key to close this window.',
            'pause >nul',
            'exit /b %RC%',
            '',
            ':nops',
            'echo ERROR: Install-TrustedPublisher.ps1 was not found in this folder.',
            'echo Please unzip the whole package first, then double-click again.',
            'echo.',
            'pause >nul',
            'exit /b 1',
            '',
            ':nocer',
            'echo ERROR: trusted-publisher.cer was not found in this folder.',
            'echo Please unzip the whole package first, then double-click again.',
            'echo.',
            'pause >nul',
            'exit /b 1'
        )
        Write-LinesFile -Path $trustBatPath -Lines $trustBatLines -Encoding ASCII

        $cerHash = (Get-FileHash -LiteralPath $cerPackagePath -Algorithm SHA256).Hash

        $trustReadme = @()
        $trustReadme += '第一次运行之前：先装「信任发布者」（只需做一次）'
        $trustReadme += ('生成时间：' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
        $trustReadme += ''
        $trustReadme += '为什么：这个程序用的是公司内部签名证书，你的电脑默认还不信任它。'
        $trustReadme += '        不装也能运行，但会显示「未知发布者」，个别杀毒软件也可能拦。'
        $trustReadme += ''
        $trustReadme += '怎么做（三选一，都不需要管理员权限）：'
        $trustReadme += '  A. 双击本文件夹里的「安装信任_双击我.bat」   ← 最省事，推荐'
        $trustReadme += '  B. 在 Install-TrustedPublisher.ps1 上点右键 →「使用 PowerShell 运行」'
        $trustReadme += '  C. 打开 PowerShell，在本文件夹执行：'
        $trustReadme += '     powershell -ExecutionPolicy Bypass -File .\Install-TrustedPublisher.ps1'
        $trustReadme += ''
        $trustReadme += '装完之后：右键 MSIFAITOOLS.exe → 属性 →「数字签名」，签名者姓名应显示下面的主体。'
        $trustReadme += ''
        $trustReadme += '要信任的证书是什么（请核对，防止被替换）：'
        $trustReadme += ('  主体　　　　：' + $signerCert.Subject)
        $trustReadme += ('  指纹(SHA-1) ：' + $signerCert.Thumbprint)
        $trustReadme += ('  有效期　　　：' + $signerCert.NotBefore.ToString('yyyy-MM-dd') + ' 〜 ' + $signerCert.NotAfter.ToString('yyyy-MM-dd'))
        $trustReadme += ('  cer SHA-256 ：' + $cerHash)
        $trustReadme += ''
        $trustReadme += '想撤销：运行「安装信任_双击我.bat」时多给一个参数 -Uninstall，或在命令行执行：'
        $trustReadme += '        powershell -ExecutionPolicy Bypass -File .\Install-TrustedPublisher.ps1 -Uninstall'
        $trustReadme += '        （撤销时 Windows 会弹一次「ルート証明書ストア」确认框，点「はい / 是」才会删掉；'
        $trustReadme += '          安装时不会弹任何框。）'
        $trustReadme += ''
        $trustReadme += '注意：装信任解决的是「显示发布者 / 签名校验」，并不提高 SmartScreen 的信誉分。'
        $trustReadme += '      如果文件是从网上下载的（带网络来源标记），SmartScreen 仍可能拦一次；'
        $trustReadme += '      从内网共享目录或 U 盘拷过来不会有这个问题。'

        Write-LinesFile -Path (Join-Path $publishDir '先装信任_再运行.txt') -Lines $trustReadme

        $trustBundleNote = '已附带（trusted-publisher.cer + Install-TrustedPublisher.ps1 + 安装信任_双击我.bat）'
        Write-Host ('    签名主体：' + $signerCert.Subject)
        Write-Host '    已放入：trusted-publisher.cer / Install-TrustedPublisher.ps1 / 安装信任_双击我.bat / 先装信任_再运行.txt'
    }
}

# ---------------------------------------------------------------- 4) 发布信息
Write-Step '[4/5] 生成「发布信息.txt」（含每个文件的 SHA-256，提交误报时用得上）'

$infoPath = Join-Path $publishDir '发布信息.txt'
$info = New-Object 'System.Collections.Generic.List[string]'

$info.Add('程序        : ' + $ProjectName)
$info.Add('版本        : ' + $version)
$info.Add('构建配置    : ' + $Configuration)
$info.Add('配置脱敏    : ' + $secretNote)
$info.Add('构建时间    : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))
$info.Add('签名状态    : ' + $signatureStatus)
if ($wantTrustBundle) {
    $info.Add('信任材料    : ' + $trustBundleNote)
}

$mainSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $publishDir ($ProjectName + '.exe'))
if ($mainSignature.SignerCertificate) {
    $info.Add('签名主体    : ' + $mainSignature.SignerCertificate.Subject)
    $info.Add('签名颁发者  : ' + $mainSignature.SignerCertificate.Issuer)
    $info.Add('证书有效期  : ' + $mainSignature.SignerCertificate.NotBefore.ToString('yyyy-MM-dd') + ' 〜 ' + $mainSignature.SignerCertificate.NotAfter.ToString('yyyy-MM-dd'))
}

$info.Add('')
$info.Add('文件 SHA-256：')

$hashTargets = Get-ChildItem -LiteralPath $publishDir -Recurse -File | Sort-Object FullName
foreach ($file in $hashTargets) {
    if ($file.Name -eq '发布信息.txt') { continue }

    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $relative = $file.FullName.Substring($publishDir.Length).TrimStart('\')
    $info.Add(('{0}  {1,10}  {2}' -f $hash, $file.Length, $relative))
}

Write-LinesFile -Path $infoPath -Lines $info

# ---------------------------------------------------------------- 5) 打包
Write-Step '[5/5] 打包 zip'

$zipPath = $publishDir + '.zip'

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# 用 .NET 的 ZipFile 而不是 Compress-Archive：PS 5.1 的 Compress-Archive 不写 UTF-8 名字标志，
# 某些解压工具会把「部品マスタ」这类日文名显示成乱码。
# 这里显式用 UTF-8 编码写条目名 —— .NET 会自动置上「UTF-8 文件名」标志位（通用标志 0x800），
# 资源管理器 / 7-Zip / WinRAR 都会按 UTF-8 解码，中文名（安装信任_双击我.bat）也不会丢字。
# ★ 千万别用 [Text.Encoding]::Default：那是本机 ANSI 代码页（日文机 = CP932），
#   里面没有「击 / 认」等简体字，会被替换成 '?'，解压时报「パスに無効な文字」。
Add-Type -AssemblyName 'System.IO.Compression.FileSystem'
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true,
    [System.Text.Encoding]::UTF8)

$zipSize = (Get-Item -LiteralPath $zipPath).Length
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash

# --------------------------------- 5.5) 生成「误报提交材料」（被报毒时直接照抄）
# 杀毒软件不看签名、只看特征：被报毒时免费的解法只有「提交误报」和「按哈希加白」。
# 这里把要粘贴的正文、要填的哈希、要点的网址一次性生成好，发版时不用再手抄。
$reportDir = ($zipPath -replace '\.zip$', '_误报提交材料')
if (Test-Path -LiteralPath $reportDir) {
    Remove-Item -LiteralPath $reportDir -Recurse -Force
}
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

$exeHashLines = @()
# 主程序在包根目录，分割程序在自己的子目录里 —— 两个都要进误报材料（它们都会被报毒）。
foreach ($exeRel in @(($ProjectName + '.exe'), (Join-Path 'CadPartSplitter' 'CadPartSplitter.exe'))) {
    $exeFull = Join-Path $publishDir $exeRel
    if (Test-Path -LiteralPath $exeFull) {
        $exeHashLines += ('{0}  {1}  {2:N1} MB' -f $exeRel,
                          (Get-FileHash -LiteralPath $exeFull -Algorithm SHA256).Hash,
                          ((Get-Item -LiteralPath $exeFull).Length / 1MB))
    }
}

$reportReadme = @()
$reportReadme += '被报毒时按下面 3 步做，每个网址一次，总共 5 分钟（没被报毒就不用管这个文件夹）。'
$reportReadme += ''
$reportReadme += '① Microsoft（Defender / SmartScreen，最优先）'
$reportReadme += '   https://www.microsoft.com/en-us/wdsi/filesubmission'
$reportReadme += '   → "I am a software developer" → "Incorrectly detected as malware"'
$reportReadme += '   → 上传 MSIFAITOOLS.exe，说明栏粘贴 02_提交正文_EN.txt（只把检测名称改成实际报的名字）'
$reportReadme += ''
$reportReadme += '② VirusTotal（先看清到底是谁报，再决定分别提交给谁）'
$reportReadme += '   https://www.virustotal.com/gui/home/upload 上传 MSIFAITOOLS.exe'
$reportReadme += '   每个报毒的厂商点进去，页面上都有 "Submit false positive" / 误报提交入口，'
$reportReadme += '   粘贴同一份 02_提交正文_EN.txt 即可（Trend Micro / Symantec / McAfee 各自都收）'
$reportReadme += ''
$reportReadme += '③ 公司内部杀毒（同事端是公司统一管理时，这条最省事、永久有效）'
$reportReadme += '   把 04_哈希清单.txt 发给信息安全/IT，请他们在管理控制台按 SHA-256 加白名单。'
$reportReadme += '   不需要证书、不需要改代码，加一次 = 这个版本以后不再报。'
$reportReadme += ''
$reportReadme += '提交后一般 24〜72 小时出结果；同一哈希重复提交会更快。'
Write-LinesFile -Path (Join-Path $reportDir '01_怎么提交.txt') -Lines $reportReadme

$hashList = @()
$hashList += ('产品    : {0} {1}' -f $ProjectName, $version)
$hashList += ('构建时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$hashList += ''
$hashList += '-- 需要加白名单的 exe --'
$hashList += $exeHashLines
$hashList += ''
$hashList += '-- 整个发布包 zip --'
$hashList += ('{0}  {1:N1} MB' -f $zipHash, ($zipSize / 1MB))
Write-LinesFile -Path (Join-Path $reportDir '04_哈希清单.txt') -Lines $hashList

# 英文正文（Microsoft / 各厂商误报表单通用）：只留一个空位给「检测名称」。
$enBody = @()
$enBody += 'Subject: False positive - internal business tool (C#/.NET), not malware'
$enBody += ''
$enBody += 'Hello,'
$enBody += ''
$enBody += 'The file(s) below are incorrectly detected. They are part of an internal business'
$enBody += 'tool used only inside our company network; it is NOT distributed to the public.'
$enBody += ''
$enBody += ('Detected as : <PUT THE DETECTION NAME HERE, e.g. Trojan:Win32/Wacatac.B!ml>')
$enBody += ('Product     : {0} {1}' -f $ProjectName, $version)
$enBody += 'Publisher   : MSIF / LIXIL Corporation (internal tool)'
$enBody += ('Build time  : ' + (Get-Date -Format 'yyyy-MM-dd'))
$enBody += 'Technology  : C# / .NET Framework 4.8 / x64 / Authenticode signed'
$enBody += ''
$enBody += 'SHA-256:'
foreach ($line in $exeHashLines) { $enBody += ('  ' + $line) }
$enBody += ('  {0}  (full release package, {1:N1} MB)' -f $zipHash, ($zipSize / 1MB))
$enBody += ''
$enBody += 'Why it may look suspicious (all intentional, none is malicious):'
$enBody += '* It starts a second executable from its own subfolder'
$enBody += '  (CadPartSplitter\CadPartSplitter.exe) to split PDF drawings.'
$enBody += '* It connects to our internal SQL Server (10.103.126.208 / .209, port 1433)'
$enBody += '  to read a parts master table.'
$enBody += '* It writes a local cache to D:\MSIFDB and %APPDATA%\MSIFAITOOLS.'
$enBody += '* It bundles Pdfium (PDF rendering) and SQLite (local DB) native libraries,'
$enBody += '  which are common sources of heuristic detections.'
$enBody += '* It does NOT download or execute code from the internet, does not install'
$enBody += '  services or drivers, does not touch autostart entries, and opens no listener.'
$enBody += ''
$enBody += 'Please add this build to your clean list, or tell us which behaviour is'
$enBody += 'considered malicious so we can adjust it.'
$enBody += ''
$enBody += 'Thank you.'
Write-LinesFile -Path (Join-Path $reportDir '02_提交正文_EN.txt') -Lines $enBody

# 日文正文（社内ベンダー / 国内厂商用）
$jpBody = @()
$jpBody += '件名：【誤検知のご連絡】社内業務ツール（C#/.NET）について'
$jpBody += ''
$jpBody += 'お世話になっております。MSIF（LIXIL）の<氏名>です。'
$jpBody += ''
$jpBody += '下記ファイルがウイルスとして検知されておりますが、社内業務用に自作した'
$jpBody += 'ツール（C# / .NET Framework 4.8 製）であり、マルウェアではありません。'
$jpBody += '検知除外のご対応をお願いできますでしょうか。'
$jpBody += ''
$jpBody += ('検知名　　：<ここに検知名を記入>')
$jpBody += ('製品名　　：{0} {1}' -f $ProjectName, $version)
$jpBody += ('ビルド日　：' + (Get-Date -Format 'yyyy-MM-dd'))
$jpBody += 'SHA-256　：'
foreach ($line in $exeHashLines) { $jpBody += ('  ' + $line) }
$jpBody += ''
$jpBody += '動作内容（すべて意図した処理です）：'
$jpBody += '・同梱フォルダの CadPartSplitter.exe を起動して PDF 図面を分割します。'
$jpBody += '・社内 SQL Server（10.103.126.x:1433）から部品マスタを取得します。'
$jpBody += '・ローカルキャッシュを D:\MSIFDB と %APPDATA%\MSIFAITOOLS に保存します。'
$jpBody += '・Pdfium（PDF 描画）と SQLite（ローカル DB）のネイティブ DLL を同梱しています。'
$jpBody += '・インターネットからの取得・実行、サービス/ドライバ登録、自動起動設定、'
$jpBody += '　待ち受けポートは一切ありません。'
$jpBody += ''
$jpBody += 'ご確認のほど、よろしくお願いいたします。'
Write-LinesFile -Path (Join-Path $reportDir '03_提交正文_JP.txt') -Lines $jpBody

Write-Host ''
Write-Host '================ 发布完成 ================' -ForegroundColor Green
Write-Host ("目录      : {0}" -f $publishDir)
Write-Host ("zip       : {0}（{1:N1} MB）" -f $zipPath, ($zipSize / 1MB))
Write-Host ("zip SHA256: {0}" -f $zipHash)
Write-Host ("版本      : {0}" -f $version)
Write-Host ("签名状态  : {0}" -f $signatureStatus)
Write-Host ("误报材料  : {0}" -f $reportDir)

if ($signatureStatus -eq 'Valid') {
    Write-Host ''
    Write-Host '签名有效：客户机上会显示「签名主体」里的发布者。' -ForegroundColor Green
}
elseif ($signatureStatus -like '*签名已写入*') {
    Write-Host ''
    Write-Host '提醒：签名已经写进 exe，但本机不信任这个证书的根（自签，或内网 CA 还没部署根证书）。' -ForegroundColor Yellow
    Write-Host '      域内机器通过 GPO 装上根证书后才会显示发布者；公网 SmartScreen 仍会拦，' -ForegroundColor Yellow
    Write-Host '      对外分发必须换正式证书（见 docs\代码签名_IT申请.md）。' -ForegroundColor Yellow
}
else {
    Write-Host ''
    Write-Host '提醒：这个包没有有效签名，客户机上大概率仍会被 SmartScreen / 杀毒软件拦。' -ForegroundColor Yellow
    Write-Host '      下一步见 docs\病毒误报_对策.md。' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '===== 接下来怎么做（最省事，一共 3 步） =====' -ForegroundColor Cyan
Write-Host '  1. 把 zip 或解压后的文件夹交给同事：走内网共享 / U 盘 / 软件分发系统，'
Write-Host '     不要用浏览器或邮件发链接下载 —— 没有「网络来源标记」，SmartScreen 就不会弹窗。'
Write-Host '  2. 同事第一次运行时，先双击包内的「安装信任_双击我.bat」'
Write-Host '     （一次即可，不需要管理员；装完就不再显示「未知发布者」）。'
Write-Host ('  3. 如果杀毒软件仍然报毒：打开 ' + (Split-Path -Leaf $reportDir) + '\01_怎么提交.txt 照做，')
Write-Host '     里面英文/日文正文和哈希都填好了；最省事的一条是把 04_哈希清单.txt 交给公司'
Write-Host '     信息安全按哈希加白 —— 那一条不需要证书，加一次这个版本就不再报。'
