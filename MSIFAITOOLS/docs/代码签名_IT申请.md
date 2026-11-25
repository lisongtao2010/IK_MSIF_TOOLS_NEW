# 代码签名证书 申请指南

> 目标：拿到一张可用的 Authenticode 代码签名证书，让 `MSIFAITOOLS.exe` 能真正签名。
> 本文包含**可直接转发的 IT 申请原文（日文/中文）**、脚本用法、验收清单和边界说明。

---

## 1. 已核实的事实（不要重复踩坑）

| 结论 | 依据 |
| --- | --- |
| `msif.pfx` / `cad.pfx` **不是**代码签名证书 | 两个文件都是 1764 字节、只有 1 个 `encryptedData` 节点、无证书链；项目里以 `<AssemblyOriginatorKeyFile>` + `SignAssembly=false` 使用，是 VS「创建测试证书」生成的强名称密钥 |
| 密码无法猜出 | 59 个候选密码全部失败；仓库/文档/脚本/提交历史里都没有登记密码 |
| 本机没有可用的代码签名证书 | `CurrentUser\My` / `LocalMachine\My` 里不存在带 EKU `1.3.6.1.5.5.7.3.3` 的证书 |
| 公司有内网 CA | 受信任根里有两个 `CN=lixil-CA, DC=lixil, DC=lan`（V0.0 / V1.0 续期）和 `CN=jpn-CA, DC=jpn, DC=ithd, DC=local` |
| 但 CA 不能自助申请 | AD 中 `objectClass=pKIEnrollmentService` 数量 = **0**，CA 证书也没有 AIA/CRL 扩展 → 独立（standalone）CA，只能由 IT 在 CA 侧手动签发 |
| 仓库现有产物全部未签名 | 扫描 `bin\` 下 75 个 exe，`Get-AuthenticodeSignature` 全部为 `NotSigned` |
| 签名链路本身是通的 | 已用临时自签名代码签名证书跑通 `signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256`，`signtool verify /pa` 通过 |

**结论：唯一卡点是「证书由谁签发」。这部分只能由 IT/CA 出证，本文把申请材料做到"IT 只需点签发"的程度。**

---

## 2. 流程总览（3 步）

```powershell
# 第 1 步：本机生成证书请求（私钥留在本机，不外发）
cd D:\Github_lis2010\IK_MSIF_TOOLS_NEW\MSIFAITOOLS
.\Tools\New-CodeSigningRequest.ps1 -Subject 'CN=MSIF Tools, OU=IT, O=LIXIL Corporation, C=JP'
#   → artifacts\codesign\codesign.req  （发给 IT）
#   → artifacts\codesign\codesign.inf  （参数说明，随信附上）

# 第 2 步（若 IT 愿意给 CA 名和模板名，可跳过人工往返）
.\Tools\New-CodeSigningRequest.ps1 -CaConfig 'CA服务器名\lixil-CA' -Template CodeSigning

# 第 3 步：拿到 IT 返回的 codesign.cer 后，装证书 + 导出 pfx
.\Tools\New-CodeSigningRequest.ps1 -CertificateFile .\codesign.cer

# 然后发布
.\Tools\Publish-Signed.ps1 -CertificateThumbprint <上一步打印的指纹>
```

脚本行为：

- `certreq -new` 生成 PKCS#10 请求，KeyUsage = Digital Signature，EKU = Code Signing（`1.3.6.1.5.5.7.3.3`），RSA 3072；
- 拿到证书后先做 **EKU 校验**，模板不对（例如签成了"客户端身份验证"证书）会在本机直接报错，不会等到发布时才发现；
- `certreq -accept` 把证书和本机私钥配对，再 `Export-PfxCertificate` 导出 `artifacts\codesign\code-signing.pfx`（已在 `.gitignore` 里，`*.pfx` 被忽略）。

> 换电脑构建时必须把 `code-signing.pfx` 带走（或用指纹在另一台机器的证书存储里导入），
> 因为私钥只存在生成请求的那台机器上。

---

## 3. 给 IT 的申请原文

### 3.1 日文版（建议直接使用）

```
件名：【依頼】社内ツール用「コード署名証明書」の発行について

お疲れさまです。<氏名>（<部署>）です。

社内配布している Windows 用ツール（MSIF Tools / C# .NET 製 exe）について、
配布先端末の Windows SmartScreen や一部のウイルス対策ソフトで
「発行元が不明なプログラム」として警告・誤検知が出ております。

配布先で警告なく起動できるよう、Authenticode コード署名を付与したく、
社内 CA（lixil-CA）で「コード署名（Code Signing）」テンプレートの
証明書を 1 枚発行していただけないでしょうか。

■ 用途
　Windows デスクトップアプリ（exe）への Authenticode 署名

■ 要求内容
　・件名（Subject）：CN=MSIF Tools, OU=IT, O=LIXIL Corporation, C=JP
　　（社内標準の記載があれば合わせます）
　・Extended Key Usage：Code Signing (1.3.6.1.5.5.7.3.3)
　・Key Usage：Digital Signature
　・鍵長：RSA 3072bit
　・証明書形式：.cer（Base64）でご返送ください
　・有効期間：社内規定に合わせます

■ 添付ファイル
　・codesign.req … 証明書要求（PKCS#10）
　・codesign.inf … 上記パラメータの定義ファイル

■ 発行方法（いずれでも結構です）
　(a) certsrv の「証明書の要求」→「詳細設定の証明書要求」を開き、
　　　Base64 形式の要求を貼り付けて、テンプレート「コード署名」で発行
　(b) CA サーバ上で以下を実行
　　　certreq -submit -config <CAサーバ名>\lixil-CA -cert CodeSigning codesign.req codesign.cer

　CA サーバ名・CA 名・テンプレート名をご教示いただければ、
　こちらの端末から提出〜取得まで自動で実施いたします。

■ 補足
　・秘密鍵は当方端末内で生成・保管しており、要求ファイルには含まれておりません。
　・証明書（pfx）はソース管理に登録せず、配布ビルド時のみ使用します。
　・タイムスタンプは外部 TSA（DigiCert 等）を利用予定です。
　　外部通信が不可の場合は、社内 TSA の有無をご教示ください。

ご確認のほど、よろしくお願いいたします。
```

### 3.2 中文版（自己留档 / 走中文工单时用）

```
主题：【申请】内部工具用「代码签名证书」签发

你好，我是<姓名>（<部门>）。

我们内部配发的 Windows 工具（MSIF Tools，C#/.NET 编译的 exe）在客户端上
会被 Windows SmartScreen 以及部分杀毒软件判为「未知发布者」并告警/误报。
为了让它带 Authenticode 签名，想请在公司内网 CA（lixil-CA）上用
「代码签名（Code Signing）」模板给我签发 1 张证书。

■ 用途：Windows 桌面程序（exe）的 Authenticode 签名
■ 申请内容
　· 主题（Subject）：CN=MSIF Tools, OU=IT, O=LIXIL Corporation, C=JP
　· 扩展密钥用法：Code Signing (1.3.6.1.5.5.7.3.3)
　· 密钥用法：Digital Signature
　· 密钥长度：RSA 3072
　· 证书格式：请把 .cer（Base64）回传
　· 有效期：按公司规定
■ 附件
　· codesign.req —— 证书请求（PKCS#10）
　· codesign.inf —— 上述参数定义
■ 签发方式（任选其一）
　(a) certsrv →「申请证书」→「高级证书申请」，粘贴 Base64 请求，
　　　模板选「代码签名」后签发；
　(b) 在 CA 服务器上执行
　　　certreq -submit -config <CA服务器名>\lixil-CA -cert CodeSigning codesign.req codesign.cer
■ 补充
　· 私钥只存在于我的电脑，请求文件里不含私钥。
　· 证书（pfx）不入源码库，只在发布构建时使用。
　· 时间戳计划使用外部 TSA（DigiCert）；若不允许访问外网，请告知是否有内部 TSA。

谢谢！
```

### 3.3 需要 IT 明确回答的 4 个问题

1. CA 服务器名和 CA 名（形如 `CAHOST\lixil-CA`）—— 有了就能 `-CaConfig` 一条命令走完；
2. 模板名（`CodeSigning` / `コード署名` / 公司自定义名）；
3. 有效期（1 年 / 2 年 / 3 年）；
4. 是否允许使用外部时间戳服务器（`http://timestamp.digicert.com`），不允许的话是否有内部 TSA。

---

## 4. 证书到手后的验收清单

| 检查项 | 命令 | 期望结果 |
| --- | --- | --- |
| 证书带私钥 | `Get-ChildItem Cert:\CurrentUser\My\<指纹> \| Select-Object HasPrivateKey` | `True` |
| EKU 正确 | `certutil -v -store My <指纹>` | 含 `コード署名 / Code Signing (1.3.6.1.5.5.7.3.3)` |
| 签发者可信 | `Get-ChildItem Cert:\LocalMachine\Root \| Where-Object { $_.Subject -match 'lixil-CA' }` | 有 `lixil-CA` 根（客户端机器上也要有） |
| 能签 | `.\Tools\Publish-Signed.ps1 -CertificateThumbprint <指纹>` | 输出 `签名状态 : Valid` |
| 签名可验证 | `signtool verify /pa /v <exe>` | `Successfully verified` |
| 时间戳存在 | `signtool verify /pa /v <exe>` 输出里 `Timestamp` 段 | 有 `Issued to: DigiCert Timestamp...` |

发布脚本里已内置前两项的校验（`Assert-CodeSigningCertificate`），模板不对会直接报错并提示原因。

---

## 5. 边界：内网 CA 签名能解决什么、不能解决什么

必须提前说清楚，避免签完名发现"误报还在"：

| 场景 | 内网 CA 签名（lixil-CA） | 公网 OV 证书 | 公网 EV 证书 |
| --- | --- | --- | --- |
| 公司域内机器打开 exe 不再报「未知发布者」 | ✅ | ✅ | ✅ |
| 属性页显示公司名（LIXIL） | ✅（显示 `lixil-CA` 签发的主体） | ✅ | ✅ |
| 消除 SmartScreen「Windows 已保护你的电脑」 | ❌ 根证书不在微软信任列表的不算"已知发布者" | ⚠️ 需要积累下载声誉，数周 | ✅ 较快 |
| 减少杀软「启发式/未知程序」误报 | ⚠️ 部分能改善 | ⚠️ 需配合误报上报 | ⚠️ 同一套上报流程 |
| 内网分发（网络共享 / 拷贝） | ✅ 足够 | ✅ | ✅ |

结论：**内网 CA 签名是"内部可信 + 公司身份"的最低成本方案**；如果最终目标是"发给外部客户也不报警"，
还需要 §6 的公网证书或 Azure Trusted Signing，并按 `病毒误报_对策.md` §5 向各杀软厂商报误报。
连申请流程都不想走的话，用 §6 的「自签 + `-IncludeTrustedPublisher`」自助方案（只限公司内部，**不需要 IT**）。

另外两点容易忽略：

- 客户端必须信任 `lixil-CA` 根证书。域内机器通常已通过组策略分发；**域外/客户机器不会有**，
  这种机器上签名会显示"无法验证签名"（比不签名更容易引起怀疑，所以对外发布不要用内网 CA 证书）。
- 私钥泄漏等于公司身份被盗用。`code-signing.pfx` 不要进 git、不要放网络共享、不要放进发布包，
  建议放 `D:\certs\` 或密码管理器，并设置强密码；离职/换机时通知 IT 吊销。

---

## 6. 备选方案（内网 CA 走不通时）

| 方案 | 年费量级 | 硬件要求 | 说明 |
| --- | --- | --- | --- |
| 公司内网 CA（本文主方案） | 0 | 无 | 需 IT 出证，只对内网有效 |
| 公网 OV 代码签名证书（DigiCert / Sectigo / GlobalSign） | 3k–6k CNY | 无（私钥在文件/USB） | 2023 起要求企业实名 + 电话核验，签发 1–5 工作日 |
| 公网 EV 代码签名证书 | 6k–15k CNY | **必须硬件令牌（HSM/USB）** | SmartScreen 声誉最好，签名需要插令牌 |
| Azure Trusted Signing | 约 $120/年 | 无（云签名） | 需要 Azure 订阅 + 企业资质审核（约 1–3 周），CI 里用 `TrustedSigning` 任务 |
| 自签证书（`Tools\New-SelfSignedCodeSigningCert.ps1`）+ `-IncludeTrustedPublisher` | 0 | 无 | **不需要 IT**：签名打包时把 cer 与安装脚本一起发出去，接收人跑一次即显示发布者。仍只对内网有效，公网 SmartScreen 照弹 |

Azure Trusted Signing 对"个人开发者/小团队 + CI 自动发布"最省事，但中国区账号能否通过资质审核需要先确认。

「免费」这个词要先说清（2026-11 核实）：**公网 CA 已经没有免费代码签名证书可申请**——

* Let's Encrypt / ZeroSSL / Buypass / Google Trust Services 只签 TLS 证书（EKU = `serverAuth`），
  没有 Code Signing EKU，`signtool` 会直接拒绝；
* SignPath.io、Certum/Sectigo 的 OSS 免费额度只给**公开开源项目**，要仓库 public + 许可证审核 + 人工批准，
  公司内部工具不适用；
* 真正免费又能立刻用的只有两条：**公司内网 CA**（本文主方案，需 IT 出证）和
  **自签证书**（`.\Tools\New-SelfSignedCodeSigningCert.ps1`，RSA 3072 / 3 年 / Code Signing EKU，
  同时导出 pfx（签名用）与 cer（GPO 部署根证书用））。

自签证书实测（2026-11-25）：签名本身完全正常（Authenticode + DigiCert 时间戳）；`Get-AuthenticodeSignature`
在未装根证书的机器上是 `UnknownError`，装进「受信任的根证书颁发机构」后立即变 `Valid`。
所以它只适合「域内 GPO 分发根证书」或「测试机验证签名流水线」，**不能对外分发**。
详见 [病毒误报_对策.md](病毒误报_对策.md) 4.4 节。

---

## 7. 一页速查

```powershell
# 1) 生成请求（发给 IT）
.\Tools\New-CodeSigningRequest.ps1

# 2) 证书装回并导出 pfx
.\Tools\New-CodeSigningRequest.ps1 -CertificateFile .\codesign.cer

# 3) 签名发布
.\Tools\Publish-Signed.ps1 -CertificateThumbprint <指纹>

# 4) 验证
signtool verify /pa /v .\artifacts\MSIFAITOOLS\MSIFAITOOLS.exe
```

拿不到证书时的临时方案（不需要签名也能用）：

```powershell
# 打包但不签名，通过内网共享分发（网络共享来源的文件没有 Mark-of-the-Web，不会触发 SmartScreen）
.\Tools\Publish-Signed.ps1 -SkipSign
```

完全不想走 IT 流程时（自签 + 接收人自助装信任，只限公司内部）：

```powershell
# 1) 生成自签代码签名证书（只做一次，RSA 3072 / 3 年 / Code Signing EKU）
.\Tools\New-SelfSignedCodeSigningCert.ps1

# 2) 签名打包，并把 trusted-publisher.cer + Install-TrustedPublisher.ps1 一起打进包
.\Tools\Publish-Signed.ps1 -CertificateThumbprint <自签证书指纹> -IncludeTrustedPublisher
```

接收人只需运行一次包内的 `Install-TrustedPublisher.ps1`（装进当前用户根证书存储，**不需要管理员**），
之后「未知发布者」消失、`Get-AuthenticodeSignature` 变 `Valid`；撤销用 `-Uninstall`。
原理、实测结果与限制（不能对外、不提高 SmartScreen 信誉）见
[病毒误报_对策.md](病毒误报_对策.md) 4.5 节。

想先有一条「内部可信」的签名链（域内 GPO 部署根证书后显示公司名）：

```powershell
# 1) 生成自签证书：装进 CurrentUser\My，导出 msif-codesign-selfsigned.pfx + msif-codesign-root.cer
.\Tools\New-SelfSignedCodeSigningCert.ps1

# 2) 按指纹签名发布（输出目录 artifacts\codesign-selfsigned，含 说明.txt）
.\Tools\Publish-Signed.ps1 -CertificateThumbprint <自签证书指纹>

# 3) 把 msif-codesign-root.cer 交给 IT 用 GPO 部署到「受信任的根证书颁发机构」
#    之后域内机器上「发布者」显示证书主体名，Get-AuthenticodeSignature = Valid
```

注意：自签证书对公网用户无效（对方不会装你的根证书），SmartScreen 该弹还弹。
只在「内网分发 + 域内信任」或「测试签名流水线」两种场景使用。

