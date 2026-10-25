# 代理说明

AniMeido 是本地优先的个人追番管理应用：Windows x64 桌面程序，.NET 10、C# 14、WinUI 3，SDK 版本由 `global.json` 固定。业务集中在必需的 Base 插件里；播放功能是可选插件，在独立进程中运行。界面目前只有中文。

## 构建系统

使用 .NET SDK（`dotnet` CLI）。关键文件：
- `AniMeido.slnx` — 完整解决方案
- `global.json` — 固定 .NET SDK 版本
- `Directory.Build.props` — 所有项目共用的可空性、分析器、x64 平台和产品版本 `AniMeidoVersion`
- `Directory.Packages.props` — 中央包管理：NuGet 版本只写在这里，csproj 里的 `PackageReference` 不写版本

只能在 Windows 上构建和测试：WinUI 项目和 `AniMeido.Tests` 的目标框架都是 Windows 专用的。

## 常用命令

```powershell
# 构建
dotnet build AniMeido.slnx

# 运行全部测试
dotnet test AniMeido.slnx

# 运行某个测试项目
dotnet test AniMeido.PluginProtocol.Tests

# 按名称运行单个测试
dotnet test AniMeido.Tests --filter "FullyQualifiedName~PendingReview"

# 用独立数据目录启动应用（必须是绝对路径）
$env:ANIMEIDO_DATA_ROOT = 'C:\AniMeidoDev\sandbox'
dotnet run --project AniMeido.App/AniMeido.App.csproj
```

### 完成前

下面这些都通过，改动才算完成：

```powershell
dotnet build AniMeido.slnx   # 不能有新警告
dotnet test AniMeido.slnx
```

手动试改动时一定设置 `ANIMEIDO_DATA_ROOT`。不设置时，应用直接使用真实的 `%APPDATA%\AniMeido`，启动时还会就地升级数据库结构，可能改掉你自己的追番记录。

CI（`.github/workflows/ci.yml`）在 `windows-latest` 上做 Release 构建并运行全部测试。

## 边界

### 不要改

| 内容 | 原因与做法 |
|------|------------|
| `DatabaseSchema` 里已有的迁移步骤 | 已经升级过的数据库不会再执行它们。结构变化一律新增一个版本号的迁移 |
| `AniMeido.Updates/UpdateTrustedKeys.cs` 里的公钥 | 与发布用的签名私钥配对，改动会导致更新清单验签失败。由维护者更换 |

### 不要提交密钥和个人数据

不提交令牌、API key 和私钥（包括更新签名私钥），也不提交数据库、日志、截图这类个人数据。

### 先问

- 新功能或跨模块的改动：先开 Issue 讨论范围
- 修改 Contracts、插件协议或数据库结构
- 新增 NuGet 依赖
- 修改 `.github/workflows/` 下的文件

## 仓库结构

```
AniMeido.App/                    # WinUI 外壳：窗口、导航、根 DI、数据库生命周期、插件监督
AniMeido.Contracts/              # 跨边界的共享模型与能力契约（纯 net10.0）
AniMeido.PluginProtocol/         # 插件清单与 JSON 管道 RPC 协议
AniMeido.PluginHost/             # 可选插件的独立宿主进程
AniMeido.Updates/                # 应用更新核心：验签、下载、安装状态与回滚（纯 net10.0）
AniMeido.Launcher/               # Native AOT 启动器：选择版本、失败回退
Plugins/AniMeido.Plugin.Base/    # 必需插件：番剧资料、追番、计划、档案、推荐、个人数据库
Plugins/AniMeido.Plugin.Player/  # 可选的播放插件
Plugins/AniMeido.Plugin.AI/      # 实验源码，未启用
Shared/                          # 多个项目链接的共享源码，如数据目录 AppDataRoots
AniMeido.Tests/                  # 应用与插件测试（Windows 专用）
AniMeido.PluginProtocol.Tests/   # 插件协议测试
AniMeido.Updates.Tests/          # 更新核心测试
Installer/                       # Inno Setup 安装脚本
Tools/                           # 更新签名工具，打包、发布与开发安装脚本
docs-public/                     # 公开文档，如数据与隐私说明
```

## 关键约定

- 业务功能写在 Base 插件里；App 只负责窗口、导航和设置这类外壳功能。
- App 只引用 Base。可选插件在 PluginHost 进程里运行、经插件协议通信，App 不直接引用它们；宿主提供给插件的接口保持窄，不暴露具体服务或 SQLite 连接。
- Contracts 只放跨边界的稳定契约；只在一个插件里用到的接口和服务留在插件内。
- 基础功能不能依赖 Player；不要启用 AI 插件。
- 修改应用访问的网络地址或上传的内容时，同步更新 [数据与隐私](docs-public/data-and-privacy.md)。

## 测试约定

- 框架：**xUnit**
- 测试名描述行为，例如 `Import_KeepsLocalNotes_WhenRemoteEntryExists`
- 修缺陷先写一个能复现问题、会失败的测试，再修复
- 依赖实现细节的测试标 `[Trait("Category", "Mechanism")]`
- 只用临时目录和合成数据，不读写真实的用户数据目录和 Registry

## Git 工作流

- 从 `main` 拉分支。
- 提交消息用约定式提交，摘要写中文，scope 用模块名：

```
feat(base): 实现多平台综合评分与统一排序展示
fix(player): 停用来源后再标记脱离订阅
perf(tags): 整次标签查询只排序一次并批量补全资料
```

- 一个提交只做一件事：行为修复、结构调整、性能优化和删除清理分开提交。
- PR 描述写清改了什么、为什么改、实际测试了哪些场景；涉及插件协议、数据库结构或 Contracts 时，说明兼容性和数据迁移的影响。

## 代码风格

`.editorconfig` 决定格式（UTF-8 BOM、CRLF、4 空格）和分析器级别。保持零警告，不为消除警告而放宽规则。改文件时保留原有的编码和换行；Git Bash 的 `sed -i` 会把 CRLF 改成 LF。

### 注释写原因，不写做什么

代码已经说明的事不重复，不写修改历史，注释掉的代码直接删除。公开契约（Contracts、PluginProtocol）写 XML 文档注释。

重复代码：

```csharp
// 如果缓存过期就重新请求
if (cache.IsExpired)
```

说明代码里看不出来的原因：

```csharp
// 404 不缓存七天，避免刚公布的作品长期不可见。
if (data is not MetadataDetail { Data: null })
```

（第二个例子摘自 `MetadataDataSource` 的缓存代码。）

### 日志按操作结果分级

| 级别 | 用于 |
|------|------|
| `Warning` | 出了异常或降级，但流程能继续，例如改用缓存展示 |
| `Error` | 这次操作失败且没有恢复，例如保存、导入、插件启动没完成 |
| `Critical` | 应用无法继续运行 |
| 不记 | 用户正常取消、预期内的输入校验失败 |

只在负责最终处理的边界记录异常，同一个异常只记一次。

### 界面文案

面向用户的文字要说清发生了什么、数据是否安全、可以怎么做，不出现内部术语（资料服务、公共 ID、表名、原因代码等）。
