# AniMeido

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)](https://animeido.com/)
[![License](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE.txt)

[下载](https://animeido.com/) · [开发路线](ROADMAP.md) · [反馈问题](https://github.com/XAN9xXx/AniMeido/issues/new/choose)

## 功能

**今天与计划**：今日放送、未来七天、最近观看，补番计划和提醒。

**放送日历与番剧库**：按星期浏览本季放送，番剧库支持季度、形态筛选和多种排序。

**我的番剧**：追番中、补番中、关注、已看完、弃番，不感兴趣或屏蔽；行内修改和批量操作。

**档案馆**：每部作品的长期笔记（支持 Markdown）、按时间记下的感想、观看时间线、截图和统计，导出年度回顾。

**本地推荐**：根据你的评分、状态和浏览记录在本机计算推荐，可手动纠正。

**搜索**：作品、人物和标签。

**播放插件**：在线播放由独立的 Player 插件提供。

[数据与隐私](docs-public/data-and-privacy.md)。

## 下载

已发布版本请到 [官网](https://animeido.com/) 查看和下载。2.x 提供含运行环境的完整版和精简版。

2.x 版本只在官网发布，GitHub Releases 里只有早期的 1.x 版本。从 3.0 开始，安装包会同时发布到 GitHub Releases。

## 开发路线

源码主线已完成业务核心重构、自有多来源资料服务接入，以及新的安装方式和应用内更新。下一步先验证 Uno，再迁移界面并支持 Android。这些主线能力随 3.0 发布，完整路线见 [ROADMAP](ROADMAP.md)。

路线的一部分依据来自 [2026 年 9 月用户问卷](docs-public/user-feedback-2026-09.md)：呼声最高的是电脑与手机同步。

## 参与

欢迎报告问题、提建议和提交 PR，请先看 [贡献指南](CONTRIBUTING.md)。

### 从源码构建

```powershell
dotnet build AniMeido.slnx
$env:ANIMEIDO_DATA_ROOT = 'D:\AniMeidoDev\sandbox'
dotnet run --project AniMeido.App/AniMeido.App.csproj
```

打包与发布工具见 [Tools](Tools/README.md)。

## 许可证

[GNU Affero General Public License v3.0](LICENSE.txt)。番剧资料与图片版权归各自权利人所有。
