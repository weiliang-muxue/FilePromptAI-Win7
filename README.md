# FilePrompt Win7

一个不依赖 WebView2 的 Windows 7 文件内容模型客户端。

## 当前功能

- 配置完整请求 URL、API Key、模型名称。
- 拖入或选择多个本地文件。
- 从剪贴板粘贴文字、图片或资源管理器中复制的文件。
- 本地提取文本/代码、PDF、DOC/DOCX、RTF、XLS/XLSX。
- PNG、JPEG、BMP、GIF、TIFF 图片压缩后以内联 Base64 提交。
- 模型只收到文件名、提取出的内容和内联数据，不会收到本地路径。
- 支持流式输出、停止、复制结果和保存为 Markdown/文本文件。
- 支持多会话、新建/重命名/删除会话，并在后续提问中携带当前会话历史。
- 可搜索会话标题和近期内容；会话切换时保留当前运行期内尚未发送的草稿。
- 可独立测试 URL、Key、模型连接，不写入会话；配置完成后可收起连接区。
- 模型回复按标题、列表、代码块和 Markdown 表格排版显示。
- 双击或按 Enter 可预览已主动添加的文字/图片，不会重新读取本地文件。
- 全部会话可备份为 `.fpc` 并合并恢复；备份不包含 URL、API Key 或模型配置。
- 可把回答导出为 Word（`.docx`）；回答包含 Markdown 表格时可导出为 CSV 表格。
- API Key 使用 Windows DPAPI 加密后保存在当前用户的本地配置目录。

## 固定接口格式

程序把填写的 URL 当作完整请求地址，原样发起 `POST`，不会补路径或切换接口。
认证头固定为：

```text
Authorization: Bearer <API Key>
Content-Type: application/json
```

请求体使用 Chat Completions 形式：

```json
{
  "model": "填写的模型名称",
  "messages": [
    {
      "role": "user",
      "content": "用户指令与提取出的文件内容"
    }
  ],
  "stream": true
}
```

有图片时，`content` 使用 `text` + `image_url` 的多模态数组格式。

## 系统要求

- Windows 7 SP1、Windows 8/10/11。
- .NET Framework 4.8。
- Windows 7 需要启用 TLS 1.2，并安装可用的系统根证书更新。

## Windows 7 离线完整版

优先使用 `FilePrompt-Win7-Full-v1.4.zip`。完整解压后运行
`Start-FilePrompt.exe`，启动器会检测 .NET Framework 4.8；缺少时会调用包内
经过微软数字签名的官方完整离线安装程序。安装过程不下载文件，也不需要访问
互联网。不要只复制 `app` 目录中的 EXE。

完整版已经附带 PDF、DOC/DOCX、XLS/XLSX 和图片处理所需的全部托管 DLL，
不需要另装 WebView2、VC++ 运行库、Node.js、Python、Java 或 Microsoft Office。
实际生成内容时，电脑仍需能够访问用户填写的完整请求 URL；该地址可以是内网
模型服务，不要求连接公网。

对于长期未更新或被精简过的 Windows 7，微软的 .NET Framework 4.8 安装程序
仍可能要求操作系统已具备 SP1、SHA-2 代码签名支持、最新服务堆栈以及
`D3DCompiler_47.dll`。这些属于 Windows 系统更新，不是应用 DLL，当前完整版
没有附带。应由内网管理员通过离线补丁源预先安装。HTTPS 请求若使用单位内部
证书，还需把单位根证书安装到 Windows 证书库。

## 构建

在 PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `dist\FilePrompt.exe`。发布时需要复制整个 `dist` 目录，不能只复制
EXE，因为 PDF 与旧版 Office 文件解析器由旁边的 DLL 提供。

包含 .NET Framework 4.8 离线安装程序的完整包可运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-offline-package.ps1
```

打包脚本只使用项目内已存在的 `packages` 与 `redist` 文件，不会联网还原或下载。
它会重建依赖、核对 28 个应用 DLL、验证 .NET 4.8 安装包的固定 SHA-256 与
Microsoft Authenticode 签名，并生成 `PACKAGE-CHECKSUMS-SHA256.txt`。仅准备和
检查发布目录而不生成 ZIP 时，可加 `-StageOnly`。
