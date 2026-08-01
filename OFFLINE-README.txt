FilePrompt Windows 7 离线完整版
================================

使用方法
--------

1. 请先完整解压 ZIP，不要在压缩包中直接运行，也不要只复制 EXE。
2. 双击“Start-FilePrompt.exe”。
3. 启动器会自动检测 Microsoft .NET Framework 4.8。
4. 如果缺少运行环境，选择“是”。启动器会先校验随包安装程序的大小和
   SHA-256，再调用微软官方完整离线安装程序；此过程不会下载任何文件。
5. 安装完成后会自动启动 FilePrompt；如果提示重启，请重启电脑后再次
   双击启动器。

无需另外安装
------------

- WebView2、VC++ 运行库、Node.js、Python、Java。
- Microsoft Word 或 Excel。Word/表格文件的读取和导出由 app 目录中的 DLL 完成。
- PDF、Office、图片解析插件。所需的 28 个托管 DLL 已放在 app 目录。

必须保留 app 目录中的全部文件，不能只复制 FilePrompt.exe。

主要功能
--------

- 多会话、会话搜索、会话备份/恢复和运行期草稿保留。
- 连接自检、流式输出、Markdown 排版、资料预览。
- Word 文档和 CSV 表格导出，不依赖 Microsoft Office。
- 会话备份不包含 URL、API Key 或模型名称。

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
