# AniMeido

**本地优先的个人追番管理应用。** 看今天更新什么、记下看到哪、给看过的作品留下评分、笔记、感想和截图。

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)](https://animeido.com/)
[![License](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE.txt)

[下载](https://animeido.com/) · [开发路线](ROADMAP.md) · [反馈问题](https://github.com/XAN9xXx/AniMeido/issues/new/choose) · [English](#english)

## 功能

**今天与计划**：今日放送、未来七天、最近观看，以及你自己排的补番计划和提醒，打开应用第一眼就知道今天该看什么。

**放送日历与番剧库**：按星期浏览本季放送，用"时光机"回到任意往季；番剧库支持季度、形态筛选和多种排序。

**我的番剧**：追番中、补番中、关注、已看完、弃番，不想再看到的作品可以标记为不感兴趣或屏蔽；支持行内修改和批量操作。

**档案馆**：每部作品的长期笔记（支持 Markdown）、按时间记下的感想、观看时间线、截图和统计，还能导出年度回顾。

**本地推荐**：根据你的评分、状态和浏览记录在本机计算推荐，每一条都说明推荐理由，不准的话可以手动纠正。

**搜索**：作品、人物和标签，优先显示本地匹配结果。

**可选播放插件**：在线播放由独立的 Player 插件提供，不装也不影响任何管理功能。

## 你的数据留在你的电脑上

- 个人记录全部保存在本地 SQLite 数据库，没有账户，也没有使用统计上报。
- 支持数据库备份与恢复、JSON 导入导出，以及包含截图原图的完整档案包。
- 番剧资料来自 [Bangumi 番组计划](https://bgm.tv/)，通过 AniMeido 的资料镜像和代理获取。

详见 [数据与隐私](docs-public/data-and-privacy.md)。

## 下载

当前版本为 2.1.1，请到 [官网](https://animeido.com/) 下载，有含运行环境的完整版和精简版两种。

2.x 版本只在官网发布，GitHub Releases 里只有早期的 1.x 版本。从 3.0 开始，安装包会同时发布到 GitHub Releases。

## 开发路线

近期在做三件事：新的安装方式与应用内一键更新、自有的多来源番剧资料服务，以及迁移到 Uno 平台以支持 Android。完整内容见 [ROADMAP](ROADMAP.md)。

路线的一部分依据来自 [2026 年 9 月用户问卷](docs-public/user-feedback-2026-09.md)：呼声最高的是电脑与手机同步。

## 参与

欢迎报告问题、提建议和提交 PR，请先看 [贡献指南](CONTRIBUTING.md)。

### 从源码构建

需要 Windows x64、支持 WinUI 3 开发的 Visual Studio，以及 `global.json` 指定的 .NET SDK。

```powershell
dotnet build AniMeido.slnx
dotnet run --project AniMeido.App/AniMeido.App.csproj
```

打包与发布工具见 [Tools](Tools/README.md)。

## English

AniMeido is a local-first anime tracker for Windows. It helps you follow the current season, plan what to watch, and keep a personal archive of ratings, notes, impressions and screenshots.

- **Today & plans**: today's airings, the next seven days, recently watched titles and your own watch plans with reminders.
- **Calendar & library**: weekly schedule for the current season, a "time machine" for past seasons, and a filterable library.
- **Archive**: Markdown notes, timestamped impressions, watch timeline, screenshots, statistics and a yearly review export.
- **Local recommendations**: computed on your machine from your own ratings and activity, with an explanation for every pick and manual corrections.
- **Privacy**: no account and no telemetry. Personal data stays in a local SQLite database with backup, JSON export and full archive packages.
- **Optional player plugin**: online playback lives in a separate plugin that runs in its own process.

Anime metadata comes from [Bangumi](https://bgm.tv/). The UI is currently Chinese only.

Built with C# / .NET 10 / WinUI 3, with an out-of-process plugin host and a versioned JSON-RPC plugin protocol. Next up: a signed in-app update system, a self-hosted multi-source metadata service, and a migration to [Uno Platform](https://platform.uno/) for Android, with macOS and Linux to follow.

Download from [animeido.com](https://animeido.com/). Bug reports and pull requests are welcome.

## 许可证

[GNU Affero General Public License v3.0](LICENSE.txt)。番剧资料与图片版权归各自权利人所有。
