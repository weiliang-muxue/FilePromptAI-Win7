FilePrompt AI for Windows 7 离线完整版
======================================

使用方法
--------

1. 请先完整解压 ZIP，不要在压缩包中直接运行，也不要只复制 EXE。
2. 双击“Start-FilePromptAI.exe”。
3. 启动器会自动检测 Microsoft .NET Framework 4.8。
4. 如果缺少运行环境，选择“是”。启动器会先校验随包安装程序的大小和
   SHA-256，再调用微软官方完整离线安装程序；此过程不会下载任何文件。
5. 安装完成后会自动启动 FilePrompt AI；如果提示重启，请重启电脑后再次
   双击启动器。

卸载方法
--------

运行“Uninstall-FilePromptAI.exe”，或在主窗口选择“更多”→
“卸载 FilePrompt AI...”。卸载器只删除校验清单中内容未被修改的程序文件，
发布目录中的额外文件不会被递归删除。用户配置和会话默认保留；只有明确勾选
并再次确认后才会删除。

正常运行时，程序数据固定保存在当前用户的
%LocalAppData%\FilePromptAI-Win7，不会搜索、读取或迁移其他数据目录。

无需另外安装
------------

- WebView2、VC++ 运行库、Node.js、Python、Java。
- Microsoft Word 或 Excel。Word/表格文件的读取和导出由 app 目录中的 DLL 完成。
- PDF、Office、图片解析与导出组件。所需的 33 个托管 DLL 已放在 app 目录。

必须保留 app 目录中的全部文件，不能只复制 FilePromptAI.exe。

主要功能
--------

- 多会话、会话搜索、会话备份/恢复和运行期草稿保留。
- 文件可直接拖入、选择，或粘贴文件路径后主动点击读取；不会后台扫描目录。
- 连接自检、流式输出、Markdown 排版、资料预览和长会话上下文控制。
- 文本资料正文会进入会话历史；图片和无文本内联文件只在当前轮发送，后续轮次
  需要再次主动添加，不会根据路径自动重读。
- 最新回复或整个会话可导出 Word/PDF，Markdown 表格可导出 Excel/CSV，
  不依赖 Microsoft Office。
- 网络请求具有超时、有限重试和异常流式结束检测，避免无限等待或保存半截回复。
- 会话备份不包含 URL、API Key 或模型名称。
- 可从剪贴板安装普通文本、SKILL.md 或 JSON 离线技能；程序不会扫描技能目录、
  执行技能脚本或访问扩展商店。
- 支持 stdio 与 Streamable HTTP MCP，可从剪贴板导入标准 mcpServers JSON。
- MCP 命令、环境变量、URL 和请求头使用当前 Windows 用户的 DPAPI 加密保存；
  剪贴板导入后一律停用，stdio 启动和每一次工具调用默认都要人工确认。

离线扩展说明
------------

- 技能是保存在本机的模型指令，不需要额外运行环境；程序不会执行技能中的脚本。
- stdio MCP 的 EXE 和它自身需要的 Node.js、Python、Java 等运行环境不会由
  FilePrompt AI 下载或安装，必须由管理员提前从离线介质准备好。
- 手工启用 stdio MCP 后，每次实际启动前都会显示完整命令、工作目录、参数和
  环境变量名称，默认操作是拒绝。
- HTTP MCP 只连接用户填写的地址。模型只收到工具名称、说明、参数结构和获准
  执行后的结果，不会收到 MCP 命令、本地路径、环境变量、URL 或请求头。
- MCP 结果若回显上述较长的已知配置值，程序会先脱敏再交给模型。
- MCP 服务本身的文件和系统访问能力由该服务及当前 Windows 用户权限决定。
  脱敏不能阻止恶意服务编码或转发内容，只能启用管理员已审查的可信 MCP。

Windows 7 基础要求
------------------

- 必须是 Windows 7 Service Pack 1；不支持未安装 SP1 的系统。
- 微软 .NET Framework 4.8 完整离线安装程序已随包附带。
- 长期未更新或被精简过的 Windows 7，可能还需要先离线安装 SHA-2 代码签名
  支持、最新服务堆栈以及提供 D3DCompiler_47.dll 的系统更新。这些是 Windows
  自身补丁，当前包没有附带；请从单位的离线补丁源安装。
- 使用 HTTPS 内网模型服务时，Windows 需要支持 TLS 1.2。如果服务使用单位
  内部 CA，还需由管理员把单位根证书安装到 Windows 证书库。

网络说明
--------

程序安装和启动不访问互联网，也不会在线下载依赖。生成内容时，程序只会连接
界面中填写的完整请求 URL；该 URL 可以是内网地址。若内网模型服务不可达，
程序无法生成内容，这与安装依赖无关。

随包运行环境
------------

Microsoft .NET Framework 4.8 Offline Installer
文件名：runtime\NDP48-x86-x64-AllOS-ENU.exe
文件大小：121346568 字节
SHA-256：0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40
数字签名：Microsoft Corporation
官方来源：https://go.microsoft.com/fwlink/?linkid=2088631

管理员可在命令提示符中离线复核安装程序：

certutil -hashfile runtime\NDP48-x86-x64-AllOS-ENU.exe SHA256

包内 PACKAGE-CHECKSUMS-SHA256.txt 列出了其余交付文件的 SHA-256。
