#Requires -Version 5.1
<#
.SYNOPSIS
    生成「代码签名证书」的 CSR，并把 IT 签回来的证书装进证书存储、导出成可用的 pfx。

.DESCRIPTION
    背景：仓库里的 msif.pfx / cad.pfx 经核实是「强名称（strong name）」测试密钥，
    不能用于 Authenticode（详见 docs\病毒误报_对策.md 第 4 章）。公司有独立 CA
    （lixil-CA / jpn-CA），但 AD 里没有发布企业 CA（Enrollment Service = 0），
    所以只能走「生成 CSR → 发给 IT → IT 用 Code Signing 模板签发 → 收回证书」的流程。
    本脚本把这三步自动化，私钥始终留在本机密钥容器里，不会随 CSR 外发：

      第 1 步（本机）：生成 codesign.inf + codesign.req，把这两个文件发给 IT；
      第 2 步（本机，可选）：IT 开放了 CA 提交权限时，用 -CaConfig 直接提交；
      第 3 步（本机）：拿到 IT 返回的 codesign.cer 后，-CertificateFile 装进证书存储
                      并导出 code-signing.pfx，或直接用指纹发布。

.PARAMETER Subject
    证书主体（X.500 字符串）。O=公司法定名称、OU=部门、C=国家，按需修改。
    默认值只是占位，发出去之前请确认公司名称写法。

.PARAMETER OutputDir
    inf / req / cer / pfx 的输出目录，默认 <工程>\artifacts\codesign。

.PARAMETER KeyLength
    RSA 密钥长度，默认 3072（2048 也够用，4096 更慢）。

.PARAMETER CaConfig
    内部 CA 配置串，形如 <CA服务器>\<CA名>。给了它就尝试 certreq -submit 直接提交。

.PARAMETER Template
    证书模板名（由 IT 提供），默认 CodeSigning。

.PARAMETER CertificateFile
    IT 返回的证书文件（.cer / .crt）。给了它就执行「装证书 + 导出 pfx」。

.PARAMETER PfxPassword
    导出 pfx 用的密码；不传则安全提示输入。

.EXAMPLE
    # 第 1 步：生成 CSR（把 artifacts\codesign\codesign.req 和 codesign.inf 发给 IT）
    .\Tools\New-CodeSigningRequest.ps1 -Subject 'CN=MSIF Tools, OU=IT, O=<公司名>, C=JP'

.EXAMPLE
    # 第 3 步：拿到 IT 返回的 codesign.cer 后装好并导出 pfx
    .\Tools\New-CodeSigningRequest.ps1 -CertificateFile .\codesign.cer

.EXAMPLE
    # IT 给了 CA 配置串时，一步到底：提交 → 收回 → 装证书
    .\Tools\New-CodeSigningRequest.ps1 -CaConfig 'CAHOST\lixil-CA' -Template CodeSigning
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=MSIF Tools, OU=IT, O=LIXIL Corporation, C=JP',
    [string] $OutputDir,
    [ValidateSet(2048, 3072, 4096)]
    [int]    $KeyLength = 3072,
    [string] $CaConfig,
    [string] $Template = 'CodeSigning',
    [string] $CertificateFile,
    [string] $PfxPassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$CodeSigningEku = '1.3.6.1.5.5.7.3.3'

function Write-Step {
    param([string] $Text)

    Write-Host ''
    Write-Host "== $Text " -ForegroundColor Cyan
}

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Split-Path -Parent $scriptDir

if (-not $OutputDir) {
    $OutputDir = Join-Path $projectDir 'artifacts\codesign'
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

$infPath = Join-Path $OutputDir 'codesign.inf'
$reqPath = Join-Path $OutputDir 'codesign.req'
$cerPath = Join-Path $OutputDir 'codesign.cer'
$pfxPath = Join-Path $OutputDir 'code-signing.pfx'

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
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)

    if (-not $Certificate) {
        throw '没能取到证书对象，无法判断它是否可以用来做代码签名。'
    }

    Write-Host ("    证书主体　：{0}" -f $Certificate.Subject)
    Write-Host ("    颁发者　　：{0}" -f $Certificate.Issuer)
    Write-Host ("    有效期　　：{0} 〜 {1}" -f $Certificate.NotBefore.ToString('yyyy-MM-dd'), $Certificate.NotAfter.ToString('yyyy-MM-dd'))

    $ekuExtension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }

    $ekuText = ''
    if ($ekuExtension) {
        $ekuText = ($ekuExtension.Format($false) -replace '\s+', ' ').Trim()
    }

    if ($ekuText -notlike ('*' + $CodeSigningEku + '*')) {
        if (-not $ekuText) { $ekuText = '（没有 EKU 扩展）' }

        throw ("这张证书不能用于代码签名：缺少 EKU「Code Signing ($CodeSigningEku)」，实际是 $ekuText`n" +
               '      请把这条信息回给 IT，让他们用「Code Signing / コード署名」模板重签。')
    }

    Write-Host '    EKU 校验通过（含 Code Signing）' -ForegroundColor Green
}

# ---------------------------------------------------------------- 第 3 步：装证书 + 导出 pfx
if ($CertificateFile) {
    $resolvedCer = (Resolve-Path -LiteralPath $CertificateFile).Path

    Write-Step "[1/3] 读取 CA 返回的证书（$resolvedCer）"

    $issued = Get-PfxCertificate -FilePath $resolvedCer
    Assert-CodeSigningCertificate -Certificate $issued

    Write-Step '[2/3] 把证书和本机私钥配对（certreq -accept）'

    & certreq -accept $resolvedCer

    if ($LASTEXITCODE -ne 0) {
        $acceptFailed = 'certreq -accept 失败（退出码 {0}）。多半是本机没有对应的待定私钥：' -f $LASTEXITCODE
        $acceptFailed += '换过电脑或删过密钥容器的话，请回到第 1 步重新生成 CSR 再请 IT 签。'

        throw $acceptFailed
    }

    $storeCert = Get-ChildItem -Path ('Cert:\CurrentUser\My\' + $issued.Thumbprint) -ErrorAction SilentlyContinue
    if (-not $storeCert) {
        $storeCert = Get-ChildItem -Path ('Cert:\LocalMachine\My\' + $issued.Thumbprint) -ErrorAction SilentlyContinue
    }

    if (-not $storeCert) {
        throw "证书已导入，但在证书存储里找不到指纹 $($issued.Thumbprint)。"
    }

    if (-not $storeCert.HasPrivateKey) {
        throw '证书没有配到私钥（HasPrivateKey=false）。请回到第 1 步重新生成 CSR 后请 IT 重签。'
    }

    Write-Step '[3/3] 导出 pfx（备份用；换台机器构建时也能直接拿来签）'

    if (-not $PfxPassword) {
        $secure = Read-Host -Prompt '  为导出的 pfx 设置密码' -AsSecureString
        $PfxPassword = ConvertTo-PlainText -Secure $secure
    }

    if (-not $PfxPassword) {
        throw 'pfx 必须设置密码（不能为空）。重跑时用 -PfxPassword 传入，或在上面的提示里输入。'
    }

    $pfxSecure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $storeCert -FilePath $pfxPath -Password $pfxSecure | Out-Null

    Write-Host ''
    Write-Host '================ 证书就绪 ================' -ForegroundColor Green
    Write-Host ('  指纹（发布时用这个）  ：' + $storeCert.Thumbprint)
    Write-Host ('  pfx（不要提交进 git） ：' + $pfxPath)
    Write-Host ''
    Write-Host '  发布命令：'
    Write-Host ('    .\Tools\Publish-Signed.ps1 -CertificateThumbprint ' + $storeCert.Thumbprint)
    Write-Host ('    .\Tools\Publish-Signed.ps1 -CertificatePath ''' + $pfxPath + '''')

    return
}

# ---------------------------------------------------------------- 第 1 步：生成 CSR
Write-Step "[1/3] 生成证书请求（RSA $KeyLength / EKU Code Signing）"

# KeyUsage = 0x80 → Digital Signature。EKU 直接用 OID 指定，不依赖 CA 模板的默认值。
$inf = @"
[Version]
Signature="`$Windows NT`$"

[NewRequest]
Subject = "$Subject"
KeySpec = 1
KeyLength = $KeyLength
Exportable = TRUE
ExportableEncrypted = TRUE
MachineKeySet = FALSE
PrivateKeyArchive = FALSE
UserProtected = FALSE
UseExistingKeySet = FALSE
ProviderName = "Microsoft Enhanced RSA and AES Cryptographic Provider"
ProviderType = 24
RequestType = PKCS10
KeyUsage = 0x80
FriendlyName = "MSIF Tools Code Signing"

[EnhancedKeyUsageExtension]
OID = 1.3.6.1.5.5.7.3.3
"@

# certreq 读 INI 时不认 UTF-8 BOM，这里用 ASCII 写；因此 Subject 里的公司名请用英文写法
# （需要日文/中文显示名时，让 IT 在签发时改 Subject）。
Set-Content -LiteralPath $infPath -Value $inf -Encoding ASCII

& certreq -new $infPath $reqPath

if ($LASTEXITCODE -ne 0) {
    throw ("certreq -new 失败（退出码 {0}）。请检查 {1}。" -f $LASTEXITCODE, $infPath)
}

Write-Host ('    请求文件：' + $reqPath)
Write-Host ('    模板文件：' + $infPath)
Write-Host ''
Write-Host '    把 codesign.req 发给 IT，请他们用「Code Signing / コード署名」模板签发，' -ForegroundColor Yellow
Write-Host '    再把证书（.cer）发回来。' -ForegroundColor Yellow

# ---------------------------------------------------------------- 第 2 步（可选）：直接提交
if (-not $CaConfig) {
    Write-Step '[2/3] 跳过在线提交（没有给 -CaConfig）'
    Write-Host '    IT 签发后执行：'
    Write-Host '    .\Tools\New-CodeSigningRequest.ps1 -CertificateFile <IT 返回的 .cer>'
    return
}

Write-Step "[2/3] 提交到内部 CA（$CaConfig，模板 $Template）"

& certreq -submit -config $CaConfig -cert $Template $reqPath $cerPath

if ($LASTEXITCODE -ne 0) {
    Write-Warning ("certreq -submit 失败（退出码 {0}）。常见原因：" -f $LASTEXITCODE)
    Write-Warning '    * 这个账号没有该模板的「注册」权限（最常见，需要 IT 授权）'
    Write-Warning '    * CA 服务器名 / CA 名不对，或 135/tcp（DCOM）被防火墙挡住'
    Write-Warning ''
    Write-Warning '  兜底：把 codesign.req 发邮件给 IT，请他们在 CA 上用 certsrv「提交证书申请」签发。'
    return
}

Write-Host '    签发成功，正在安装...'

& certreq -accept $cerPath

if ($LASTEXITCODE -ne 0) {
    throw ("证书签发成功但安装失败（退出码 {0}）。请手工执行：certreq -accept '{1}'" -f $LASTEXITCODE, $cerPath)
}

$issued = Get-PfxCertificate -FilePath $cerPath
Assert-CodeSigningCertificate -Certificate $issued

$storeCert = Get-ChildItem -Path ('Cert:\CurrentUser\My\' + $issued.Thumbprint) -ErrorAction SilentlyContinue

if (-not $storeCert) {
    throw "证书已导入，但在「个人」证书存储里找不到指纹 $($issued.Thumbprint)。"
}

if (-not $storeCert.HasPrivateKey) {
    throw '证书没有配到私钥（HasPrivateKey=false）。请回到第 1 步重新生成 CSR 后请 IT 重签。'
}

Write-Host ''
Write-Host '================ 证书就绪 ================' -ForegroundColor Green
Write-Host ('  指纹（发布时用这个）：' + $storeCert.Thumbprint)
Write-Host ('  发布命令：.\Tools\Publish-Signed.ps1 -CertificateThumbprint ' + $storeCert.Thumbprint)

