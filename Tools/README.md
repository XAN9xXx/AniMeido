# Tools：插件打包与发布辅助脚本

本目录的脚本都需要 **PowerShell 7（`pwsh`）**。Windows 自带的 Windows PowerShell 5.1 不支持脚本中用到的 .NET API，
脚本开头的 `#Requires -Version 7.0` 会让它在 5.1 下直接拒绝运行，而不是执行到一半出错。

`refactor/` 是本地的历史排查资料，被 `.gitignore` 忽略，不随仓库分发。

| 脚本 | 作用 | 由谁调用 |
| --- | --- | --- |
| `pack-plugin.ps1` | 把插件构建输出打成 `.animeido-plugin` 包（ZIP，内含带 SHA-256 的 `plugin.json` 清单） | Player / AI 项目的 `PackPlugin` 构建目标自动调用，也可手动调用 |
| `prepare-libmpv.ps1` | 下载并校验固定版本的 libmpv，放到 Player 插件的 `runtimes\win-x64\native\` | 维护者手动执行；打包 Player 前执行一次即可 |
| `pack-player-source.ps1` | 把一个播放源目录打成 `.animeido-source` 包 | 手动调用 |

## 打包 Player 插件

1. 首次打包前准备 libmpv（需要网络和 7-Zip；libmpv 不入库，由脚本按固定版本和 SHA-256 下载）：

   ```powershell
   pwsh -File .\Tools\prepare-libmpv.ps1
   ```

2. 构建并打包：

   ```powershell
   dotnet msbuild .\Plugins\AniMeido.Plugin.Player\AniMeido.Plugin.Player.csproj -restore -t:PackPlugin -p:Configuration=Release -p:Platform=x64
   ```

   输出：`artifacts\plugins\AniMeido.Plugin.Player-<版本>.animeido-plugin`。同名文件会先被删除再重新生成。

AI 插件（`AniMeido.Plugin.AI`）是保留的实验源码，默认不启用；需要时把上面命令中的项目路径换成 AI 插件即可，它不依赖 libmpv。

插件包的版本号和最低 App 版本取自插件 csproj 中的 `<Version>` 和 `<PluginMinAppVersion>`。
插件目录下 `plugin.manifest.json` 的 `version`、`minAppVersion` 必须与之一致，否则 `pack-plugin.ps1` 会拒绝打包；升级插件版本时两处一起改。

## 手动调用 pack-plugin.ps1

适用于不经过 MSBuild、直接打包一个已准备好的插件目录：

```powershell
.\Tools\pack-plugin.ps1 `
  -PluginDir .\artifacts\PlayerPlugin `
  -PluginId AniMeido.Plugin.Player `
  -DisplayName 在线播放器 `
  -Version 0.4.0 `
  -MinAppVersion 1.4.0 `
  -EntryAssembly AniMeido.Plugin.Player.dll `
  -ManifestTemplatePath .\Plugins\AniMeido.Plugin.Player\plugin.manifest.json `
  -OutputPath .\artifacts\AniMeido.Plugin.Player-0.4.0.animeido-plugin
```

示例中的版本号仅作说明，不是当前发布版本。

## 打包播放源

```powershell
.\Tools\pack-player-source.ps1 `
  -SourceDir .\MySource `
  -SourceId example.source `
  -DisplayName 'Example source' `
  -Version 1.0.0 `
  -EntryFile example.animeido-source.json `
  -OutputPath .\artifacts\sources\example.source-1.0.0.animeido-source
```

## 测试覆盖率（无通过门槛）

已有依赖还原完成后，在仓库根目录执行：

```powershell
pwsh -File .\Tools\test-coverage.ps1
```

脚本通过现有 Microsoft.NET.Test.Sdk 17.8.0 的 Code Coverage 采集器和根目录 `coverage.runsettings`，
分别构建并运行两个测试项目（`--no-restore`，不联网、不安装包或工具）。输出位于已忽略的
`TestResults/coverage/<时间戳-GUID>/<测试项目>/`，打印各产品程序集的 Cobertura 行/分支覆盖率。
两个项目不合并；未采集到的程序集明确列为 `not collected`，不当作 0%。
PluginHost 为单独进程，单元测试不运行它，因而本配置不采集它。测试或采集/报告失败返回非零退出码。

## 安装与安全说明

插件包在 AniMeido 的"应用设置"页安装；安装或更新后，在设置中重新加载可选插件宿主即可生效，不需要重启主程序。

AniMeido 插件与应用本身拥有相同的本地权限。插件包没有发布者签名，只应安装来源可信的插件。
清单中的哈希只能发现打包后或安装后的意外损坏，不能证明插件由谁制作。
