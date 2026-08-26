# FilePrompt AI for Windows 7

一个面向 Windows 7 与内网自定义模型的文件问答 AI 客户端，不依赖 WebView2。

## 版本与发布状态

当前唯一维护版本为 **v1.17**。版本号或包名本身不表示已经正式发布：只有 annotated
`v1.17` 标签中的 `RELEASE-SHA256.txt`、同一 ZIP 的成功测试 receipt，以及规定环境中的
Windows 7 PASS XML 和 sidecar 全部通过封存与标签后复核时，该 ZIP 才是正式发布包；
缺少任一项时均按候选包处理。这是仓库和离线安装包的唯一维护版本；界面采用左侧会话导航、
右侧会话记录和底部消息编辑器布局；模型、技能/MCP、会话和维护选项
统一收进左侧“设置”窗口，不占用主工作区。
附件列表直接收进底部消息编辑器：通过“+ 添加”选择文件、粘贴内容或打开路径窗口，
有资料时才展开；发送新消息时只追加和更新本轮内容，不再清空并重画已有历史。
左侧会话支持置顶、当前/已归档视图，并可从最新模型回复创建独立分支；分支复制
此前上下文，原会话保持不变。
打包脚本会在项目根目录生成 `FilePromptAI-Win7-Full-v1.17.zip` 及其 SHA-256
校验 sidecar。便利 sidecar 不决定发布状态；通过上述门禁后，固定摘要另存于对应 Git
标签跟踪的 `RELEASE-SHA256.txt`。源码和构建脚本均保留在
仓库中，便于内网环境审计、
重建和离线分发。

## 当前功能

- 配置完整请求 URL、模型名称和可选 API Key；无需鉴权的 Ollama、vLLM 等
  OpenAI 兼容服务可将 Key 留空。
- 对标准 OpenAI 兼容 URL 可主动读取同源 `/models` 列表并选择模型；模型框仍可编辑，
  服务未提供列表接口时继续支持手动输入。
- 可在左侧“设置”→“模型连接”→“模型配置...”保存多个内网模型预设，随时选择应用或删除；预设中的 API Key 使用 Windows DPAPI 加密。
- 可从剪贴板或用户显式选择的单个本地 UTF-8 文件安装技能；文件支持 `SKILL.md`、
  JSON 或普通文本，大小上限为 2 MiB。技能是离线保存的 system 指令，可独立启用或停用。
- 支持本地 stdio 和 Streamable HTTP 两种 MCP；可粘贴标准 `mcpServers` JSON，
  也可由用户显式选择单个不超过 2 MiB 的本地 UTF-8 JSON 文件导入，然后测试连接、
  发现工具并完成多轮工具调用。
- 剪贴板或本地文件导入的 MCP 一律保持停用；stdio 每次启动前先核对完整命令和参数，
  工具调用默认也逐次确认。
- 可拖入、选择多个本地文件，也可粘贴一个或多个文件路径后主动点击读取；
  不扫描目录，不会仅因粘贴路径就在后台读取。
- Windows 路径检查超过 15 秒会保留未读路径供重试；若上一次网络路径检查仍在由
  Windows 收尾，新操作不会再启动第二个后台检查，避免不可取消的路径探测不断累积。
- Windows 7 只注册一个始终可见的专用拖放区，避免子控件句柄变化导致
  `DragDrop 注册失败`；拖放不可用时仍可用文件选择器或路径读取。
- 从剪贴板粘贴文字、图片或资源管理器中复制的文件。
- 本地提取文本/代码、PDF、DOC/DOCX、RTF、XLS/XLSX、PPTX 和 XMind；
  PPTX 保留自然页序、标题、正文、表格和备注，新版 XMind `content.json` 与
  旧版 `content.xml` 保留多画布、主题层级及备注。
- PNG、JPEG、BMP、GIF、TIFF 图片压缩后以内联 Base64 提交。
- 当前运行期跨会话草稿合计最多保留 20 MB 二进制附件，避免 Win7 32 位进程内存耗尽；
  超限时需先发送或移除已有附件。
- 模型只收到文件名、提取出的内容和内联数据，不会收到本地路径。
- 文本资料正文会随会话历史保留；图片和无文本内联文件只发送当前轮，后续轮次
  若需再次查看请主动重新添加，程序不会偷偷从本地路径重读。
- 支持标准 Chat Completions SSE，以及常见兼容网关返回的 Responses 文本事件封装、
  `delta.text` 和 NDJSON 文本分片；停止、复制结果，并可将最新回复或整个会话保存为
  Markdown/文本文件。思考字段、工具参数和加密内容不会混入最终回复。
- 可对最新模型回复原位重新生成，用新结果替换原回复，避免在会话中堆叠重复问答；
  请求失败后可从失败位置快速重试。
- 可在主工作区快速切换已保存的模型配置，无需先打开设置窗口。
- 会话历史固定在右侧上方并占据主要空间；新回复只更新当前问答，过期流式分片会
  被丢弃，不会再次追加到已经完成的回复后面。
- 窗口缩放和最小尺寸下专用拖放入口保持句柄稳定，对话记录和输入框仍然可见；
  完整上下文摘要可通过悬停提示和无障碍描述查看。
- 主窗口、设置窗口和路径窗口使用 WinForms DPI 缩放，应用清单声明 system-DPI-aware；
  固定发布显示门禁读取实际屏幕指标、当前显示模式和 96×96 system DPI，不使用截图缩放
  来替代真实环境。
- 支持多会话、新建/重命名/删除会话，并在后续提问中携带当前会话历史；
  超长会话按预算保留最近的完整问答轮次，不拆分单条消息。
- 会话可置顶、归档或移回，并在“当前 / 已归档”视图间切换；置顶会话始终排在
  当前视图前面。可从最新模型回复创建会话分支，在保留原路线的同时继续新的讨论。
- 当前轮的文字描述、技能提示和提取文件正文也共同遵守 48,000 字符预算；
  文件正文超出时保留开头并加入明确截断标记，避免模型返回上下文超限错误。
- 可搜索会话标题和近期内容；会话切换时保留当前运行期内尚未发送的草稿。
- 可在“设置”→“模型连接”中独立测试 URL、Key、模型连接或主动获取模型列表，
  不写入会话，也不会提前写入 `settings.xml`；验证失败时会定位到对应输入项。
- “设置”窗口按一次打开/保存形成事务：在其中应用模型配置只更新待保存值，只有
  保存并关闭父窗口才写入 `settings.xml`；取消会恢复打开前的连接、生成参数和快捷键。
  模型配置列表本身仍由“模型配置”窗口单独保存。
- 模型回复按标题、列表、代码块和 Markdown 表格排版显示。
- 双击或按 Enter 可预览已主动添加的文字/图片，不会重新读取本地文件。
- 全部会话可备份为 `.fpc` 并合并恢复；备份不包含 URL、API Key 或模型配置。
- 可把最新回复或整个会话导出为 Markdown、文本、Word（`.docx`）、PDF、
  PowerPoint（`.pptx`）和 XMind（`.xmind`）；回答包含 Markdown 表格时可导出为 Excel 工作簿
  （`.xlsx`）或 CSV。全部导出均在本机完成。
- 底部“快捷指令”可填入总结、提炼、翻译、PPT 大纲、XMind 结构和 Markdown
  表格模板；右键对话记录可载入上一条用户指令进行编辑重发，模板不会自动发送。
- 填写的 API Key 使用 Windows DPAPI 加密后保存在当前用户的本地配置目录。
- MCP 的命令、参数、工作目录、环境变量、URL 和请求头整体使用 Windows DPAPI
  加密保存；不会把这些连接配置作为提示词发给模型。
- 完整请求 URL 不跟随 3xx 重定向，避免把用户资料转发到其他地址。
- 网络请求具有响应头超时、流式空闲超时和有限重试；进入成功响应体后若断流或读取超时，
  不会自动重复提交。无二进制附件的文字请求只有收到 `[DONE]`、`finish_reason: stop`
  或普通 `done: true` 且没有正文、思考、工具、拒绝或错误内容时，才自动改用一次非流式
  请求；`content_filter`、`length`、`tool_calls` 等结束状态不会重提。任何二进制附件
  请求都不会自动重复上传。附件请求使用 120 秒等待上限，
  HTTP 400/413/415/422 会显示服务端信息以及
  本轮附件名称、类型和大小；二进制数据在 Base64 序列化前后均检查 32 MB 请求上限。
- 会话按完整问答轮次原子保存；保存失败会回滚，重复启动会切回现有窗口。
- 会话文件内容损坏时先在同目录重命名保留；若有效文件只是暂时被占用、无权限或
  无法安全读取，程序不会把它标记为损坏或尝试移动，而是进入粘性只读保护且绝不覆盖原文件。
- 关闭程序时若仍有未发送文字、已添加资料或运行中的任务，会默认阻止误退出并要求确认。
- 对异常 Office XML、极端 Excel 列号和 CSV 公式内容进行安全限制与转义。
- 完整离线包包含根目录卸载器；完整解压后，可直接运行该卸载器，也可通过左侧
  “设置”→“维护”→“卸载程序...”启动卸载。

## 固定接口格式

程序把填写的 URL 当作完整请求地址，原样发起 `POST`，不会补路径或切换接口。
填写 API Key 时发送认证头；Key 留空时不发送 `Authorization`：

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
程序不会把上述请求自动改写为 Responses、Anthropic 或 Ollama 原生请求格式；这些服务
应提供 Chat Completions 兼容入口。响应解析可兼容标准 SSE、部分网关使用的 Responses
`response.output_text.delta` 事件封装，以及 `application/x-ndjson` 等逐行 JSON 文本分片。
这项响应兼容不代表原生支持其他请求协议。
启用技能后，程序会在最前面增加 `system` 消息。启用并成功连接 MCP 后，程序
使用 Chat Completions 的 `tools`、`tool_choice: "auto"`、assistant
`tool_calls` 和 `tool` 结果消息完成最多 8 轮调用；自定义模型接口需兼容这些字段。
若首轮包含二进制附件，后续工具回合只保留附件名称、类型和大小的文字占位，
不会再次发送 Base64 数据。

## 离线技能与 MCP

左侧“设置”→“技能与 MCP”入口完全在本机工作，不访问扩展商店，也不会下载
依赖。

技能可手工新建、从剪贴板安装，或由用户显式选择单个本地 UTF-8 文件导入；本地文件
支持普通文本、带 `name`/`description` YAML 头部的 `SKILL.md` 或 JSON，大小上限为
2 MiB。程序不会扫描技能目录，只读取用户当次选中的文件；只把技能正文作为模型指令
保存，不执行其中的脚本或命令，也不会因导入操作联网：

```json
{
  "name": "合同审阅",
  "description": "单位内部审阅规则",
  "instructions": "先列风险，再逐条引用依据。",
  "enabled": true
}
```

MCP 可手工填写，也可把标准配置复制到剪贴板后选择“粘贴 JSON”，或由用户显式选择
单个不超过 2 MiB 的本地 UTF-8 JSON 文件导入。程序不会扫描目录、执行文件中的脚本
或因导入操作联网：

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

从剪贴板或本地文件导入的 MCP，即使 JSON 中写了 `"enabled": true` 也不会自动启用。
请先在界面逐项核对，再手工开启需要的服务。

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

- Windows 7 必须安装 Service Pack 1；也支持 Windows 8/10/11。
- 程序需要 .NET Framework 4.8，完整离线包已经附带微软官方完整离线安装程序。
- 长期未更新或经过精简的 Windows 7 可能还要先安装微软 SHA-2 代码签名支持、
  最新服务堆栈以及提供 `D3DCompiler_47.dll` 的系统更新；这些 Windows 补丁不在
  应用离线包内。
- 访问 HTTPS 模型或 HTTP MCP 地址时，Windows 需要支持 TLS 1.2，并信任服务端
  证书使用的根证书；单位内部 CA 证书需由管理员预先导入系统证书库。
- 中文 PDF 导出需要系统已安装 PDFsharp 可嵌入的 CJK 字体，例如 Microsoft YaHei、
  SimSun 或 Noto Sans SC。精简系统若删除了这些字体，需要由管理员离线补装。

## Windows 7 离线完整版

优先使用 `FilePromptAI-Win7-Full-v1.17.zip`。在解压或运行其中任何程序前，先按本节开头
的门禁证据判断包状态。已封存的正式包应从可信 annotated `v1.17` 标签取得固定摘要；
未满足正式发布门禁的包必须按候选包处理，并从可信候选提交中的 `exe/README.txt`
独立取得候选摘要。随后核对 ZIP：

```powershell
# 正式包
git show v1.17:src/RELEASE-SHA256.txt
# 候选包
git show <可信候选提交>:exe/README.txt
certutil -hashfile .\FilePromptAI-Win7-Full-v1.17.zip SHA256
```

只能使用与包状态相符的摘要来源，列出的摘要必须与计算结果完全相同。随 ZIP 生成的
`.zip.sha256.txt` 只是便利副本，若它与 ZIP
来自同一下载位置，不能独立证明 ZIP 未被替换。`RELEASE-SHA256.txt` 刻意不放进
ZIP，避免摘要自引用；它也为包内 `Verify-FilePromptAI.exe` 提供外部身份锚点。

校验成功后把 ZIP 完整解压到同一个目录，不要在压缩包内运行，也不要只复制其中某个
EXE；然后从解压根目录运行 `Start-FilePromptAI.exe`。启动器会检测 .NET Framework 4.8；缺少时会调用包内
经过微软数字签名的官方完整离线安装程序。安装过程不下载文件，也不需要访问
互联网。不要只复制 `app` 目录中的 EXE。

运行 ZIP 已包含 .NET Framework 4.8 完整离线安装程序，以及 PDF、DOC/DOCX、
XLS/XLSX 和图片处理所需的 33 个托管 DLL；安装和启动不会联网下载依赖，
不需要另装 WebView2、VC++ 运行库、Node.js、Python、Java 或 Microsoft Office。
实际生成内容时，电脑仍需能够访问用户填写的完整请求 URL；该地址可以是内网
模型服务，不要求连接公网。模型请求和主动模型列表请求不使用 Windows 系统代理，
也不会访问更新、遥测、扩展商店或其他辅助服务；基础问答只需要这个 OpenAI
Chat Completions 兼容端点。用户主动启用的 stdio MCP 及其运行环境不属于客户端
运行 ZIP，仍需按“离线技能与 MCP”一节由管理员另行准备。

卸载时运行完整解压目录根部的 `Uninstall-FilePromptAI.exe`，或依次选择左侧
“设置”→“维护”→“卸载程序...”。卸载器会先锁定并校验清单列出的全部程序文件；文件缺失、
被修改、被占用或路径身份异常等预检失败会在删除前停止，不删除程序文件或用户数据。
缺少 `PACKAGE-CHECKSUMS-SHA256.txt` 时，错误窗口会列出实际检查目录；应重新完整解压
ZIP，不能只复制卸载器。验证通过后也不会递归删除发布目录中的额外文件。用户配置和
会话默认保留，只有明确勾选并再次确认后才删除。若 Windows 文件系统在提交删除或
撤销删除标记时发生极少见异常，卸载器会明确报告可能的部分删除，尽量保留根卸载器并
写入恢复标记；再次运行即可清理剩余文件，恢复信息损坏时则重新完整解压原 ZIP 后重试。
删除用户数据前会先锁定和复核整棵数据目录；任一文件占用、路径身份或重解析点检查
失败时不会先删掉其他文件。若受控测试进程设置了 `FILEPROMPTAI_DATA_ROOT` 且它不等于
默认目录，界面和后台 worker 都会拒绝删除用户数据，并保留默认目录与覆盖目录。

正常运行时，程序数据固定保存在当前用户的
`%LocalAppData%\FilePromptAI-Win7`，不会搜索、读取或迁移其他数据目录。

对于长期未更新或被精简过的 Windows 7，上述微软系统更新和字体必须由内网管理员
通过可信离线介质预先安装。应用包不会联网获取系统补丁、证书或字体。

### Windows 7 一键验收

在目标 Windows 7 SP1 电脑上先安装包内 .NET Framework 4.8，将主屏设置为
1920×1080、100% 缩放。保留原始 ZIP，并把它完整解压到另一个目录；原始 ZIP
不得放在解压目录内。进入解压目录后运行，`--archive` 参数不可省略：

```powershell
.\Verify-FilePromptAI.exe --archive `
    'D:\Transfer\FilePromptAI-Win7-Full-v1.17.zip'
```

参数必须指向这次解压所用、名称仍为 `FilePromptAI-Win7-Full-v1.17.zip` 的原始 ZIP。
验收期间程序会保持该 ZIP 的只读句柄并把其 SHA-256、大小和包清单身份写入报告。

验收程序不访问公网、不修改注册表、不请求管理员权限，也不使用用户现有会话数据。
运行前必须先按上一节判断包状态：正式包从可信 Git 标签取得固定摘要，其他包从可信
候选提交取得独立候选摘要。包内清单不能单独证明验收程序
自身未被替换。
它会把原始 ZIP 身份与解压载荷进行比对，校验精确包清单，再从解压根目录运行
`Start-FilePromptAI.exe`，由启动器启动真实 `app\FilePromptAI.exe`。随后使用独立临时
数据目录，并通过 127.0.0.1
回环服务验证无 API Key 的主动 `/models` 发现及流式 Chat Completions；TXT、PDF、
DOCX、PNG 解析和 DOCX、PDF、PPTX、XLSX、CSV、XMind 导出也直接调用包内真实程序。
XML schema 2 报告和同名 `.sha256.txt` 写到 `%TEMP%`；如果该目录位于发布包内，则
改写到 `%LocalAppData%\FilePromptAI-Acceptance\AcceptanceReports`，不会改变包内文件
集合。只有总结果为 `PASS` 的报告才会以 `packageIdentity status="verified"` 记录已经锁定
并验证的 `PACKAGE-CHECKSUMS-SHA256.txt` 原始字节 SHA-256 和条目数；失败报告只写
`status="unverified"`，不能作为发布封存证据。

只有 Windows 7 SP1、.NET Framework 4.8、1920×1080@96 DPI 和全部功能检查同时
通过时才返回退出码 0 并输出总 `PASS`。退出码按位表示失败：1=系统、2=.NET、
4=显示、8=包清单、16=启动、32=API、64=文件、128=验收程序内部错误。在
Windows 8/10/11 上运行会明确失败 `os.win7-sp1`，这是防止伪造 Win7 验收结论的
预期行为；其他检查仍可独立给出诊断。

## 构建

源码构建、打包和测试使用构建机上的 Windows PowerShell 5.1；这是构建工具要求，
解压后的客户端运行不依赖 PowerShell。在 PowerShell 5.1 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `dist\FilePromptAI.exe`。发布时需要复制整个 `dist` 目录，不能只复制
EXE，因为 PDF 与旧版 Office 文件解析器由旁边的 DLL 提供。

包含 .NET Framework 4.8 离线安装程序的完整包可运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-offline-package.ps1
```

仅克隆源码仓库不会得到被忽略的 `packages`、`lib` 和 `redist` 本地缓存。离线重建
前必须另行准备项目锁定的 `packages` 包缓存和经过核验的 .NET Framework 4.8
`redist` 安装程序；构建脚本不会联网还原或下载这些文件。
打包脚本只使用项目内已存在的 `packages` 与 `redist` 文件，不会联网还原或下载。
它会重建依赖、按仓库 `LIBRARIES-SHA256.txt` 核对 33 个应用 DLL 的固定 SHA-256、
验证 .NET 4.8 安装包的固定 SHA-256 与
Microsoft Authenticode 签名，并生成 `PACKAGE-CHECKSUMS-SHA256.txt`。仅准备和
检查发布目录而不生成 ZIP 时，可加 `-StageOnly`。

正式发布使用三段不可跳步的提交链：`C`（源码候选）→ `P`（原字节晋升）→ `S`
（Windows 7 PASS 证据封存）。本文档只定义门禁，不表示当前仓库已经取得 Windows 7
PASS。

先提交待发布源码为 `C`，确认整个工作树和索引均为空，再以发布候选模式运行完整测试。
只有全部测试和离线包验证成功后，脚本才会在被 Git 忽略的
`tests\build-artifacts\release` 中写入 schema 2 receipt，把 `C`、最终 ZIP 的名称、
SHA-256，以及 staging/ZIP 中同一份包清单的原始字节 SHA-256 和条目数绑定。ZIP 大小
由晋升脚本从这份 SHA-256 已绑定的原字节文件读取并写入候选证据：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\RunAllSmokeTests.ps1 `
    -Version 1.17 -WriteReleaseReceipt
```

receipt 生成后不得重建 ZIP 或移动 `C`。从 `C` 的干净工作树运行晋升脚本：

```powershell
powershell -ExecutionPolicy Bypass -File .\promote-release-candidate.ps1 `
    -Version 1.17
```

脚本逐字节复制 receipt 绑定的 ZIP 和 sidecar 到 `exe`，再生成候选说明与候选证据。
`C` 必须已经删除 `exe` 中所有旧版本 ZIP、sidecar 和候选证据；晋升脚本会在写入前
拒绝任何旧版或未授权条目，并在写入后复核目录精确只含 `.gitattributes` 和当前四项，
不会把额外删除混入 `P`。四项写入后，脚本还会从最终 `exe` ZIP 运行固定的完整安装
用户旅程；只有最终路径的清单、启动器、两轮上下文、路径附件、导出、重启恢复和
应用内卸载检查全部通过，晋升事务才会提交。该旅程失败时四项交付文件会恢复为晋升前
的原字节状态，不能留下“源码候选已测试但 `exe` 仍不可用”的半成品。
随后创建 `C` 的直接子提交 `P`；`P` 必须只修改以下四个路径，不能夹带源码或正式
发布结论：

```text
exe/FilePromptAI-Win7-Full-v1.17.zip
exe/FilePromptAI-Win7-Full-v1.17.zip.sha256.txt
exe/README.txt
exe/ReleaseCandidate-v1.17.txt
```

其中 ZIP 由 Git LFS 跟踪；晋升不允许重压缩或重建它。`P` 仍只是已测试候选，不表示
Windows 7 已通过。

将 `P` 中的同一字节 ZIP 原样带到规定环境，在 Windows 7 SP1、.NET Framework 4.8、
1920×1080@96 DPI 机器上解压到独立目录，同时把原始 ZIP 留在解压目录之外，然后运行：

```powershell
.\Verify-FilePromptAI.exe --archive `
    'D:\Transfer\FilePromptAI-Win7-Full-v1.17.zip'
```

只有验收器输出总 `PASS` 时，才同时保留 XML 和同名 sidecar；Windows 10/11 的诊断报告
不能替代该证据。回到仍位于 `P` 且保留本地 receipt 的构建工作树，把报告路径作为必填
参数运行封存：

```powershell
powershell -ExecutionPolicy Bypass -File .\seal-release.ps1 `
    -Version 1.17 `
    -AcceptanceReportPath 'D:\Acceptance\FilePromptAI-Acceptance-....xml'
```

封存要求 `HEAD` 是 `P`、`P` 的唯一父提交是 receipt 中的 `C`、`C`→`P` 恰好只有上述
四个路径，且索引为空。除封存脚本将要写入的两个文件外，工作树必须干净；`exe` 中
ZIP、sidecar 和候选证据也必须仍与 receipt 完全一致。脚本以禁止 DTD 和外部实体的 XML
读取器验证报告 sidecar、schema、总 `PASS`/退出码 0、v1.17 verifier、全部必需检查唯一
且为 `pass`，并要求报告、receipt、候选证据及 ZIP 内清单的身份完全一致。

封存脚本只生成以下两个正式证据文件。只提交这两个文件，创建 `P` 的直接子提交 `S`：

```text
src/RELEASE-SHA256.txt
src/RELEASE-EVIDENCE.txt
```

两文件固定使用 UTF-8 无 BOM 和 CRLF，且 `.gitattributes` 禁止 Git 换行转换。`S` 必须
恰好是两文件 seal commit，不能修改 ZIP 或任何其他路径；annotated `v1.17` 标签只可
指向 `S`。
封存后不得再次运行会重建 ZIP 的命令。

标签建立后运行：

```powershell
powershell -ExecutionPolicy Bypass `
    -File .\tests\VerifyTaggedRelease.ps1 `
    -Version 1.17 `
    -AcceptanceReportPath 'D:\Acceptance\FilePromptAI-Acceptance-....xml'
```

验证脚本要求标签指向 `S`，复核 `C`→`P` 的四路径 promotion commit 和 `P`→`S` 的
两文件 seal commit，并按原始字节确认标签中的 CRLF 摘要、本地 ZIP、sidecar、receipt、
候选证据与所提供 Windows 7 PASS 报告的身份一致。只有该命令实际通过后，才可发布或
推送标签与资产；文档和候选文件本身不能证明已经通过。

## 测试

构建、生成离线包并运行全部自动回归测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\RunAllSmokeTests.ps1
```

套件会安全解压本次刚生成的 ZIP，逐项核对精确文件集合和每个 SHA-256，运行根目录
启动器与卸载器自检；随后从解压根目录运行 `Start-FilePromptAI.exe`，由它启动真实
`app\FilePromptAI.exe`，再验证主窗口响应、
单实例门禁、正常退出和关闭持久化，再通过真实 WinForms 消息循环深测设置保存、
Enter/按钮发送、两轮上下文、路径附件、导出入口，以及应用内卸载器路径和
`--from-app` PID。它不会使用 `exe` 目录中先前晋升的 ZIP 代替本次构建产物。
晋升脚本会在事务提交前另行对最终 `exe` 路径重复这条真实用户旅程；这两个门禁分别
保证新构建产物本身可用，以及用户实际取得的晋升字节可用。

界面截图测试单独运行 `tests\CaptureUiSmokeTest.ps1`，支持正常窗口和最小窗口。
截图进程会声明 system-DPI-aware，使窗口边界和截图使用同一物理坐标空间；该脚本
不提供或声称 125% 实机验收。发布前在 1920×1080、100% 缩放的主屏上运行
`tests\CaptureUiSmokeTest.ps1 -FullHd100`，脚本会校验屏幕指标、当前显示模式和 96×96 DPI，
并按完整工作区生成固定验收截图。设置、技能和 MCP 窗口分别使用
`tests\CaptureExtensionsUiSmokeTest.ps1 -Mode Settings`、`-Mode Skills` 和
`-Mode Mcp` 留存截图；发布验收时三条命令均加 `-FullHd100`，脚本同样会拒绝
非 1920×1080 或非 96 DPI 的环境。

构建机发布前使用仓库内固定脚本执行 Microsoft Defender 自定义扫描，避免把临时长
`powershell -Command` 或 `pwsh -Command` 审计命令误判成 ClickFix。扫描脚本不清除历史
检测、不设置排除项，只比较本次扫描前后的 DetectionID；任一新增检测都会失败：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\ScanReleaseWithDefender.ps1 `
    -ScanPath '..\exe', '..\src', 'D:\已解压候选包'
```

发布记录必须同时写明 Defender 签名版本、扫描路径、历史检测数量和新增检测数量。
