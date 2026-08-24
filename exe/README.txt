FilePrompt AI for Windows 7 - v1.17
===================================

此目录只保存最终离线安装包及其 SHA-256 校验文件。

文件：
- FilePromptAI-Win7-Full-v1.17.zip
- FilePromptAI-Win7-Full-v1.17.zip.sha256.txt

安装：
1. 使用 certutil 校验 ZIP：
   certutil -hashfile FilePromptAI-Win7-Full-v1.17.zip SHA256
2. 结果必须为：
   070B8BBFD9377B2C8E85B0B2DF4BDDCD17A1865683217BE67F1B594587DCF424
3. 完整解压 ZIP，不要直接在压缩包内运行。
4. 双击 Start-FilePromptAI.exe。
5. 若缺少 .NET Framework 4.8，启动器会使用包内微软官方完整离线安装程序，
   不会在线下载依赖。

卸载：
- 运行 Uninstall-FilePromptAI.exe；或在程序“设置 -> 维护”中选择卸载。

说明：
- 包内主程序、启动器、卸载器和验收器版本均为 1.17.0.0。
- Windows Defender 签名库 1.457.310.0 扫描相关检出为 0。
- 当前包是 v1.17 候选交付包；正式发布仍需在真实 Windows 7 SP1、
  .NET Framework 4.8、1920x1080、96 DPI 环境完成验收。
