FilePrompt AI for Windows 7 离线完整版
======================================

当前版本：v1.18
安装包：FilePromptAI-Win7-Full-v1.18.zip

能做什么
--------

- 连接单位内网中的 OpenAI Chat Completions 兼容模型。
- 读取用户主动添加的文本、代码、PDF、Office、图片、PPTX 和 XMind 文件。
- 支持多会话、搜索、置顶、归档、分支、备份和恢复。
- 可将回答或整个会话导出为 Markdown、文本、Word、PDF、PowerPoint、XMind、
  Excel 或 CSV。
- 支持离线技能，以及由用户明确启用的本地 stdio 和 Streamable HTTP MCP。

下载安装：只需三步
------------------

1. 取得 FilePromptAI-Win7-Full-v1.18.zip。
2. 将 ZIP 完整解压到一个文件夹。不要在压缩包内直接运行，也不要只复制某个 EXE
   或只复制 app 目录。
3. 进入解压目录，双击 Start-FilePromptAI.exe。

如果电脑缺少 Microsoft .NET Framework 4.8，启动器会先校验包内微软官方离线
安装程序，再提示并自动运行它；安装过程不会联网下载。如果安装程序要求重启，
重启电脑后再次双击 Start-FilePromptAI.exe。

普通用户无需运行 Verify-FilePromptAI.exe，也无需使用 --archive 参数。
Verify-FilePromptAI.exe 是发布维护者的验收工具，不是应用启动器。

首次使用
--------

1. 打开左侧“设置”→“模型连接”。
2. 填写单位提供的完整请求 URL 和模型名称。程序不会自动补全请求路径。
3. 服务需要鉴权时填写 API Key；无需鉴权的内网服务可留空。
4. 可使用“获取模型”读取标准兼容服务的同源 /models 列表。
5. 选择“测试连接”，确认成功后选择“保存并关闭”。

程序固定发送 OpenAI Chat Completions 格式请求。模型服务需要兼容 model、messages
和可选流式响应。填写 API Key 时会发送 Bearer 认证头；留空时不发送该认证头。

日常使用
--------

- 在底部输入问题，按 Enter 或点击发送；生成过程中可以停止，失败后可以重试。
- 通过“+ 添加”选择文件、粘贴内容或打开路径窗口，也可以把文件拖到专用拖放区。
- 粘贴文件路径后，只有主动点击“添加”才会读取；程序不会扫描目录。
- 双击附件或按 Enter 可以预览已读取的文字或图片。
- 文本资料可随会话历史保留；图片和其他无文本二进制附件只在当前轮发送，后续需要
  时请重新添加。
- 模型只收到文件名、提取内容和内联数据，不会收到本地文件路径。
- 左侧会话列表支持新建、重命名、搜索、置顶、归档、移回、删除和创建分支。
- 会话可备份为 .fpc 并合并恢复；备份不包含 URL、API Key 或模型配置。
- 可导出 Markdown、文本、Word、PDF、PowerPoint 和 XMind；Markdown 表格还可导出
  Excel 或 CSV。导出在本机完成，不要求安装 Microsoft Office 或 XMind。

完整运行期间，所有会话中尚未发送的二进制附件合计上限为 20 MB。中文 PDF 导出
需要系统具有可嵌入的中文字体，例如 Microsoft YaHei、SimSun 或 Noto Sans SC。

系统要求
--------

- Windows 7 必须安装 Service Pack 1；也支持 Windows 8、Windows 10 和 Windows 11。
- .NET Framework 4.8 已随完整包附带，缺少时由启动器离线安装，无需另行下载。
- 不需要另装 WebView2、VC++ 运行库、Node.js、Python、Java 或 Microsoft Office。
- 解压后的客户端运行不依赖 PowerShell。
- 长期未更新或被精简过的 Windows 7 可能还需要管理员离线安装微软 SHA-2
  代码签名支持、最新服务堆栈，以及提供 D3DCompiler_47.dll 的系统更新。这些
  Windows 补丁不在应用包内。
- 使用 HTTPS 内网模型服务时，Windows 需要支持 TLS 1.2 并信任服务端证书；
  单位内部 CA 的根证书需由管理员预先导入系统证书库。

1920×1080、100% 缩放只用于正式发布验收，不是普通用户运行程序的要求。

卸载
----

运行完整解压目录根部的 Uninstall-FilePromptAI.exe，或依次选择左侧
“设置”→“维护”→“卸载程序...”。

卸载器会先校验发布清单。文件缺失、被修改、被占用或路径身份异常时会在删除前
停止；发布目录中的额外文件不会被递归删除。用户配置和会话默认保留，只有明确
勾选并再次确认后才删除。

如果提示缺少 PACKAGE-CHECKSUMS-SHA256.txt，请重新完整解压原 ZIP，不要只复制
卸载器。正常数据目录固定为：

%LocalAppData%\FilePromptAI-Win7

安全与联网边界
--------------

- 安装和启动不访问互联网，也不会下载依赖、更新、遥测或扩展。
- 生成内容时只连接用户填写的完整模型请求 URL；模型请求和主动模型列表请求
  不使用 Windows 系统代理，也不跟随 3xx 重定向。
- API Key、MCP 命令、环境变量、URL 和请求头使用当前 Windows 用户的 DPAPI
  加密保存。
- 程序只读取用户当次明确选择、拖入、粘贴或确认添加的文件，不扫描目录。
- 从文件或剪贴板导入的技能和 MCP 不会因为导入而执行脚本或联网。
- 导入的 MCP 默认停用；stdio 服务启动及工具调用默认需要用户确认。
- stdio MCP 需要的 Node.js、Python、Java 或其他运行环境不随客户端安装，
  必须由管理员另行离线准备。只能启用来源可信且已经审查的 MCP。
- 基础问答只需要用户配置的 OpenAI Chat Completions 兼容端点；服务不可达时
  无法生成内容，这与安装依赖无关。

验证下载
--------

普通用户可选：核对 ZIP 的 SHA-256

从可信的 v1.18 发布页或带注释的 v1.18 Git 标签中的 src\RELEASE-SHA256.txt
取得公布摘要，再在 ZIP 所在目录运行：

(Get-FileHash .\FilePromptAI-Win7-Full-v1.18.zip -Algorithm SHA256).Hash

计算结果应与可信来源公布的摘要完全一致。单独计算当前文件的摘要只能确认文件
身份，不能代替可信来源。此核对是可选步骤，不影响正常启动。

发布维护者可选：Windows 7 正式验收
----------------------------------

本节只用于正式发布取证。普通用户无需执行，也无需使用 --archive。

验收机必须是 Windows 7 SP1，主屏为 1920×1080、100% 缩放。如果缺少
.NET Framework 4.8，先双击 Start-FilePromptAI.exe，由启动器调用包内离线
安装程序；如提示重启，重启后再继续。

保留本次解压所用的原始 ZIP，并完整解压到另一个目录。原始 ZIP 不得位于解压
目录内。进入解压根目录后运行：

.\Verify-FilePromptAI.exe --archive D:\Transfer\FilePromptAI-Win7-Full-v1.18.zip

这里的 --archive 在“正式验收命令”中不可省略，参数必须指向本次解压所用、
文件名仍为 FilePromptAI-Win7-Full-v1.18.zip 的原始 ZIP。它不是正常启动参数。

验收器保持原始 ZIP 的只读句柄，将 ZIP 的 SHA-256、大小和包清单身份写入报告，
再校验解压目录的精确文件集合。它使用独立临时数据和 127.0.0.1 回环服务检查
真实启动器、独立主进程、设置、两轮问答、附件、持久化和生产导出处理器，不连接
外部模型服务，不使用现有用户会话，也不修改注册表。

只有 Windows 7 SP1、.NET Framework 4.8、1920×1080@96 DPI 和全部功能检查
同时通过时，验收器才输出总 PASS 并返回退出码 0。Windows 8/10/11 上的报告会
明确失败 os.win7-sp1，不能作为 Windows 7 发布证据。

验收报告及其同名 .sha256.txt 校验文件默认写入 %TEMP%；如果该目录位于发布包内，
则改写到：

%LocalAppData%\FilePromptAI-Acceptance\AcceptanceReports

只有总 PASS 报告才能用于正式发布封存。发布维护者还必须按源码仓库
src\DEVELOPMENT.md 中的说明核对可信包身份、保留报告，并完成晋升、封存和标签复核。

随包 .NET Framework 4.8 离线安装程序
-------------------------------------

文件名：runtime\NDP48-x86-x64-AllOS-ENU.exe
文件大小：121346568 字节
SHA-256：0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40
数字签名：Microsoft Corporation
官方来源：https://go.microsoft.com/fwlink/?linkid=2088631

管理员可在命令提示符中离线复核：

certutil -hashfile runtime\NDP48-x86-x64-AllOS-ENU.exe SHA256

包内 PACKAGE-CHECKSUMS-SHA256.txt 列出了其余交付文件的 SHA-256。
