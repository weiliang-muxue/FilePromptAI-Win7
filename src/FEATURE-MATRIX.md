# FilePrompt AI v1.19 功能矩阵

本文档按常见桌面 AI 客户端能力核对 FilePrompt AI v1.19 源码。它描述产品能力和明确
边界，不替代测试报告、目标 Windows 7 验收报告或发布包 SHA-256 证明。只有 annotated
`v1.19` 标签中的正式摘要和证据、同一 ZIP 的成功测试收据、规定环境中的 Windows 7
PASS XML（验收时另附同名校验文件）全部通过封存与标签后复核时，该 ZIP 才是正式
发布包；否则均按候选处理。
发布证据链为“源码候选提交 → 安装包晋升提交 → Win7 验收封存提交”；
本矩阵描述实现与门禁，不声称当前仓库已经取得 Windows 7 PASS。

## 状态说明

| 状态 | 含义 |
| --- | --- |
| 已实现 | v1.19 源码已有完整用户路径及对应实现或自动测试证据；这是实现状态，不单独表示已经正式发布。 |
| v1.19 补齐项 | 本轮为 v1.19 增加或加固；只有最终源码、离线包、自动回归和目标机验收全部一致并通过发布门禁时，才属于正式发布。 |
| 未实现（非 v1.19 承诺） | 常见客户端可能提供，但本版本没有相应用户路径；这不自动表示永久排除，也不是 v1.19 发布承诺。 |
| 有意排除 | 与本项目的 Windows 7、内网、自带依赖、用户主动连接边界不符，本版本明确不提供。 |

表中“证据”引用源码文件和稳定的类型或方法名，避免行号随后续维护调整而失效。

## 核心能力

| 常见能力 | v1.19 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 自定义模型端点 | 已实现 | `src/SettingsDialog.cs` 的模型连接页；`src/ModelClient.cs` 的 `GenerateAsync` 按用户填写的完整 URL 发送 OpenAI Chat Completions 请求。 | 不自动补全聊天路径，不原生适配 Responses、Anthropic 等其他协议；服务可通过兼容层接入。 |
| 模型发现与手工选择 | 已实现 | `src/ModelClient.cs` 的 `FetchModelsAsync` 读取同源 `/models`；模型输入仍可编辑；`tests/NetworkReliabilitySmokeTest.cs` 覆盖端点推导、响应校验和重定向拒绝。 | 模型列表是用户主动请求，不做后台发现。 |
| 有鉴权及无鉴权连接 | v1.19 补齐项 | `src/MainForm.cs`、`src/SettingsDialog.cs` 和 `src/ModelProfiles.cs` 允许 API Key 留空；`src/ModelClient.cs` 仅在 Key 非空时发送 `Authorization`；网络及模型配置回归覆盖匿名请求。 | 适用于 Ollama、vLLM 等无需鉴权的 OpenAI 兼容端点；不代替服务端访问控制。 |
| 多模型配置保存与快速切换 | 已实现 | `src/ModelProfiles.cs` 的 `ModelProfileStore`、`src/ModelProfilesDialog.cs` 以及 `src/MainForm.cs` 的快速模型菜单。 | 配置保存在当前 Windows 用户本机，不云同步；跨用户不可解密的配置会被忽略。 |
| 设置窗口事务保存 | v1.19 补齐项 | `src/MainForm.cs` 的 `ShowSettingsDialog` 快照连接、生成参数和快捷键；测试连接、获取模型及应用模型配置都只使用待保存值，父窗口确认后才写 `settings.xml`，取消则恢复快照；`tests/UiStateSmokeTest.cs` 在真实 STA 消息循环和模态窗口中逐字节验证磁盘不提前改变。 | 模型配置列表由其独立窗口单独保存；这里的事务边界是父设置窗口所管理的应用设置。 |
| 普通用户可编辑的 system prompt | v1.19 补齐项 | `src/SettingsDialog.cs` 提供编辑页；`AppSettings.SystemPrompt` 与 `ModelProfile.SystemPrompt` 持久化；`MainForm.BuildCombinedSystemPrompt` 把自定义提示放在技能指令之前；`ModelClient.BuildInitialMessages` 将合并结果作为首条 `system` 消息；生成设置测试覆盖合并顺序和预算。 | 最多输入 16,000 字符，并与技能及会话共同服从 48,000 字符预算。 |
| `temperature`、`top_p`、最大输出 token | v1.19 补齐项 | 设置页和模型配置均提供独立启用开关；`AppSettings`、`ModelProfile`、`ModelRequest` 使用可空值；`MainForm.GenerateAsync` 传入请求，`ModelClient.AddGenerationOptions` 映射为 `temperature`、`top_p`、`max_tokens` 并校验范围；生成设置测试覆盖发送/省略、范围拒绝和持久化往返。 | 未启用时不写入载荷，由服务采用默认值。传输响应大小限制不是生成 token 上限。 |
| 流式输出与停止 | v1.19 补齐项 | `src/ModelClient.cs` 白名单解析 Chat SSE、Responses 文本事件封装、`delta.text` 与 NDJSON；`src/MainForm.cs` 管理当前生成和取消；网络回归覆盖代表性兼容报文矩阵、思考/工具字段隔离、错误事件、空完成降级、断流和取消。 | 请求始终是 Chat Completions 格式，不原生适配 Responses、Anthropic 或 Ollama 请求协议；只有无二进制附件且由 `[DONE]`、`finish_reason: stop` 或普通 `done: true` 结束的干净空流可自动改用一次非流式请求。 |
| 失败重试与回复重新生成 | 已实现 | `src/MainForm.cs` 的 `RetryLastFailedGeneration`、`StartRegeneration` 和原位替换逻辑；`tests/UiStateSmokeTest.cs` 覆盖成功、失败、取消及状态失效。 | 仅最新完整问答可原位重新生成；一次性二进制附件不会从旧本地路径静默重读。 |
| 多轮上下文与预算控制 | 已实现 | `src/ConversationContextBudget.cs` 按完整问答轮次裁剪；`src/MainForm.cs` 将历史、当前文字、技能和附件正文纳入 48,000 字符预算。 | 这是字符预算，不是特定模型 tokenizer 的精确 token 计数。 |
| MCP 工具调用 | 已实现 | `src/McpRuntime.cs` 支持 stdio 与 Streamable HTTP；`src/ModelClient.cs` 支持 `tools`、`tool_calls` 和最多 8 轮工具循环；相关 MCP 与工具循环 smoke tests。 | MCP 仅在普通问答模式、由用户配置并启用后连接；代码工作区启用时不注册 MCP。stdio 运行环境不随客户端安装。 |

## 输入、阅读与输出

| 常见能力 | v1.19 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 文本、代码和 Office/PDF/XMind 附件问答 | 已实现 | `src/FileContentExtractor.cs` 的 `ExtractFile` 支持普通文本/代码、PDF、DOC/DOCX、RTF、XLS/XLSX、PPTX，以及新版 `content.json` 和旧版 `content.xml` XMind；PPTX 提取页标题、正文、表格和备注，XMind 保留多画布、主题层级和备注；提取与加固 smoke tests 覆盖正常及恶意压缩包。 | 普通附件模式仅处理用户主动添加的文件，不扫描目录或发送本地路径；代码工作区启用时不开放资料附件。 |
| 受限代码工作区修改 | v1.19 补齐项 | `src/CodeWorkspace.cs` 从用户选择的已有代码文件确定固定磁盘父目录，所有模型路径均强制为相对路径并在每次操作前复核文件身份；`src/CodeWorkspaceToolProvider.cs` 只提供列举、搜索、读取及提交修改四个内置工具；`src/WorkspaceDiffDialog.cs` 在写入前显示 Diff 并默认拒绝；SHA-256 基线、普通 NTFS 文件的 DACL/隐藏与存档属性/命名流保留、DPAPI 加密备份、最近一次撤销及释放并发由专项回归覆盖。 | 只处理工作区内已有、单个不超过 256 KiB、可写且受支持的文本文件；只读文件需由用户先解除保护并重新读取。不创建、删除、重命名或执行命令。拒绝网络/移动盘、磁盘根、重解析点、硬链接、版本库元数据、依赖/构建目录、凭据、环境变量、私钥和证书。EFS、NTFS 压缩及其他特殊元数据未列入承诺。代码模式不开放资料附件或 MCP，关闭工作区、切换会话或关闭程序即释放授权。 |
| 图片输入与视觉模型载荷 | 已实现 | `src/FileContentExtractor.cs` 压缩 PNG/JPEG/BMP/GIF/TIFF；`src/ModelClient.cs` 构造 `text` 与 `image_url` 多模态内容。 | 依赖所选模型支持兼容的视觉输入；不包含 OCR 引擎或本地图像识别。 |
| 拖放、文件选择、路径读取和剪贴板 | 已实现 | `src/MainForm.cs` 的文件添加、专用拖放区、`OnReadPathClick` 和 `OnPasteClick`；UI 状态测试覆盖拖放句柄和忙碌状态。 | 粘贴路径本身不会触发后台读取，必须由用户执行读取动作。 |
| Windows 路径解析单飞与超时恢复 | v1.19 补齐项 | `src/MainForm.cs` 的 `pendingPathResolution` 和 `AddFilesAsync` 对路径检查设置 15 秒等待上限；旧探测仍由 Windows 收尾时拒绝再起 worker，并保留失败路径供重试；`tests/UiStateSmokeTest.cs` 的 `TestPathResolutionSingleFlight` 覆盖阻塞、释放和恢复。 | 不能强制中止已进入操作系统的网络路径调用；单飞门禁用于避免重复操作累积后台任务。 |
| 附件预览与生命周期保护 | 已实现 | `src/InputPreviewDialog.cs` 预览已读入内容；`src/MainForm.cs` 实施草稿二进制内存预算和一次性二进制规则。 | 文本正文可进入历史；图片和无文本二进制只发送当前轮，后续需要重新添加。 |
| Markdown 阅读 | 已实现 | `src/MarkdownRichTextRenderer.cs` 和 `src/MarkdownDocument.cs` 支持标题、列表、代码块、引用和表格排版；`tests/MarkdownRendererSmokeTest.cs` 验证渲染。 | 代码块没有逐块复制按钮或语法高亮。 |
| 复制与 Markdown/纯文本保存 | 已实现 | `src/MainForm.cs` 的 `OnCopyOutputClick`、`ExportText`、`BuildConversationMarkdown` 和 `BuildConversationPlainText`。 | 可保存最新回复或整个会话；复制对象仍是当前最新回复，不是任意富文本片段管理器。 |
| 文档、演示、表格和思维导图导出 | 已实现 | `DocxExporter`、`PdfExporter`、`PptxExporter`、`XlsxExporter`、`CsvExporter`、`XMindExporter`，由 `src/MainForm.cs` 的导出菜单调用；各导出 smoke tests。 | 全部本地生成；PDF 中文输出依赖系统存在可嵌入的 CJK 字体。 |
| 语音输入、朗读和实时语音对话 | 有意排除 | 源码和 UI 没有麦克风、STT、TTS 或音频会话路径。 | v1.19 不请求麦克风权限、不采集音频，也不随包提供语音模型。 |
| 内置图像生成或编辑 | 未实现（非 v1.19 承诺） | 当前图片只作为用户附件发送给兼容视觉模型。 | 没有图像生成 API、画布或本地图像生成模型。 |

## 会话与数据

| 常见能力 | v1.19 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 本地持久会话 | 已实现 | `src/ConversationStore.cs` 的原子 XML 存储、完整问答提交、损坏文件保留和临时占用只读保护；会话存储、备份及 UI smoke tests。 | 数据固定在当前用户的 `%LocalAppData%\FilePromptAI-Win7`；暂时被占用或无权限的有效文件不会被移动或覆盖。 |
| 会话新建、重命名、删除和搜索 | 已实现 | `ConversationStore.CreateSession`、`RenameSession`、`DeleteSession`；`src/MainForm.cs` 搜索标题和近期内容。 | 搜索是本地文本匹配，不是向量或语义检索。 |
| 置顶、归档与分支 | 已实现 | `ConversationStore.SetSessionPinned`、`SetSessionArchivedAndResolveCurrent`；`src/MainForm.cs` 的 `OnBranchSessionClick`。 | 分支复制已有上下文，不建立跨会话实时联动。 |
| 草稿保留 | 已实现 | `src/MainForm.cs` 在当前运行期按会话保存未发送草稿，并实施跨会话二进制草稿预算。 | 关闭应用后的草稿不作为云草稿同步。 |
| 会话备份与合并恢复 | 已实现 | `ConversationStore.ExportBackup`、`ImportBackup` 使用 `.fpc`；备份与 UI smoke tests。 | 备份不含 URL、API Key 或模型配置，恢复由用户选择本地文件发起。 |
| 账号、云同步与跨设备历史 | 有意排除 | 程序没有账号、登录、云端会话或后台同步模块。 | 数据归当前 Windows 用户本机所有；迁移使用显式 `.fpc` 备份。 |
| 团队空间、分享链接与协作编辑 | 有意排除 | 源码和 UI 没有租户、成员、权限或分享服务。 | 不为会话创建公网链接，不进行多人实时协作。 |
| 本地知识库、向量索引与 RAG | 未实现（非 v1.19 承诺） | 当前按轮添加文件并将受预算约束的正文发送给模型。 | 不监控文件夹，不构建 embedding 或持久向量索引。 |

## 本地扩展边界

| 常见能力 | v1.19 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 可启停的本地技能指令 | 已实现 | `src/ExtensionModels.cs` 的 `BuildSystemPrompt`、`src/ExtensionStore.cs` 和 `src/ExtensionsDialog.cs`；支持手工编辑、从剪贴板导入，或由用户显式选择单个不超过 2 MiB 的本地 UTF-8 `SKILL.md`、JSON 或文本文件。 | 技能只是发送给模型的本地指令；导入不扫描目录、不执行脚本或命令，也不会联网。 |
| 用户自备 MCP 服务 | 已实现 | `src/ExtensionsDialog.cs` 可手工配置、粘贴 `mcpServers` JSON，或由用户显式选择单个不超过 2 MiB 的本地 UTF-8 JSON 文件，然后测试连接；导入项默认停用，工具调用默认逐次确认。 | 导入不扫描目录、不执行脚本或联网；MCP 服务权限来自服务自身及当前 Windows 用户，只应启用管理员审查过的服务。 |
| 插件/技能市场与在线安装 | 有意排除 | 扩展页没有商店或下载器；`README.md` 明确其完全在本机工作且不访问扩展商店。 | 不浏览市场、不下载插件、不自动更新技能或 MCP 服务。 |
| 自动扫描技能目录或执行技能脚本 | 有意排除 | 技能仅手工新建、从剪贴板安装，或读取用户显式选择的单个本地文件；`ExtensionStore` 只保存结构化本地配置。 | 不发现或扫描外部目录，不运行其中脚本，不因技能内容或导入操作发起网络请求。 |
| 通用进程内插件 API | 有意排除 | 没有加载任意第三方 DLL 的插件接口；MCP 是独立、显式配置的协议边界。 | 第三方能力应通过用户审查并启用的 MCP 服务提供，而不是注入客户端进程。 |

## 隐私、网络与发布

| 常见能力 | v1.19 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 本地敏感配置保护 | 已实现 | `src/AppSettings.cs`、`src/ModelProfiles.cs` 和 `src/ExtensionStore.cs` 使用当前用户作用域的 Windows DPAPI。 | DPAPI 保护静态配置，不承诺防御已取得当前用户权限的恶意程序。 |
| 最小化外发数据 | 已实现 | 普通模式下模型收到用户输入、历史、启用的技能、附件内容及获准 MCP 结果；附件仅含名称而不含本地路径。代码模式下模型只收到用户要求、会话和内置工具返回的相对路径及必要文件内容，不收到工作区绝对路径，也不加载 MCP。 | 模型端点和普通模式下获准的 MCP 服务仍会收到完成任务所需内容，应由部署方信任并管理。 |
| 模型与模型列表直连 | v1.19 补齐项 | `src/ModelClient.cs` 明确 `UseProxy = false`；`tests/NetworkReliabilitySmokeTest.cs` 使用拒绝型系统代理验证请求仍直达用户端点。HTTP MCP 同样在 `src/McpRuntime.cs` 禁用系统代理。 | 不提供应用内代理配置；需要代理才能访问的服务不属于 v1.19 支持路径。 |
| 无遥测、更新器和隐式辅助网络 | 已实现 | 当前源码没有遥测、崩溃上报、更新检查或辅助服务客户端；安装和启动不下载依赖。 | 生成时连接用户填写的模型 URL；启用 HTTP MCP 时还会连接用户填写的 MCP URL，因此“离线安装”不等于“生成时零网络”。 |
| 离线依赖与 Windows 7 启动 | 已实现 | `build-offline-package.ps1` 打包固定校验的 .NET Framework 4.8 离线安装器和托管 DLL；bootstrapper 只使用离线/缓存信任检查。代码工作区使用 .NET 4.8 与 Win32 原生 API，不引入 Node.js、Python、Git、MCP 或在线安装依赖。 | Windows 7 SP1 仍可能需要管理员预装 SHA-2、服务堆栈、TLS/根证书和字体等系统先决条件；生成修改方案仍需连接用户配置的内网模型 URL。 |
| 包完整性与可验证发布证据 | v1.19 补齐项 | 源码候选提交的结构版本 2 测试收据绑定测试产物，并仅保留在 Git 忽略的 `src/tests/build-artifacts/release`；完整套件安全解压刚构建的 ZIP，核对精确文件集和全部清单哈希。`promote-release-candidate.ps1` 要求源码候选提交已淘汰旧资产，把测试收据绑定的同一字节 ZIP 写入 `exe/FilePromptAI-Win7-Full-v1.19.zip`，复核 `exe` 精确只含该 ZIP并再次运行安装旅程，任何失败都回滚单个 ZIP；安装包晋升提交只改变该路径，ZIP 由仓库根 `.gitattributes` 通过 Git LFS 跟踪。正式封存生成 `src/RELEASE-SHA256.txt` 和 `src/RELEASE-EVIDENCE.txt`，直接绑定测试收据、ZIP 身份与 Win7 报告。 | 安装包晋升提交仍是候选，只有 Win7 PASS 报告实际存在并通过 Win7 验收封存提交及标签后复核才可称正式发布；`exe` 不包含任何摘要副本、说明或其他文件。 |
| 两阶段校验与可恢复卸载 | v1.19 补齐项 | 完整解压后，可运行发布根目录卸载器，或依次选择“设置”→“维护”→“卸载程序...”；根卸载器读取 `PACKAGE-CHECKSUMS-SHA256.txt` 后，在任何删除前锁定并复核全部载荷及清单；用户数据删除同样先锁定和复核整棵目录，任一文件占用、重解析点或身份变化均零删除；自定义测试数据根会在界面与 worker 两层强制保留。提交删除时根卸载器最后处理；极少见的文件系统提交/回滚异常会准确报告部分删除并写入恢复标记，允许再次运行完成清理。 | 必须完整解压 ZIP，不能把卸载器单独复制到完整解压目录之外运行；额外文件不会递归删除，用户数据默认保留。 |
| 目标 Windows 7 一键验收 | v1.19 补齐项 | `acceptance/Program.cs` 要求 `Verify-FilePromptAI.exe --archive <zip>`，同时锁定解压载荷和位于其外部的原始 ZIP并校验精确包集合。启动检查通过真实根启动器拉起独立主进程，验证窗口、进程映像、响应和正常退出；UI 功能旅程在隔离验收进程中加载同一包内程序集，以隔离数据目录和回环端点完成设置、双轮对话、路径文本及已注册 WinForms/OLE 的 FileDrop 处理器所接收的 PNG 图片附件、持久化、13 个生产导出处理器与文件内容和布局检查；schema 3 PASS 才记录验证身份。 | `--archive` 不可省略，ZIP 必须保持规范文件名且位于解压目录外；自动旅程不执行真实 Explorer 鼠标拖动，也不操作系统文件选择器或“另存为”窗口；总 PASS 必须来自 Windows 7 SP1、.NET 4.8、1920x1080、96 DPI，Windows 10/11 报告不能替代。 |
| System-DPI-aware 布局与真实显示门禁 | v1.19 补齐项 | `app.manifest` 声明 system-DPI-aware，主窗口、设置和路径窗口使用 `AutoScaleMode.Dpi`；验收器及 `tests/DisplayEnvironmentProbe.cs` 在 DPI-aware 进程中核对屏幕指标、当前显示模式和 `GetDeviceCaps` 的 96×96 DPI，截图进程也启用 DPI awareness 以避免坐标虚拟化。 | 固定发布门禁只覆盖真实 1920×1080@96 DPI；截图缩放不能作为 125% 实机证据。 |
| 自动更新与后台遥测 | 有意排除 | 发布包没有自动更新器、遥测客户端或后台服务。 | 新版本由管理员离线取得、校验并部署。 |

## v1.19 本次补齐清单

以下项目与既有能力区分列出；它们是否属于正式发布，以最终源码、重新生成的完整 ZIP
和本页开头的发布证据门禁为准：

1. 普通 system prompt、`temperature`、`top_p` 和最大输出 token 已从设置、模型配置接到请求载荷并通过专项回归；是否已成为正式发布内容由本页开头的发布门禁判定。
2. API Key 可留空，匿名模型列表和聊天请求不发送 `Authorization`，同时保存和切换无 Key 模型配置。
3. 模型、模型列表及 HTTP MCP 不使用 Windows 系统代理，并以拒绝型代理回归证明直连行为。
4. 离线技能和 MCP 支持从用户显式选择的单个本地 UTF-8 文件导入；技能接受 `SKILL.md`、JSON 或文本，MCP 接受 JSON，文件上限均为 2 MiB；导入不扫描目录、不执行脚本、不联网，MCP 导入项默认停用。
5. 附件提取依赖按批准的 NPOI 文件名加载，避免通配加载应用目录中的同名前缀 DLL；见 `FileContentExtractor.LoadNpoiAssemblies`。
6. 离线包加入独立 `Verify-FilePromptAI.exe`，锁定并校验载荷后，通过真实根启动器和独立主进程完成启动检查，再由隔离验收进程加载同一包内程序集完成 UI 功能旅程；它输出 schema 3 XML 报告及同名 SHA-256 校验文件，只有 PASS 报告记录有效包清单身份。
7. 发布顺序固定为“源码候选提交 → 安装包晋升提交 → Win7 验收封存提交”：测试收据绑定源码候选提交，且该提交已删除旧版交付资产；测试收据只保留在 Git 忽略的 `src/tests/build-artifacts/release`；晋升前后都复核 `exe` 精确只含 `FilePromptAI-Win7-Full-v1.19.zip`；安装包晋升提交是源码候选提交的直接子提交且只改变这一 ZIP 路径，该 ZIP 由仓库根 `.gitattributes` 通过 Git LFS 跟踪；待取得匹配的 Win7 PASS XML 后，Win7 验收封存提交是安装包晋升提交的直接子提交且只含 `src/RELEASE-SHA256.txt`、`src/RELEASE-EVIDENCE.txt` 两个文件，正式证据直接绑定测试收据、ZIP 身份和 Win7 报告，不依赖 `exe` 中任何附加文件。
8. 完整解压后，可运行根目录卸载器，或依次选择“设置”→“维护”→“卸载程序...”进入；卸载先锁定并验证完整精确文件集合，任何检查失败均在删除前停止；缺清单提示实际检查目录并要求重新完整解压，不能只复制卸载器，发布目录额外文件和默认用户数据保持不变。
9. 流式响应增加自定义网关代表性兼容报文矩阵：支持 event-name-only Responses 文本事件、
   `delta.text`、文本部件数组和 NDJSON；思考、工具参数、输入回显及加密字段不作为正文，
   成功响应体断流不自动重发，带二进制附件的空流也不会降级重传。
10. 新增受限代码工作区：选择已有代码文件后仅授权其固定磁盘父目录，模型只通过相对
    路径使用内置列举、搜索、读取和提交修改工具；写入前显示 Diff 且默认拒绝，以
    SHA-256 防止覆盖并发修改，保留编码和换行，使用 DPAPI 加密备份并支持最近一次撤销。
    代码模式不开放资料附件、MCP、命令执行、创建、删除或重命名能力。

## 显示支持口径

`1920x1080@96 DPI` 是 v1.19 一键验收程序的固定真实环境门禁，不是客户端运行时的
唯一允许分辨率。门禁在 system-DPI-aware 进程中同时核对 Windows 屏幕指标、当前物理
显示模式和 `GetDeviceCaps` 返回的 96×96 DPI；截图工具也使用物理坐标，避免 DPI 虚拟化。
客户端仍有窗口缩放及约 `900x540` 的最小工作区路径。125% 或其他分辨率/缩放比例没有
同等级发布验收证据，不能用按比例预览或截图模拟来代替真实环境测试。

## 明确不在 v1.19 范围

v1.19 有意不加入语音采集/识别/朗读、账号和云同步、跨设备历史、分享及团队协作、
插件或技能市场、在线安装/自动更新、技能目录自动扫描、技能脚本执行、进程内第三方
DLL 插件、遥测和崩溃上报。图像生成、内置网页浏览/搜索、OCR、本地向量知识库、
语义检索和精确 tokenizer 计费属于“未实现（非 v1.19 承诺）”，不能从本矩阵推导为
永久排除或后续版本承诺。
