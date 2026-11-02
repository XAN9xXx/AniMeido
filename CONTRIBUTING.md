# 贡献指南

感谢你愿意帮助 AniMeido。报告问题、提建议、改文档和提交代码都欢迎。

## 报告问题

请用 [问题模板](https://github.com/XAN9xXx/AniMeido/issues/new/choose) 提交，并写清楚以下几点：

- 应用版本（在设置页可以看到）和 Windows 版本
- 是否安装了 Player 插件
- 操作步骤、你期望的结果和实际看到的结果

日志位置可以在设置的"数据管理"页查看。附截图或日志前，请先遮掉账户、路径里的用户名等个人信息。

## 提建议

请先说明使用场景：你想做什么，现在哪一步不方便。比起直接给出方案，这些信息更有助于找到合适的做法。提之前可以先看看 [开发路线](ROADMAP.md)，也许已经在计划中了。

## 提交代码

1. 小修复可以直接提 PR。新功能或跨模块的改动，请先开 Issue 讨论范围。
2. 业务逻辑与存储写在 `Plugins/AniMeido.Plugin.Base.Core`；WinUI 页面、ViewModel 和平台接线写在 `Plugins/AniMeido.Plugin.Base`。App 负责窗口、导航、应用设置和插件宿主。
3. 如果改动涉及插件协议、数据库结构或 Contracts，请在 PR 里说明兼容性和数据迁移的影响。
4. PR 描述写清楚：改了什么、为什么改、你实际测试了哪些场景。

### 构建与测试

```powershell
dotnet build AniMeido.slnx
dotnet test AniMeido.Plugin.Base.Core.Tests
dotnet test AniMeido.PluginProtocol.Tests
dotnet test AniMeido.Updates.Tests
dotnet test AniMeido.Tests
```

完整解决方案构建和 `AniMeido.Tests` 需要 Windows。业务核心 `AniMeido.Plugin.Base.Core` 及其测试使用纯 `net10.0`，可以在 Linux 上单独构建和测试。

需要运行应用时，先设置独立数据目录：

```powershell
$env:ANIMEIDO_DATA_ROOT = 'D:\AniMeidoDev\sandbox'
dotnet run --project AniMeido.App/AniMeido.App.csproj
```

## 许可

提交的贡献按本仓库的 [AGPL-3.0](LICENSE.txt) 许可发布。
