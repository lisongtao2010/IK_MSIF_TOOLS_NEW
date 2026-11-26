<#
.SYNOPSIS
    接收方（同事的电脑）用：把发布的程序签名证书装进「当前用户 → 受信任的根证书颁发机构」，
    之后程序才显示发布者名称、`Get-AuthenticodeSignature` 才是 Valid。

.DESCRIPTION
    ★ 这个脚本不需要管理员权限（只写当前用户证书存储，不碰机器级存储）。
    ★ 只对「公司内部自签证书 / 内网 CA 证书」有意义。对外部客户，请改用正规 OV/EV 证书，
      不要让别人去信任你的私有根证书。

    装了什么：把 trusted-publisher.cer 里的公钥证书加进 Cert:\CurrentUser\Root。
    没装会怎样：程序照样能运行，但 Windows 会显示「未知发布者」，个别杀毒软件也可能拦。
    想撤销：本脚本加 -Uninstall。

.PARAMETER CerPath
    证书文件路径。默认用脚本同目录的 trusted-publisher.cer；
    找不到时自动选同目录下唯一的 .cer。

.PARAMETER ExpectedThumbprint
    期望的证书指纹（SHA-1）。提供时会严格比对，不匹配直接报错退出，
    用于防止 cer 被中间人替换。指纹写在「先装信任_再运行.txt」里。

.PARAMETER Uninstall
    从当前用户的受信任根里删除这张证书。

.PARAMETER ExePath
    装完后顺便报告该 exe 的签名状态。默认自动找同目录的 MSIFAITOOLS.exe。

.PARAMETER NoPause
    结束时不停顿（给自动化调用用；手动双击运行时不用加）。
    

.EXAMPLE
    # 手动运行（双击 / 右键「使用 PowerShell 运行」时无需任何参数）
    powershell -ExecutionPolicy Bypass -File .\Install-TrustedPublisher.ps1

.EXAMPLE
    # 顺便校验指纹并指定要检查的 exe
    .\Install-TrustedPublisher.ps1 -ExpectedThumbprint 1363AF2AA5DA7CDDB52E371A0FF4A83C022AD12D -ExePath .\MSIFAITOOLS.exe

.EXAMPLE
    # 撤销信任
    .\Install-TrustedPublisher.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $CerPath,
    [string] $ExpectedThumbprint,
    [switch] $Uninstall,
    [string] $ExePath,
    [switch] $NoPause
)

$ErrorActionPreference = 'Stop'

function Write-Head([string] $Text) {
    Write-Host ''
    Write-Host ('=== ' + $Text + ' ===') -ForegroundColor Cyan
}

try {
    $here = $PSScriptRoot
    if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }

    # ---------------------------------------------------------------- 定位证书
    if (-not $CerPath) {
        $defaultCer = Join-Path $here 'trusted-publisher.cer'
        if (Test-Path -LiteralPath $defaultCer) {
            $CerPath = $defaultCer
        }
        else {
            $candidates = @(Get-ChildItem -LiteralPath $here -Filter '*.cer' -File -ErrorAction SilentlyContinue)
            if ($candidates.Count -eq 1) {
                $CerPath = $candidates[0].FullName
            }
            else {
                throw ('找不到证书文件：请把 trusted-publisher.cer 放在本脚本同一目录，或用 -CerPath 指定（当前目录：{0}）' -f $here)
            }
        }
    }

    $CerPath = (Resolve-Path -LiteralPath $CerPath).Path
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPath)

    Write-Head '证书信息（请核对）'
    Write-Host ('  主体　　　　：' + $cert.Subject)
    Write-Host ('  颁发者　　　：' + $cert.Issuer)
    Write-Host ('  指纹(SHA-1) ：' + $cert.Thumbprint)
    Write-Host ('  有效期　　　：' + $cert.NotBefore.ToString('yyyy-MM-dd') + ' 〜 ' + $cert.NotAfter.ToString('yyyy-MM-dd'))
    Write-Host ('  cer 文件　　：' + $CerPath)
    Write-Host ('  SHA-256　　 ：' + (Get-FileHash -LiteralPath $CerPath -Algorithm SHA256).Hash)

    # ------------------------------------------------------------ 基本安全性检查
    if ($ExpectedThumbprint) {
        $expected = $ExpectedThumbprint -replace '[^0-9A-Fa-f]', ''
        if ($expected.ToUpperInvariant() -ne $cert.Thumbprint.ToUpperInvariant()) {
            throw ('指纹不匹配！期望 {0}，实际 {1}。cer 可能被替换过，已中止。' -f $expected, $cert.Thumbprint)
        }
        Write-Host '  指纹比对：一致（-ExpectedThumbprint）' -ForegroundColor Green
    }

    if ($cert.NotAfter -lt (Get-Date)) {
        throw ('这张证书已于 {0} 过期，装进去也没有意义，请向发布者索取新证书。' -f $cert.NotAfter.ToString('yyyy-MM-dd'))
    }
    if ($cert.NotBefore -gt (Get-Date)) {
        throw ('这张证书还没生效（{0} 起），请检查系统时间是否正确。' -f $cert.NotBefore.ToString('yyyy-MM-dd'))
    }

    # 只接受「代码签名」用途的证书；若证书里根本没有 EKU 扩展（典型 CA 根证书），提示但不阻止。
    $ekuExt = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    if ($ekuExt) {
        $ekuText = $ekuExt.Format($false)
        if ($ekuText -notmatch '1\.3\.6\.1\.5\.5\.7\.3\.3') {
            throw ('这张证书不是代码签名证书（EKU：{0}），装成受信任根没有意义，已中止。' -f $ekuText)
        }
        Write-Host '  用途检查：包含 Code Signing (1.3.6.1.5.5.7.3.3)' -ForegroundColor Green
    }
    else {
        Write-Warning '这看起来是一张 CA 根证书（没有 EKU 扩展），继续安装。'
    }

    # ---------------------------------------------------------------- 装 / 卸
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)

    try {
        $same = @($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })

        if ($Uninstall) {
            Write-Head '撤销信任'
            if ($same.Count -eq 0) {
                Write-Host '  当前用户受信任根里没有这张证书，无需撤销。' -ForegroundColor Yellow
            }
            else {
                foreach ($item in $same) { $store.Remove($item) }
                Write-Host '  已从 Cert:\CurrentUser\Root 移除。' -ForegroundColor Green
            }
        }
        else {
            Write-Head '安装信任'
            if ($same.Count -gt 0) {
                Write-Host '  已经装过了，无需重复安装。' -ForegroundColor Yellow
            }
            else {
                $store.Add($cert)
                Write-Host '  已加入 Cert:\CurrentUser\Root（只影响当前 Windows 用户，不需要管理员）' -ForegroundColor Green
            }
        }
    }
    finally {
        $store.Close()
    }

    # ------------------------------------------------------- 结果验证（证书链）
    Write-Head '验证'
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    $chainResult = $chain.Build($cert)

    if ($chainResult) {
        Write-Host '  证书链校验：通过（当前用户已信任这张证书）' -ForegroundColor Green
    }
    else {
        $reasons = ($chain.ChainStatus | ForEach-Object { $_.Status.ToString() }) -join '、'
        if ($Uninstall) {
            Write-Host ('  证书链校验：不通过（已撤销信任，符合预期：{0}）' -f $reasons) -ForegroundColor Yellow
        }
        else {
            Write-Warning ('  证书链校验：不通过（{0}）' -f $reasons)
        }
    }
    $chain.Dispose()

    # ------------------------------------------------------- 顺便报告 exe 状态
    if (-not $ExePath) {
        $guess = Join-Path $here 'MSIFAITOOLS.exe'
        if (Test-Path -LiteralPath $guess) { $ExePath = $guess }
    }
    if ($ExePath -and (Test-Path -LiteralPath $ExePath)) {
        $sig = Get-AuthenticodeSignature -LiteralPath $ExePath
        Write-Host ''
        Write-Host ('  程序　　：' + (Split-Path -Leaf $ExePath))
        Write-Host ('  签名状态：' + $sig.Status)
        if ($sig.SignerCertificate) {
            Write-Host ('  签名者　：' + $sig.SignerCertificate.Subject)
        }
        if ($sig.Status -eq 'Valid') {
            Write-Host '  → 现在双击运行不会再显示「未知发布者」。' -ForegroundColor Green
        }
        elseif ($sig.Status -eq 'NotSigned') {
            Write-Host '  → 这个 exe 没有签名，装信任改变不了任何显示。' -ForegroundColor Yellow
        }
        else {
            Write-Host '  → 仍有问题：请确认装的是不是「签这个 exe 的那张证书」。' -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host '完成。' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host ('失败：' + $_.Exception.Message) -ForegroundColor Red
    if (-not $NoPause) {
        Write-Host ''
        Read-Host '按 Enter 键关闭' | Out-Null
    }
    exit 1
}

if (-not $NoPause) {
    Write-Host ''
    Read-Host '按 Enter 键关闭' | Out-Null
}

