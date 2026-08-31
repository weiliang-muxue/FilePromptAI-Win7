# FilePrompt AI v1.19 开发、测试与发布维护说明

本文面向开发者和发布维护者。普通用户只需阅读 [README.md](README.md)，下载
FilePromptAI-Win7-Full-v1.19.zip、完整解压并双击 Start-FilePromptAI.exe。普通用户
不需要执行本文命令，也不需要运行 Verify-FilePromptAI.exe。

本文只定义门禁，不表示某个安装包已经通过门禁。只有命令实际成功、证据完整且标签后
复核通过，才能把对应 ZIP 视为正式发布包。

## 维护环境与离线依赖

源码构建、打包和测试使用 Windows PowerShell 5.1。解压后的客户端运行不依赖
PowerShell。

仅克隆源码仓库不会得到被忽略的 packages、lib 和 redist 本地缓存。离线构建前
必须准备：

- 项目锁定的 packages 包缓存；
- 与 LIBRARIES-SHA256.txt 相符的 lib 依赖；
- 经过核验的 .NET Framework 4.8 完整离线安装程序
  redist\NDP48-x86-x64-AllOS-ENU.exe。

构建脚本不会联网还原或下载这些文件。打包时会核对 33 个托管 DLL 的固定 SHA-256，
并检查 .NET 安装程序的固定大小、SHA-256、产品信息和 Microsoft Authenticode 签名。

## 构建

以下命令均从仓库的 src 目录运行。

构建应用：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
~~~

输出位于 dist\FilePromptAI.exe。分发时必须保留整个 dist 目录，不能只复制 EXE，
因为 PDF 和旧版 Office 文件解析依赖同目录 DLL。

生成 v1.19 完整离线包：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\build-offline-package.ps1 -Version 1.19
~~~

打包脚本生成发布目录、PACKAGE-CHECKSUMS-SHA256.txt 和
FilePromptAI-Win7-Full-v1.19.zip。只准备并检查发布目录、不生成 ZIP 时使用：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\build-offline-package.ps1 -Version 1.19 -StageOnly
~~~

## 受限代码工作区实现

代码工作区使用 .NET Framework 4.8 和 Windows 原生文件 API，不依赖 Node.js、Python、
Git、MCP、WebView2 或在线安装。用户必须先选择一个本机固定磁盘上的已有代码文件；
`CodeWorkspace.OpenFromSelectedFile` 仅把该文件的父目录作为本次工作区根，拒绝磁盘根、
网络/移动盘、扩展路径、重解析点和硬链接。绝对根路径仅在客户端内部用于访问校验，
模型工具的参数与返回值只能使用相对路径。

`CodeWorkspaceToolProvider` 只注册四个内置工具：列出文件、搜索文字、读取文件和提交
修改。只允许处理工作区内已有、单个不超过 256 KiB 且可保真解码的文本文件；不创建、
删除、重命名文件，也不提供 CMD、PowerShell、编译、测试或其他进程执行能力。`.git`、`.svn`、`.hg`、
`.vs`、`node_modules`、`bin`、`obj`、`packages`、`vendor`、凭据文件、`.env*`、私钥和
证书等敏感名称在每层路径校验中被拒绝。

读取工具返回 SHA-256；提交修改必须携带该摘要，原文件变化时拒绝覆盖。写入前由
`WorkspaceDiffDialog` 显示 Diff，默认焦点是“拒绝”，只有用户明确确认才执行同目录
`ReplaceFileW` 原子替换；专项测试覆盖普通 NTFS 文件的 DACL、隐藏/存档属性和命名流
保留，EFS 加密、NTFS 压缩及其他特殊元数据未列入发布承诺。只读文件明确拒绝写入，
不自动解除保护。实现还保留原编码、BOM、换行及末尾换行，并用当前用户
DPAPI 加密保存备份，供最近一次撤销使用。替换后以回滚对象的文件 ID 和 SHA-256 复核
真正被换出的仍是模型读取版本；发生路径竞态时先恢复再拒绝。关闭工作区、切换会话或
退出应用必须和写入/撤销共用互斥边界，返回时授权和备份已经释放。

主窗口在代码模式下只向模型注册这些内置工作区工具，不开放普通资料附件或 MCP，避免
其他输入或扩展绕过工作区边界。客户端文件工具完全离线；生成修改方案仍会调用用户
配置的内网 Chat Completions URL，服务端必须兼容标准 `tools` / `tool_calls`。不支持
工具调用的 400、404 或 422 响应应显示明确错误，不能降级为声称已经修改的纯文本回答。

## 自动测试

构建应用、生成离线包并运行全部自动回归测试：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tests\RunAllSmokeTests.ps1 -Version 1.19
~~~

测试套件会安全解压本次新生成的 ZIP，核对精确文件集合和每个 SHA-256，并执行：

- 根目录启动器和卸载器自检；
- 真实 Start-FilePromptAI.exe 启动独立 app\FilePromptAI.exe 的检查；
- 主窗口响应、单实例、正常退出和持久化检查；
- 设置保存、Enter/按钮发送和两轮上下文旅程；
- 路径文本附件及 WinForms/OLE FileDrop 处理器接收 PNG 图片的检查；
- 受限代码工作区路径越界、重解析点/硬链接拒绝、敏感文件过滤、编码与换行保真、
  SHA 冲突、Diff 确认/拒绝、DACL/属性/命名流保留、只读拒绝、释放并发、DPAPI 备份、
  撤销和回环模型工具循环检查；
- 13 个生产导出处理器及生成文件内容检查；
- 重启恢复和应用内卸载路径检查；
- API、网络可靠性、文件提取、会话、扩展和安全边界回归。

自动旅程不执行真实的资源管理器鼠标拖动，不操作系统文件选择器或“另存为”窗口，也不
连接外部模型服务。它使用本次构建产物，不会拿 exe 目录中旧的晋升 ZIP 代替测试。

### 界面截图

主窗口截图：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tests\CaptureUiSmokeTest.ps1
~~~

发布前需要在主屏 1920×1080、100% 缩放（96×96 DPI）的真实环境执行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tests\CaptureUiSmokeTest.ps1 -FullHd100
powershell -ExecutionPolicy Bypass -File .\tests\CaptureExtensionsUiSmokeTest.ps1 -Mode Settings -FullHd100
powershell -ExecutionPolicy Bypass -File .\tests\CaptureExtensionsUiSmokeTest.ps1 -Mode Skills -FullHd100
powershell -ExecutionPolicy Bypass -File .\tests\CaptureExtensionsUiSmokeTest.ps1 -Mode Mcp -FullHd100
~~~

脚本会核对真实屏幕指标、当前显示模式和系统 DPI，不以缩放截图冒充规定环境，也不声称
125% 缩放属于正式发布验收。

## 包身份与摘要

正式包应从可信的带注释 v1.19 Git 标签读取固定摘要：

~~~powershell
git show v1.19:src/RELEASE-SHA256.txt
~~~

本地已测试但尚未正式封存的候选包，从被 Git 忽略的本地测试收据读取身份：

~~~powershell
Get-Content .\tests\build-artifacts\release\ReleaseCandidate-v1.19.txt
~~~

可信候选提交中的 Git LFS 指针，其 oid sha256 是 ZIP 内容摘要：

~~~powershell
git show <可信候选提交>:exe/FilePromptAI-Win7-Full-v1.19.zip
~~~

对实际文件重新计算：

~~~powershell
(Get-FileHash ..\exe\FilePromptAI-Win7-Full-v1.19.zip -Algorithm SHA256).Hash
~~~

预期摘要必须来自与包状态相符的可信来源，并与实际计算结果完全一致。只对手边文件重新
计算摘要只能标识当前字节，不能证明来源可信。

RELEASE-SHA256.txt 刻意不放入 ZIP，以免摘要自引用，也用于从包外锚定
Verify-FilePromptAI.exe 的可信身份。仓库 exe 目录最终只保留
FilePromptAI-Win7-Full-v1.19.zip；该 ZIP 由根目录 .gitattributes 使用 Git LFS
跟踪，不放摘要副本或其他说明文件。

## 正式发布提交链

正式发布必须依次形成三个不可跳步的提交：

1. **源码候选提交**：锁定待发布源码，工作树和索引必须干净。
2. **安装包晋升提交**：只加入测试收据绑定的原字节 ZIP。
3. **Win7 验收封存提交**：只加入两份由验收报告生成的正式证据文件。

三个阶段各自承担单一责任。任何一个提交夹带其他修改，都会破坏可复核性。

### 1. 创建源码候选提交和本地测试收据

先提交所有待发布源码，形成源码候选提交。确认工作树和索引为空，再运行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tests\RunAllSmokeTests.ps1 -Version 1.19 -WriteReleaseReceipt
~~~

只有全部测试和离线包验证成功后，脚本才会在被 Git 忽略的
tests\build-artifacts\release\ReleaseCandidate-v1.19.txt 写入结构版本 2 的本地
测试收据。收据绑定：

- 源码候选提交；
- 最终 ZIP 名称和 SHA-256；
- 发布暂存目录和 ZIP 内同一份包清单的原始字节 SHA-256；
- 包清单条目数。

本地测试收据只作为当前构建工作树的门禁输入，不提交到 Git，也不复制到 exe。
生成后不得重建 ZIP，也不得移动源码候选提交。

### 2. 创建安装包晋升提交

从源码候选提交的干净工作树运行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\promote-release-candidate.ps1 -Version 1.19
~~~

脚本把本地测试收据绑定的 ZIP 逐字节复制到 exe，并在写入前后确认该目录精确只含
当前 ZIP。源码候选提交必须已经删除 exe 中的旧包和其他条目，以免晋升提交夹带删除。

脚本还会从最终 exe ZIP 重复运行安装用户旅程。失败时会把单个 ZIP 恢复到晋升前的
原字节状态。成功后创建源码候选提交的直接子提交，即安装包晋升提交；它必须只修改：

~~~text
exe/FilePromptAI-Win7-Full-v1.19.zip
~~~

不得重压缩或重建该 ZIP。此时它仍是已测试候选包，不代表 Windows 7 已正式通过。

### 3. 在 Windows 7 上执行正式验收

把安装包晋升提交中的同一字节 ZIP 原样带到规定环境：

- Windows 7 Service Pack 1；
- .NET Framework 4.8；
- 主屏 1920×1080；
- 100% 缩放，即 96×96 system DPI。

如果验收机缺少 .NET Framework 4.8，先完整解压并双击 Start-FilePromptAI.exe，
由启动器校验和调用包内离线安装程序；如提示重启，重启后再验收。

保留原始 ZIP，并把它完整解压到另一个目录；原始 ZIP 不得位于解压目录内。从解压根
目录运行：

~~~powershell
.\Verify-FilePromptAI.exe --archive 'D:\Transfer\FilePromptAI-Win7-Full-v1.19.zip'
~~~

--archive 在正式验收命令中不可省略，必须指向本次解压所用且名称仍为
FilePromptAI-Win7-Full-v1.19.zip 的原始 ZIP。验收器在运行期间保持 ZIP 的只读
句柄，将 SHA-256、大小和包清单身份写入报告。

验收器不访问公网、不修改注册表、不请求管理员权限，也不使用现有用户会话。它先把
原始 ZIP 与 PACKAGE-CHECKSUMS-SHA256.txt 的身份绑定，再核对解压目录的精确文件
集合。启动检查运行真实启动器和独立主进程；功能旅程使用隔离临时数据目录及
127.0.0.1 回环服务，不连接外部模型。

报告采用 XML 结构版本 3，并生成同名 .sha256.txt。默认输出到 %TEMP%；如果该目录
位于发布包内，则改写到
%LocalAppData%\FilePromptAI-Acceptance\AcceptanceReports，不改变发布目录文件集合。

只有 Windows 7 SP1、.NET Framework 4.8、1920×1080@96 DPI 和全部检查同时通过，
验收器才输出总 PASS 并返回退出码 0。退出码按位表示：

- 1：操作系统环境失败；
- 2：.NET 环境失败；
- 4：显示环境失败；
- 8：包清单失败；
- 16：启动检查失败；
- 32：API 旅程失败；
- 64：文件旅程失败；
- 128：验收器内部错误。

Windows 8/10/11 会明确失败 os.win7-sp1，其诊断报告不能替代 Windows 7 证据。
只有总 PASS 的报告才以已验证状态记录锁定包清单的原始字节 SHA-256 和条目数；
失败报告只能用于诊断。

保留总 PASS XML 和同名报告 SHA-256 校验文件。两者都属于封存输入。

### 4. 创建 Win7 验收封存提交

回到仍位于安装包晋升提交、且仍保留本地测试收据的构建工作树，运行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\seal-release.ps1 -Version 1.19 -AcceptanceReportPath 'D:\Acceptance\FilePromptAI-Acceptance-....xml'
~~~

封存脚本要求：

- 当前提交是安装包晋升提交；
- 安装包晋升提交的唯一父提交是本地测试收据中的源码候选提交；
- 源码候选提交到安装包晋升提交恰好只修改单个 v1.19 ZIP；
- 索引为空，除即将生成的两份证据外工作树干净；
- exe 精确只含与收据一致的 ZIP；
- XML、同名校验文件、结构版本、总 PASS、退出码 0、v1.19 验收器和全部必需检查
  都有效；
- 本地测试收据、ZIP 身份与 Windows 7 报告直接绑定。

封存脚本使用禁用 DTD 和外部实体的 XML 读取方式，只生成：

~~~text
src/RELEASE-SHA256.txt
src/RELEASE-EVIDENCE.txt
~~~

两文件固定使用 UTF-8 无 BOM 和 CRLF，且 .gitattributes 必须禁止换行转换。只提交
这两个文件，形成安装包晋升提交的直接子提交，即 Win7 验收封存提交。该提交不得修改
ZIP 或其他路径。

带注释的 v1.19 Git 标签只能指向 Win7 验收封存提交。封存后不得再次运行任何会重建 ZIP
的命令。

### 5. 标签后复核

创建带注释的 v1.19 Git 标签后运行：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tests\VerifyTaggedRelease.ps1 -Version 1.19 -AcceptanceReportPath 'D:\Acceptance\FilePromptAI-Acceptance-....xml'
~~~

脚本复核：

- 标签确实指向 Win7 验收封存提交；
- 源码候选提交到安装包晋升提交只修改单个 ZIP；
- 安装包晋升提交到 Win7 验收封存提交只修改两份证据；
- 标签中的 CRLF 摘要、正式证据、Git LFS 对象摘要、本地 ZIP、本地测试收据和
  Windows 7 PASS 报告完全一致。

只有标签后复核实际通过，才可推送标签或发布资产。

## Microsoft Defender 发布扫描

发布前使用仓库内固定脚本执行 Microsoft Defender 自定义扫描：

~~~powershell
powershell -ExecutionPolicy Bypass -File .\tests\ScanReleaseWithDefender.ps1 -ScanPath '..\exe', '..\src', 'D:\已解压候选包'
~~~

固定脚本可避免临时拼接很长的 powershell -Command 或 pwsh -Command 审计命令被误判
为 ClickFix。脚本不清除历史检测，也不设置排除项；它记录扫描前后的 DetectionID，
任何新增检测都会使门禁失败。

发布记录必须同时写明 Defender 引擎和签名版本、扫描路径、历史检测数量及新增检测数量。

## 发布检查清单

- 版本号、包名、命令参数和带注释的 Git 标签均为 v1.19。
- 源码候选提交的工作树和索引干净。
- 本地测试收据由全部测试通过后生成，且之后未重建 ZIP。
- 安装包晋升提交只改变 exe/FilePromptAI-Win7-Full-v1.19.zip。
- 原字节 ZIP 在规定 Windows 7 环境取得总 PASS 报告和同名校验文件。
- Win7 验收封存提交只改变 src/RELEASE-SHA256.txt 和 src/RELEASE-EVIDENCE.txt。
- 带注释的 v1.19 Git 标签指向 Win7 验收封存提交。
- 标签后复核和 Defender 扫描均实际通过。
