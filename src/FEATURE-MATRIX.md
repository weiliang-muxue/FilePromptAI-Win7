# FilePrompt AI v1.17 功能矩阵

本文档按常见桌面 AI 客户端能力核对 FilePrompt AI v1.17 候选源码。它描述产品能力和
明确边界，不替代测试报告、目标 Windows 7 验收报告或发布包 SHA-256 证明。

## 状态说明

| 状态 | 含义 |
| --- | --- |
| 已实现 | 当前 v1.17 候选源码已有完整用户路径及对应实现或自动测试证据；仍须随最终发布候选执行回归。 |
| v1.17 本次补齐，待最终测试 | 本轮为 v1.17 增加或加固，只有最终源码、离线包、自动回归和目标机验收全部一致后才能视为发布完成。 |
| 未实现（非 v1.17 承诺） | 常见客户端可能提供，但本版本没有相应用户路径；这不自动表示永久排除，也不是 v1.17 发布承诺。 |
| 有意排除 | 与本项目的 Windows 7、内网、自带依赖、用户主动连接边界不符，本版本明确不提供。 |

表中“证据”引用源码文件和稳定的类型或方法名，避免行号随候选版继续调整而失效。

## 核心能力

| 常见能力 | v1.17 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 自定义模型端点 | 已实现 | `src/SettingsDialog.cs` 的模型连接页；`src/ModelClient.cs` 的 `GenerateAsync` 按用户填写的完整 URL 发送 OpenAI Chat Completions 请求。 | 不自动补全聊天路径，不原生适配 Responses、Anthropic 等其他协议；服务可通过兼容层接入。 |
| 模型发现与手工选择 | 已实现 | `src/ModelClient.cs` 的 `FetchModelsAsync` 读取同源 `/models`；模型输入仍可编辑；`tests/NetworkReliabilitySmokeTest.cs` 覆盖端点推导、响应校验和重定向拒绝。 | 模型列表是用户主动请求，不做后台发现。 |
| 有鉴权及无鉴权连接 | v1.17 本次补齐，待最终测试 | `src/MainForm.cs`、`src/SettingsDialog.cs` 和 `src/ModelProfiles.cs` 允许 API Key 留空；`src/ModelClient.cs` 仅在 Key 非空时发送 `Authorization`；网络及模型配置回归覆盖匿名请求。 | 适用于 Ollama、vLLM 等无需鉴权的 OpenAI 兼容端点；不代替服务端访问控制。 |
| 多模型配置保存与快速切换 | 已实现 | `src/ModelProfiles.cs` 的 `ModelProfileStore`、`src/ModelProfilesDialog.cs` 以及 `src/MainForm.cs` 的快速模型菜单。 | 配置保存在当前 Windows 用户本机，不云同步；跨用户不可解密的配置会被忽略。 |
| 普通用户可编辑的 system prompt | v1.17 本次补齐，待最终测试 | `src/SettingsDialog.cs` 提供编辑页；`AppSettings.SystemPrompt` 与 `ModelProfile.SystemPrompt` 持久化；`MainForm.BuildCombinedSystemPrompt` 把自定义提示放在技能指令之前；`ModelClient.BuildInitialMessages` 将合并结果作为首条 `system` 消息；生成设置测试覆盖合并顺序和预算。 | 最多输入 16,000 字符，并与技能及会话共同服从 48,000 字符预算。 |
| `temperature`、`top_p`、最大输出 token | v1.17 本次补齐，待最终测试 | 设置页和模型配置均提供独立启用开关；`AppSettings`、`ModelProfile`、`ModelRequest` 使用可空值；`MainForm.GenerateAsync` 传入请求，`ModelClient.AddGenerationOptions` 映射为 `temperature`、`top_p`、`max_tokens` 并校验范围；生成设置测试覆盖发送/省略、范围拒绝和持久化往返。 | 未启用时不写入载荷，由服务采用默认值。传输响应大小限制不是生成 token 上限。 |
| 流式输出与停止 | 已实现 | `src/ModelClient.cs` 的流式响应处理、`src/MainForm.cs` 的 `GenerateAsync` 和 `CancellationTokenSource`；网络回归覆盖空闲超时、不完整流和取消优先级。 | 服务不支持流式时可回退到非流式；停止只取消当前请求。 |
| 失败重试与回复重新生成 | 已实现 | `src/MainForm.cs` 的 `RetryLastFailedGeneration`、`StartRegeneration` 和原位替换逻辑；`tests/UiStateSmokeTest.cs` 覆盖成功、失败、取消及状态失效。 | 仅最新完整问答可原位重新生成；一次性二进制附件不会从旧本地路径静默重读。 |
| 多轮上下文与预算控制 | 已实现 | `src/ConversationContextBudget.cs` 按完整问答轮次裁剪；`src/MainForm.cs` 将历史、当前文字、技能和附件正文纳入 48,000 字符预算。 | 这是字符预算，不是特定模型 tokenizer 的精确 token 计数。 |
| MCP 工具调用 | 已实现 | `src/McpRuntime.cs` 支持 stdio 与 Streamable HTTP；`src/ModelClient.cs` 支持 `tools`、`tool_calls` 和最多 8 轮工具循环；相关 MCP 与工具循环 smoke tests。 | MCP 仅在用户配置并启用后连接；stdio 运行环境不随客户端安装。它不是通用插件市场。 |

## 输入、阅读与输出

| 常见能力 | v1.17 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 文本、代码和 Office/PDF/XMind 附件问答 | 已实现 | `src/FileContentExtractor.cs` 的 `ExtractFile` 支持普通文本/代码、PDF、DOC/DOCX、RTF、XLS/XLSX、PPTX，以及新版 `content.json` 和旧版 `content.xml` XMind；PPTX 提取页标题、正文、表格和备注，XMind 保留多画布、主题层级和备注；提取与加固 smoke tests 覆盖正常及恶意压缩包。 | 仅处理用户主动添加的文件，不扫描目录；不把本地路径发送给模型。 |
| 图片输入与视觉模型载荷 | 已实现 | `src/FileContentExtractor.cs` 压缩 PNG/JPEG/BMP/GIF/TIFF；`src/ModelClient.cs` 构造 `text` 与 `image_url` 多模态内容。 | 依赖所选模型支持兼容的视觉输入；不包含 OCR 引擎或本地图像识别。 |
| 拖放、文件选择、路径读取和剪贴板 | 已实现 | `src/MainForm.cs` 的文件添加、专用拖放区、`OnReadPathClick` 和 `OnPasteClick`；UI 状态测试覆盖拖放句柄和忙碌状态。 | 粘贴路径本身不会触发后台读取，必须由用户执行读取动作。 |
| 附件预览与生命周期保护 | 已实现 | `src/InputPreviewDialog.cs` 预览已读入内容；`src/MainForm.cs` 实施草稿二进制内存预算和一次性二进制规则。 | 文本正文可进入历史；图片和无文本二进制只发送当前轮，后续需要重新添加。 |
| Markdown 阅读 | 已实现 | `src/MarkdownRichTextRenderer.cs` 和 `src/MarkdownDocument.cs` 支持标题、列表、代码块、引用和表格排版；`tests/MarkdownRendererSmokeTest.cs` 验证渲染。 | 代码块没有逐块复制按钮或语法高亮。 |
| 复制与 Markdown/纯文本保存 | 已实现 | `src/MainForm.cs` 的 `OnCopyOutputClick`、`ExportText`、`BuildConversationMarkdown` 和 `BuildConversationPlainText`。 | 可保存最新回复或整个会话；复制对象仍是当前最新回复，不是任意富文本片段管理器。 |
| 文档、演示、表格和思维导图导出 | 已实现 | `DocxExporter`、`PdfExporter`、`PptxExporter`、`XlsxExporter`、`CsvExporter`、`XMindExporter`，由 `src/MainForm.cs` 的导出菜单调用；各导出 smoke tests。 | 全部本地生成；PDF 中文输出依赖系统存在可嵌入的 CJK 字体。 |
| 语音输入、朗读和实时语音对话 | 有意排除 | 源码和 UI 没有麦克风、STT、TTS 或音频会话路径。 | v1.17 不请求麦克风权限、不采集音频，也不随包提供语音模型。 |
| 内置图像生成或编辑 | 未实现（非 v1.17 承诺） | 当前图片只作为用户附件发送给兼容视觉模型。 | 没有图像生成 API、画布或本地图像生成模型。 |

## 会话与数据

| 常见能力 | v1.17 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 本地持久会话 | 已实现 | `src/ConversationStore.cs` 的原子 XML 存储、完整问答提交、损坏文件保留和临时占用只读保护；会话存储、备份及 UI smoke tests。 | 数据固定在当前用户的 `%LocalAppData%\FilePromptAI-Win7`；暂时被占用或无权限的有效文件不会被移动或覆盖。 |
| 会话新建、重命名、删除和搜索 | 已实现 | `ConversationStore.CreateSession`、`RenameSession`、`DeleteSession`；`src/MainForm.cs` 搜索标题和近期内容。 | 搜索是本地文本匹配，不是向量或语义检索。 |
| 置顶、归档与分支 | 已实现 | `ConversationStore.SetSessionPinned`、`SetSessionArchivedAndResolveCurrent`；`src/MainForm.cs` 的 `OnBranchSessionClick`。 | 分支复制已有上下文，不建立跨会话实时联动。 |
| 草稿保留 | 已实现 | `src/MainForm.cs` 在当前运行期按会话保存未发送草稿，并实施跨会话二进制草稿预算。 | 关闭应用后的草稿不作为云草稿同步。 |
| 会话备份与合并恢复 | 已实现 | `ConversationStore.ExportBackup`、`ImportBackup` 使用 `.fpc`；备份与 UI smoke tests。 | 备份不含 URL、API Key 或模型配置，恢复由用户选择本地文件发起。 |
| 账号、云同步与跨设备历史 | 有意排除 | 程序没有账号、登录、云端会话或后台同步模块。 | 数据归当前 Windows 用户本机所有；迁移使用显式 `.fpc` 备份。 |
| 团队空间、分享链接与协作编辑 | 有意排除 | 源码和 UI 没有租户、成员、权限或分享服务。 | 不为会话创建公网链接，不进行多人实时协作。 |
| 本地知识库、向量索引与 RAG | 未实现（非 v1.17 承诺） | 当前按轮添加文件并将受预算约束的正文发送给模型。 | 不监控文件夹，不构建 embedding 或持久向量索引。 |

## 本地扩展边界

| 常见能力 | v1.17 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 可启停的本地技能指令 | 已实现 | `src/ExtensionModels.cs` 的 `BuildSystemPrompt`、`src/ExtensionStore.cs` 和 `src/ExtensionsDialog.cs`；支持手工编辑、从剪贴板导入，或由用户显式选择单个不超过 2 MiB 的本地 UTF-8 `SKILL.md`、JSON 或文本文件。 | 技能只是发送给模型的本地指令；导入不扫描目录、不执行脚本或命令，也不会联网。 |
| 用户自备 MCP 服务 | 已实现 | `src/ExtensionsDialog.cs` 可手工配置、粘贴 `mcpServers` JSON，或由用户显式选择单个不超过 2 MiB 的本地 UTF-8 JSON 文件，然后测试连接；导入项默认停用，工具调用默认逐次确认。 | 导入不扫描目录、不执行脚本或联网；MCP 服务权限来自服务自身及当前 Windows 用户，只应启用管理员审查过的服务。 |
| 插件/技能市场与在线安装 | 有意排除 | 扩展页没有商店或下载器；`README.md` 明确其完全在本机工作且不访问扩展商店。 | 不浏览市场、不下载插件、不自动更新技能或 MCP 服务。 |
| 自动扫描技能目录或执行技能脚本 | 有意排除 | 技能仅手工新建、从剪贴板安装，或读取用户显式选择的单个本地文件；`ExtensionStore` 只保存结构化本地配置。 | 不发现或扫描外部目录，不运行其中脚本，不因技能内容或导入操作发起网络请求。 |
| 通用进程内插件 API | 有意排除 | 没有加载任意第三方 DLL 的插件接口；MCP 是独立、显式配置的协议边界。 | 第三方能力应通过用户审查并启用的 MCP 服务提供，而不是注入客户端进程。 |

## 隐私、网络与发布

| 常见能力 | v1.17 状态 | 当前实现与证据 | 明确边界 |
| --- | --- | --- | --- |
| 本地敏感配置保护 | 已实现 | `src/AppSettings.cs`、`src/ModelProfiles.cs` 和 `src/ExtensionStore.cs` 使用当前用户作用域的 Windows DPAPI。 | DPAPI 保护静态配置，不承诺防御已取得当前用户权限的恶意程序。 |
| 最小化外发数据 | 已实现 | 模型收到用户输入、历史、启用的技能、附件内容及获准 MCP 结果；附件仅含名称而不含本地路径；MCP 连接配置不会作为提示发送。 | 模型端点和获准 MCP 服务仍会收到完成任务所需内容，应由部署方信任并管理。 |
| 模型与模型列表直连 | v1.17 本次补齐，待最终测试 | `src/ModelClient.cs` 明确 `UseProxy = false`；`tests/NetworkReliabilitySmokeTest.cs` 使用拒绝型系统代理验证请求仍直达用户端点。HTTP MCP 同样在 `src/McpRuntime.cs` 禁用系统代理。 | 不提供应用内代理配置；需要代理才能访问的服务不属于 v1.17 支持路径。 |
| 无遥测、更新器和隐式辅助网络 | 已实现 | 当前源码没有遥测、崩溃上报、更新检查或辅助服务客户端；安装和启动不下载依赖。 | 生成时连接用户填写的模型 URL；启用 HTTP MCP 时还会连接用户填写的 MCP URL，因此“离线安装”不等于“生成时零网络”。 |
| 离线依赖与 Windows 7 启动 | 已实现 | `build-offline-package.ps1` 打包固定校验的 .NET Framework 4.8 离线安装器和托管 DLL；bootstrapper 只使用离线/缓存信任检查。 | Windows 7 SP1 仍可能需要管理员预装 SHA-2、服务堆栈、TLS/根证书和字体等系统先决条件。 |
| 包完整性与可验证发布证据 | v1.17 本次补齐，待最终测试 | 包内精确 `PACKAGE-CHECKSUMS-SHA256.txt`、schema 2 测试 receipt、强制 `AcceptanceReportPath` 的 `seal-release.ps1`、外部 `RELEASE-SHA256.txt` 和 `tests/VerifyTaggedRelease.ps1`；receipt、Win7 PASS XML 与 ZIP 以包清单原始字节 SHA-256 和条目数绑定，最终再由 annotated `v1.17` 标签固定。 | 候选源码或 ZIP 的存在不等于已封存发布；报告 sidecar 提供传输完整性，不是数字签名；包内清单也不单独证明构建可复现。 |
| 目标 Windows 7 一键验收 | v1.17 本次补齐，待最终测试 | `acceptance/Program.cs` 校验精确包集合，使用隔离数据目录启动真实客户端，并在回环端点测试匿名模型发现、流式聊天、文件读取和导出；schema 2 PASS 报告记录锁定并验证的包清单身份，FAIL 报告不携带有效身份。 | 总 PASS 必须来自 Windows 7 SP1、.NET 4.8、1920x1080、96 DPI 的目标机；Windows 10/11 报告不能替代，并且封存时必须同时提供 XML 与 sidecar。 |
| 自动更新与后台遥测 | 有意排除 | 发布包没有自动更新器、遥测客户端或后台服务。 | 新版本由管理员离线取得、校验并部署。 |

## v1.17 本次补齐清单

以下项目与 v1.16 已有能力区分列出，发布前均需以最终源码和重新生成的完整 ZIP 为准：

1. 普通 system prompt、`temperature`、`top_p` 和最大输出 token 已从设置、模型配置接到请求载荷并通过专项回归；仍须随最终候选重建离线包及执行发布验证。
2. API Key 可留空，匿名模型列表和聊天请求不发送 `Authorization`，同时保存和切换无 Key 模型配置。
3. 模型、模型列表及 HTTP MCP 不使用 Windows 系统代理，并以拒绝型代理回归证明直连行为。
4. 离线技能和 MCP 支持从用户显式选择的单个本地 UTF-8 文件导入；技能接受 `SKILL.md`、JSON 或文本，MCP 接受 JSON，文件上限均为 2 MiB；导入不扫描目录、不执行脚本、不联网，MCP 导入项默认停用。
5. 附件提取依赖按批准的 NPOI 文件名加载，避免通配加载应用目录中的同名前缀 DLL；见 `FileContentExtractor.LoadNpoiAssemblies`。
6. 离线包加入独立 `Verify-FilePromptAI.exe`，锁定并校验已验证载荷后再启动或反射加载真实客户端，输出 schema 2 XML 报告及 SHA-256 sidecar；只有 PASS 报告记录有效包清单身份。
7. 完整测试 receipt、真实 Win7 PASS 报告、固定发布摘要、annotated 标签及标签后复核流程以同一包清单身份串联，明确区分便利 sidecar、包内清单与 Git 标签中的外部身份锚点。
8. 卸载及包验证继续按精确文件集合工作，保护发布目录中的额外文件和默认保留的用户数据。

## 显示支持口径

`1920x1080@96 DPI` 是 v1.17 一键验收程序的固定发布测试条件，不是客户端运行时的
唯一允许分辨率。客户端仍有窗口缩放及约 `900x540` 的最小工作区路径；其他分辨率或
缩放比例不应被表述为已完成同等级发布验收，除非另有对应测试证据。

## 明确不在 v1.17 范围

v1.17 有意不加入语音采集/识别/朗读、账号和云同步、跨设备历史、分享及团队协作、
插件或技能市场、在线安装/自动更新、技能目录自动扫描、技能脚本执行、进程内第三方
DLL 插件、遥测和崩溃上报。图像生成、内置网页浏览/搜索、OCR、本地向量知识库、
语义检索和精确 tokenizer 计费属于“未实现（非 v1.17 承诺）”，不能从本矩阵推导为
永久排除或后续版本承诺。
