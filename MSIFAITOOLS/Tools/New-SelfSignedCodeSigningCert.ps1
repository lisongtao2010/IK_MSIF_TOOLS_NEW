#Requires -Version 5.1
<#
.SYNOPSIS
    生成「自签代码签名证书」并导出 pfx / cer —— 没有正式证书时先把签名流水线跑通。

.DESCRIPTION
    免费的公网代码签名证书已经没有了（2026-11 核实）：
      * Let's Encrypt / ZeroSSL / Buypass / Google Trust Services 只发 TLS 证书（EKU=serverAuth），
        里面没有 Code Signing EKU（1.3.6.1.5.5.7.3.3），signtool 会直接拒绝使用；
      * SignPath.io、Certum Open Source 的免费额度只给**公开开源项目**，要仓库公开 + 人工审核 +
        实名认证；本项目是公司内部工具，不适用；
      * Azure Trusted Signing 按 $9.99/月 收费，且要求公司有 3 年以上可验证经营记录。

    所以「免费」只剩两条路，本脚本做的是第 2 条：
      1. 公司内部 CA（lixil-CA）签发 —— 真正免费、域内自动受信，见 docs\代码签名_IT申请.md；
      2. 自签证书（本脚本）—— 立刻可用，解决「签名流水线能跑通」和「域内装了根证书后显示公司名」

    自签证书的效果边界（别当正式证书用）：
      * 在**没有**部署根证书的机器上：仍显示「未知发布者」，和未签名差不多，SmartScreen 该弹还弹；
      * 把 <OutputDir>\msif-codesign-root.cer 通过 GPO（或手动）装进「受信任的根证书颁发机构」后：
        「发布者」显示证书 CN，Get-AuthenticodeSignature 为 Valid；
      * **不能**用来给公司外的人分发 —— 对方机器上没有你的根证书。

.PARAMETER Subject
    证书主体（X.500）。CN 就是签名后「发布者」显示的名字，改成想显示的公司名。
    默认 CN=LIXIL Corporation, OU=IT, O=LIXIL Corporation, C=JP。

.PARAMETER OutputDir
    pfx / cer / 说明.txt 的输出目录，默认 <工程>\artifacts\codesign-selfsigned。

.PARAMETER Years
    有效期（年），默认 3。自签证书不受 CA 的 39 个月限制，但也别设太长（GPO 换证书麻烦）。

.PARAMETER KeyLength
    RSA 密钥长度，默认 3072。

.PARAMETER PfxPassword
    导出 pfx 用的密码；不传则自动生成随机密码，打印在控制台（只打印这一次，请自己记下）。

.PARAMETER NoInstall
    只导出文件，不把证书留在「当前用户\个人」证书存储里（签名只能走 -CertificatePath 形式）。

.EXAMPLE
    # 生成自签证书：装进 CurrentUser\My，并导出 pfx / cer
    .\Tools\New-SelfSignedCodeSigningCert.ps1

.EXAMPLE
    # 指定显示名，只导出文件（不在本机证书存储里留证书）
    .\Tools\New-SelfSignedCodeSigningCert.ps1 -Subject 'CN=LIXIL MSIF Tools, O=LIXIL Corporation, C=JP' -NoInstall
#>
[CmdletBinding()]
param(
    [string] $Subject = 'CN=LIXIL Corporation, OU=IT, O=LIXIL Corporation, C=JP',
    [string] $OutputDir,
    [ValidateRange(1, 10)]
    [int]    $Years = 3,
    [ValidateSet(2048, 3072, 4096)]
    [int]    $KeyLength = 3072,
    [string] $PfxPassword,
    [switch] $NoInstall
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
    $OutputDir = Join-Path $projectDir 'artifacts\codesign-selfsigned'
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

$pfxPath  = Join-Path $OutputDir 'msif-codesign-selfsigned.pfx'
$cerPath  = Join-Path $OutputDir 'msif-codesign-root.cer'
$readmePath = Join-Path $OutputDir '说明.txt'

# ---------------------------------------------------------------- 1) 生成证书
$generatedPassword = $false

if (-not $PfxPassword) {
    $PfxPassword = 'Msif!' + [System.Guid]::NewGuid().ToString('N').Substring(0, 20) + '#1'
    $generatedPassword = $true
}

Write-Step "生成自签代码签名证书（RSA $KeyLength / $Years 年）"
Write-Host ("    主体：{0}" -f $Subject)

$storePath = 'Cert:\CurrentUser\My'

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength $KeyLength `
    -HashAlgorithm SHA256 `
    -NotBefore (Get-Date).AddDays(-1) `
    -NotAfter (Get-Date).AddYears($Years) `
    -CertStoreLocation $storePath

if (-not $cert) {
    throw 'New-SelfSignedCertificate 没有返回证书对象（需要 Windows 10/11 自带的 PKI 模块）。'
}

$cert = Get-Item -LiteralPath (Join-Path $storePath $cert.Thumbprint)

if (-not $cert.HasPrivateKey) {
    throw ('生成出来的证书没有私钥（HasPrivateKey=false），无法用于签名：' + $cert.Thumbprint)
}

# ---------------------------------------------------------------- 2) 自检（EKU）
$ekuExtension = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
$ekuOids = @()

if ($ekuExtension) {
    $enhanced = $ekuExtension -as [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]

    if ($enhanced) {
        foreach ($oid in $enhanced.EnhancedKeyUsages) { $ekuOids += $oid.Value }
    }
}

Write-Host ("    指纹：{0}" -f $cert.Thumbprint)
Write-Host ("    有效期：{0:yyyy-MM-dd} 〜 {1:yyyy-MM-dd}" -f $cert.NotBefore, $cert.NotAfter)
Write-Host ("    EKU：{0}" -f (($ekuOids -join ', ') -replace '^$', '（无）'))

if ($ekuOids -notcontains $CodeSigningEku) {
    throw ('证书里没有 Code Signing EKU（' + $CodeSigningEku + '），signtool 不会接受它。')
}

# ---------------------------------------------------------------- 3) 导出
Write-Step '导出 pfx（含私钥）与 cer（可部署到域的根证书）'

$secure = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

# 回读 pfx：确认密码对得上、私钥在里面（这一步失败 = 导出的包没法用来签名）
$reloaded = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
    $pfxPath,
    $PfxPassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)

if (-not $reloaded.HasPrivateKey) {
    throw ('导出的 pfx 里没有私钥：' + $pfxPath)
}

Write-Host ("    {0}  ({1:N1} KB)" -f $pfxPath, ((Get-Item -LiteralPath $pfxPath).Length / 1KB))
Write-Host ("    {0}  ({1:N1} KB)" -f $cerPath, ((Get-Item -LiteralPath $cerPath).Length / 1KB))

if ($generatedPassword) {
    Write-Host ''
    Write-Host '    pfx 密码（自动生成，只显示这一次，请记下）：' -ForegroundColor Yellow
    Write-Host ("    {0}" -f $PfxPassword) -ForegroundColor Yellow
    Write-Host '    （泄漏了也不要紧：这是自签证书，删掉重发即可，见说明.txt）' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 4) 说明文件
$readme = @()
$readme += 'MSIF 自签代码签名证书（内部用）'
$readme += ''
$readme += ('生成时间　：' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$readme += ('主体　　　：' + $cert.Subject)
$readme += ('指纹　　　：' + $cert.Thumbprint)
$readme += ('有效期　　：' + $cert.NotBefore.ToString('yyyy-MM-dd') + ' 〜 ' + $cert.NotAfter.ToString('yyyy-MM-dd'))
$readme += ('pfx　　　 ：' + $pfxPath + '（含私钥，签名用，勿外发）')
$readme += ('cer　　　 ：' + $cerPath + '（公钥，部署到「受信任的根证书颁发机构」用）')
$readme += ''
$readme += '【能做什么】'
$readme += '  * 现在就能跑通 Authenticode 签名流水线（证书到期换成正式证书即可，脚本不用改）。'
$readme += '  * 把 cer 通过 GPO / 手动装进「受信任的根证书颁发机构」后，域内机器上「发布者」显示上面的主体名，'
$readme += '    Get-AuthenticodeSignature 为 Valid。'
$readme += ''
$readme += '【不能做什么】'
$readme += '  * 不能消除公网 SmartScreen / 杀软误报：根证书不在微软信任列表里，公司外的人装了也没意义。'
$readme += '  * 不能替代正式证书对外分发。正式证书请走公司内部 CA（docs\代码签名_IT申请.md）或采购 OV/EV。'
$readme += ''
$readme += '【怎么用】'
$readme += ('  1) 签名发布：.\Tools\Publish-Signed.ps1 -CertificateThumbprint ' + $cert.Thumbprint)
$readme += '  2) 或用文件：.\Tools\Publish-Signed.ps1 -CertificatePath "' + $pfxPath + '"'
$readme += '  3) 验证：    Get-AuthenticodeSignature .\artifacts\publish\<包>\MSIFAITOOLS.exe | Format-List'
$readme += ''
$readme += '【常见提示】'
$readme += '  * 本机没装根证书时，签名后 signtool verify /pa 会报「A certificate chain processed, but terminated'
$readme += '    in a root certificate which is not trusted」——这是预期结果，签名本身是好的；'
$readme += '    Publish-Signed.ps1 对这种情况只给警告（状态记为「签名已写入，但根证书在本机不受信」）。'
$readme += '  * 想在测试机上看「装根证书后的效果」：双击 cer → 安装证书 → 受信任的根证书颁发机构（当前用户），'
$readme += '    再跑 Get-AuthenticodeSignature 就会是 Valid。测完请删掉，别在开发机上长期留着。'
$readme += ''
$readme += '【怎么卸载】'
$readme += ('  Remove-Item -LiteralPath "Cert:\CurrentUser\My\' + $cert.Thumbprint + '"')
$readme += '  再删掉本目录下的 pfx / cer 即可（删除后已签名的 exe 仍然能验签失败 → 需要重新打包）。'

Set-Content -LiteralPath $readmePath -Value $readme -Encoding UTF8

if ($NoInstall) {
    Write-Step '按 -NoInstall：从「当前用户\个人」证书存储里移除（私钥只在 pfx 里）'
    Remove-Item -LiteralPath (Join-Path $storePath $cert.Thumbprint) -Force
    Write-Host '    已移除。签名时请用 -CertificatePath 指向 pfx。'
}

Write-Step '完成'
Write-Host ('    证书指纹：' + $cert.Thumbprint)
Write-Host ('    输出目录：' + $OutputDir)
Write-Host ''
Write-Host '    下一步（当前用户证书存储里已有私钥，直接按指纹签）：'
Write-Host ('      .\Tools\Publish-Signed.ps1 -CertificateThumbprint ' + $cert.Thumbprint)
Write-Host ''
Write-Host '    提醒：自签证书只够内部用；域内要显示公司名，需让 IT 用 GPO 部署 msif-codesign-root.cer；' -ForegroundColor Yellow
Write-Host '          对外分发仍需要正式证书（公司内部 CA 或 OV/EV），见 docs\代码签名_IT申请.md。' -ForegroundColor Yellow

