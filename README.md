# FilePrompt AI for Windows 7

一个面向 Windows 7 与内网自定义模型的文件问答 AI 客户端，不依赖 WebView2。

## 当前发布

当前版本为 **v1.11**。这是仓库和离线安装包的唯一维护版本；本次新增本地多模型
配置，可保存、选择和删除多组完整 URL、API Key 与模型名称，其中 API Key 使用
Windows DPAPI 当前用户加密。最终离线包位于仓库外的 `exe` 目录，源码和构建脚本
均在本仓库中，便于内网环境审计、重建和离线分发。

## 当前功能

- 配置完整请求 URL、API Key、模型名称。
- 可在“更多”→“模型配置”保存多个内网模型预设，随时选择应用或删除；预设中的 API Key 使用 Windows DPAPI 加密。
- 可从剪贴板安装本地技能；技能是离线保存的 system 指令，可独立启用或停用。
- 支持本地 stdio 和 Streamable HTTP 两种 MCP；可粘贴标准 `mcpServers` JSON、
  测试连接、发现工具并完成多轮工具调用。
- 剪贴板导入的 MCP 一律保持停用；stdio 每次启动前先核对完整命令和参数，
  工具调用默认也逐次确认。
- 可拖入、选择多个本地文件，也可粘贴一个或多个文件路径后主动点击读取；
  不扫描目录，不会仅因粘贴路径就在后台读取。
- 从剪贴板粘贴文字、图片或资源管理器中复制的文件。
- 本地提取文本/代码、PDF、DOC/DOCX、RTF、XLS/XLSX。
- PNG、JPEG、BMP、GIF、TIFF 图片压缩后以内联 Base64 提交。
- 模型只收到文件名、提取出的内容和内联数据，不会收到本地路径。
- 文本资料正文会随会话历史保留；图片和无文本内联文件只发送当前轮，后续轮次
  若需再次查看请主动重新添加，程序不会偷偷从本地路径重读。
- 支持流式输出、停止、复制结果和保存为 Markdown/文本文件。
- 支持多会话、新建/重命名/删除会话，并在后续提问中携带当前会话历史；
  超长会话按预算保留最近的完整问答轮次，不拆分单条消息。
- 可搜索会话标题和近期内容；会话切换时保留当前运行期内尚未发送的草稿。
- 可独立测试 URL、Key、模型连接，不写入会话；配置完成后可收起连接区。
- 模型回复按标题、列表、代码块和 Markdown 表格排版显示。
- 双击或按 Enter 可预览已主动添加的文字/图片，不会重新读取本地文件。
- 全部会话可备份为 `.fpc` 并合并恢复；备份不包含 URL、API Key 或模型配置。
- 可把最新回复或整个会话导出为 Word（`.docx`）和 PDF；回答包含 Markdown
  表格时可导出为 Excel 工作簿（`.xlsx`）或 CSV。
- API Key 使用 Windows DPAPI 加密后保存在当前用户的本地配置目录。
- MCP 的命令、参数、工作目录、环境变量、URL 和请求头整体使用 Windows DPAPI
  加密保存；不会把这些连接配置作为提示词发给模型。
- 完整请求 URL 不跟随 3xx 重定向，避免把用户资料转发到其他地址。
- 网络请求具有响应头超时、流式空闲超时和有限重试；异常中断的流式响应不会
  被误判为完整答案。
- 会话按完整问答轮次原子保存；保存失败会回滚，重复启动会切回现有窗口。
- 对异常 Office XML、极端 Excel 列号和 CSV 公式内容进行安全限制与转义。
- 完整离线包包含独立卸载器；主窗口“更多”菜单也可启动卸载。

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
启用技能后，程序会在最前面增加 `system` 消息。启用并成功连接 MCP 后，程序
使用 Chat Completions 的 `tools`、`tool_choice: "auto"`、assistant
`tool_calls` 和 `tool` 结果消息完成最多 8 轮调用；自定义模型接口需兼容这些字段。

## 离线技能与 MCP

主窗口右上角的“技能 / MCP”入口完全在本机工作，不访问扩展商店，也不会下载
依赖。

技能只能手工新建或从剪贴板安装，不会扫描技能目录。剪贴板可直接放普通文本、
带 `name`/`description` YAML 头部的 `SKILL.md`，也可使用下面的 JSON。程序只把
技能正文作为模型指令保存，不执行其中的脚本、命令或网络操作：

```json
{
  "name": "合同审阅",
  "description": "单位内部审阅规则",
  "instructions": "先列风险，再逐条引用依据。",
  "enabled": true
}
```

MCP 可手工填写，也可把标准配置复制到剪贴板后选择“粘贴 JSON”：

```json
{
  "mcpServers": {
    "local-tools": {
      "command": "D:\\MCP\\server.exe",
      "args": ["--stdio"],
      "cwd": "D:\\MCP",
      "env": { "TOKEN": "内网令牌" }
    },
    "intranet-tools": {
      "url": "http://10.0.0.8:8080/mcp",
      "headers": { "Authorization": "Bearer 内网令牌" }
    }
  }
}
```

粘贴导入的 MCP 即使 JSON 中写了 `"enabled": true` 也不会自动启用。请先在界面
逐项核对，再手工开启需要的服务。

stdio MCP 所需的 EXE 及其运行环境必须由管理员提前离线放到电脑上；FilePrompt AI
只负责启动已配置的命令，不会在线安装 Node.js、Python、Java 或 MCP 服务。
每次生成中首次连接 stdio MCP 前，程序会展示服务名、完整命令、工作目录、全部
参数和环境变量名称；默认按钮为“拒绝”，只有选择“允许本次启动”后才会创建进程。
Streamable HTTP MCP 只连接用户填写的 URL，不跟随 3xx 重定向。模型只会看到
已发现工具的公开名称、说明和参数结构，看不到 stdio 命令、本地路径、环境变量、
HTTP URL 或请求头。工具返回内容只有在本次调用获准并执行后才会交给模型。
如果 MCP 结果回显了较长的已知命令、参数、工作目录、环境变量值、URL 或请求头
值，程序会在回传模型前把它们替换为脱敏标记。

“每次调用前确认”默认开启。只有在完全信任对应 MCP 服务和模型时才应关闭。
本地 MCP 自身能访问哪些文件或系统资源，取决于该 MCP 的实现和当前 Windows
用户权限，不是 FilePrompt AI 自动授予的能力。字符串脱敏无法阻止恶意 MCP 对内容
编码、拆分或转发；只能启用来源可信且已由管理员审查的 MCP。

## 系统要求

- Windows 7 SP1、Windows 8/10/11。
- .NET Framework 4.8。
- Windows 7 需要启用 TLS 1.2，并安装可用的系统根证书更新。

## Windows 7 离线完整版

优先使用 `FilePromptAI-Win7-Full-v1.11.zip`。完整解压后运行
`Start-FilePromptAI.exe`，启动器会检测 .NET Framework 4.8；缺少时会调用包内
经过微软数字签名的官方完整离线安装程序。安装过程不下载文件，也不需要访问
互联网。不要只复制 `app` 目录中的 EXE。

完整版已经附带 PDF、DOC/DOCX、XLS/XLSX 和图片处理所需的全部托管 DLL，
不需要另装 WebView2、VC++ 运行库、Node.js、Python、Java 或 Microsoft Office。
实际生成内容时，电脑仍需能够访问用户填写的完整请求 URL；该地址可以是内网
模型服务，不要求连接公网。

卸载时运行 `Uninstall-FilePromptAI.exe`，或在主窗口选择“更多”→
“卸载 FilePrompt AI...”。卸载器只删除校验清单内且内容未被修改的程序文件，
不会递归删除发布目录中的额外文件。用户配置和会话默认保留，只有明确勾选并
再次确认后才删除。

正常运行时，程序数据固定保存在当前用户的
`%LocalAppData%\FilePromptAI-Win7`，不会搜索、读取或迁移其他数据目录。

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

输出位于 `dist\FilePromptAI.exe`。发布时需要复制整个 `dist` 目录，不能只复制
EXE，因为 PDF 与旧版 Office 文件解析器由旁边的 DLL 提供。

包含 .NET Framework 4.8 离线安装程序的完整包可运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-offline-package.ps1
```

打包脚本只使用项目内已存在的 `packages` 与 `redist` 文件，不会联网还原或下载。
它会重建依赖、核对 33 个应用 DLL、验证 .NET 4.8 安装包的固定 SHA-256 与
Microsoft Authenticode 签名，并生成 `PACKAGE-CHECKSUMS-SHA256.txt`。仅准备和
检查发布目录而不生成 ZIP 时，可加 `-StageOnly`。

## 测试

构建、生成离线包并运行全部 21 组自动回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\RunAllSmokeTests.ps1
```

界面截图测试单独运行 `tests\CaptureUiSmokeTest.ps1`，支持正常窗口、最小窗口和
125% 物理尺寸预览。

## Offline export formats

- Word and PDF export remain built in and require no Microsoft Office installation.
- Markdown tables export to Excel workbooks or CSV files using bundled offline DLLs.
- Added PowerPoint `.pptx` export for the latest reply or the whole conversation.
- Added XMind `.xmind` mind-map export from headings, lists, paragraphs, code and tables.
- PPTX and XMind packages are generated locally; no network request or online installer is used.
## prompt actions and editable resend

- The output toolbar includes offline prompt actions for summaries, key points,
  translation, PPT outlines, XMind structures, and Markdown tables.
- Actions only fill the local prompt box; the user confirms and sends manually.
- The output context menu can load the last user instruction for editing and a
  new submission, matching the safer edit-and-resend workflow used by desktop AI clients.