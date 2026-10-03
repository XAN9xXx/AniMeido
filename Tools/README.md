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

## 应用更新清单与离线签名（AniMeido.UpdateSigner）

工具只处理本地文件，**不会上传任何文件，也不会发出 HTTP 请求**。私钥必须离线保存并备份，
**不进仓库、不放 CI**；CI 只分发已经签名的清单。签名为 ECDSA P-256 / SHA-256，
对 release.json 原始字节签名，使用固定 64 字节 P1363 格式；修改空白或换行也必须重新签名。

以下命令在仓库根目录执行（示例版本和地址不代表当前发布）：

```powershell
# 生成 PKCS#8 PEM 私钥与单行 SPKI Base64 公钥；任一目标已存在就拒绝
dotnet run --project .\Tools\AniMeido.UpdateSigner -- keygen --private E:\OfflineKeys\update.pem --public .\update-public.txt

# 根据完整 ZIP 生成清单；可重复 --url 添加镜像
dotnet run --project .\Tools\AniMeido.UpdateSigner -- manifest --zip .\AniMeido-2.3.0-win-x64.zip --version 2.3.0 --channel stable --url https://dl.animeido.com/updates/win-x64/stable/AniMeido-2.3.0-win-x64.zip --release-date 2026-11-01 --min-updatable-from 2.2.0 --notes-file .\release-notes.txt --out .\release.json

# 输出 release.json.sig；默认拒绝覆盖，重新签名时须明确加 --overwrite
dotnet run --project .\Tools\AniMeido.UpdateSigner -- sign --private E:\OfflineKeys\update.pem --file .\release.json

# 验签后再校验 JSON，输出版本与通道；--public 也可直接传 SPKI Base64
dotnet run --project .\Tools\AniMeido.UpdateSigner -- verify --public .\update-public.txt --file .\release.json --channel stable
```

省略 --min-updatable-from 时使用 0.0.0，省略 --notes-file 时说明为空；
verify 省略 --channel 时按 stable 校验，预览版必须传 --channel preview。
manifest 的输出文件也拒绝覆盖。退出码：0 成功、1 校验/签名/文件操作失败、2 参数错误。
生产内置公钥在 U4 由用户生成并填入；不设测试源编译开关时，内置公钥列表为空，验签会拒绝全部清单。

## U2 开发安装与版本回滚验收（New-DevInstall.ps1）

这是供维护者**手动验收**的安装结构，不是 Inno 安装包，也不创建快捷方式/卸载登记。
需要本机缓存中已有 App 和 win-x64 Native AOT 发布依赖，以及 Native AOT 所需的 C++ 工具链。
脚本只从本机 NuGet 缓存还原，不下载缺失依赖；缺失时停止，不替换 `state.json`。

**重要：数据目录仍是真实的 `%APPDATA%\AniMeido`，插件仍使用原有的用户目录。**
执行以下步骤前请关闭全部 AniMeido 及 PluginHost，并自行备份用户数据；这不是数据隔离的测试沙箱。
不要使用 Program Files、`%LocalAppData%\Programs\AniMeido` 或真实用户数据目录作为 `-Root`。
新根必须为空，版本目录已存在则拒绝；已有安装必须加 `-AsPending`，让启动器负责切换。
失败的发布暂存目录会保留，检查后由维护者自行清理，不自动覆盖。

在仓库根目录打开 PowerShell 7，下面版本均为**示例**（验收时替换成递增的合法 SemVer）：

```powershell
# 1. 建立第一版，App 按现有 win-x64 profile 自包含发布（包含 PluginHost）
.\Tools\New-DevInstall.ps1 -Root D:\AniMeido-U2-Dev -Version 2.2.0
& D:\AniMeido-U2-Dev\AniMeido.exe
# 数据库完成初始化且主窗口 Activate 后，state.json 的 currentHealthy 应为 true
# 关闭主程序（若驻留托盘，请用“退出”，不是仅关闭窗口）

# 2. 加入新版本；此时只设 pending，current 仍为 2.2.0
.\Tools\New-DevInstall.ps1 -Root D:\AniMeido-U2-Dev -Version 2.3.0 -AsPending
& D:\AniMeido-U2-Dev\AniMeido.exe
# 切换为 2.3.0，previous 为 2.2.0；显示主窗口后新版本标记为健康
# 再次完整退出主程序

# 3. 加入立即退出的假版本：whoami.exe 改名为 AniMeido.App.exe，不会标记健康
.\Tools\New-DevInstall.ps1 -Root D:\AniMeido-U2-Dev -Version 2.4.0 -AsPending -Broken
& D:\AniMeido-U2-Dev\AniMeido.exe  # 第一次：切换到假版本，failedStarts=1
# 等待假版本退出后再执行，不能连点
& D:\AniMeido-U2-Dev\AniMeido.exe  # 第二次：failedStarts=2
# 再等假版本退出
& D:\AniMeido-U2-Dev\AniMeido.exe  # 第三次：自动回到 2.3.0，主窗口显示一次回滚提示
```

预期：`failedVersions` 包含 `2.4.0`；回滚提示读取后 `rolledBackFrom` 清空；
健康后清理多余 `app-*`、`*.staging` 与旧更新包，保留 current、previous 和已有 pending。
`-Broken` 不执行数据库迁移，所以验证的是**无迁移自动回滚**分支。
迁移后回滚应另用确实升级 schema 且无法显示主窗口的版本人工验收：选择“是”先另存 db/wal/shm，
再恢复迁移前备份；“否”保留新版本、重新给两次机会。回滚前数据保存在
`%APPDATA%\AniMeido\Backups\update\AniMeido-rollback-from-*\`，不会被迁移备份滚动清理删除。

启动器日志为 `%APPDATA%\AniMeido\logs\launcher.log`（超过 1 MB 后轮换为 `launcher.1.log`）。
`--wait-pid <PID>` 最长等待 30 秒，超时不切换；当前安装已有主程序运行时只按 current 多开，不计失败。
安装模式使用固定 AppUserModelID `AniMeido.Desktop`；U4 快捷方式必须设置相同值，
**切换版本后通知设置是否保留、通知激活路径、托盘和退出行为仍需人工验收**。

## U3 应用内更新人工验收（本地签名源）

以下全部由维护者手动执行。构建、单元测试不代表 WinUI、托盘退出或版本切换已验收。
沿用 U2 限制：独立开发安装根；**数据目录仍是真实的 `%APPDATA%\AniMeido`**。
验收前关闭全部 AniMeido/PluginHost 并自行备份数据。脚本不会启动应用，也不是数据隔离沙箱。

### 测试密钥与编译开关

只有 `-p:AniMeidoUpdateTestSource=true` 定义 `UPDATE_TEST_SOURCE`，才编入测试公钥和本地映射处理器。
`New-DevInstall.ps1 -UpdateTestSource` 将属性传给 Updates 构建与 App 发布；正式发布不设此属性。
单独设置环境变量不能启用映射。U4 接入生产公钥前，普通安装构建验签失败是预期行为。

当前条件编译公钥配套私钥经维护者批准新生成，位于仓库外：
`D:\AniMeido-U3-Acceptance\keys\update-test.pem`；同目录的 `update-test-public.txt` 是单行 SPKI Base64 公钥。
**私钥不进仓库、不放 CI、不复制到更新源目录，不输出其内容。**
换机器或遗失私钥时，在新的仓库外路径用签名工具生成密钥，再将公钥文件的单行内容填入
`AniMeido.Updates/UpdateTrustedKeys.cs` 的 `#if UPDATE_TEST_SOURCE` 分支，并重建测试安装：

```powershell
$acceptance = 'D:\AniMeido-U3-Acceptance'
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
# 仅在需要新密钥时执行；已有目标文件会拒绝覆盖。
New-Item -ItemType Directory -Path "$acceptance\keys" -Force | Out-Null
dotnet run --project .\Tools\AniMeido.UpdateSigner "-p:RestoreSources=$cache" -p:NuGetAudit=false -- keygen --private "$acceptance\keys\new-update-test.pem" --public "$acceptance\keys\new-update-test-public.txt"
```

### 准备安装、两个通道的包与签名清单

在仓库根目录使用 PowerShell 7。下面使用当前配套的 `update-test.pem`，不能用不匹配的私钥。
安装根必须为空或尚不存在。ZIP 必须包含完整程序和 PluginHost，根级有 `AniMeido.App.exe`，不能再套一层目录。

```powershell
$acceptance = 'D:\AniMeido-U3-Acceptance'
$install = Join-Path $acceptance 'install'
$source = Join-Path $acceptance 'source'
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
$private = Join-Path $acceptance 'keys\update-test.pem'
$public = Join-Path $acceptance 'keys\update-test-public.txt'
.\Tools\New-DevInstall.ps1 -Root $install -Version 2.2.0 -UpdateTestSource

foreach ($release in @(
    @{ Channel = 'stable'; Version = '2.3.0' },
    @{ Channel = 'preview'; Version = '2.4.0-preview.1' }
)) {
    $channel = $release.Channel
    $version = $release.Version
    $payload = Join-Path $acceptance "payload-$version"
    $channelRoot = Join-Path $source "win-x64\$channel"
    New-Item -ItemType Directory -Path $channelRoot -Force | Out-Null
    dotnet publish .\AniMeido.App -c Release -r win-x64 -p:PublishProfile=win-x64 "-p:AniMeidoVersion=$version" -p:AniMeidoUpdateTestSource=true "-p:RestoreSources=$cache" -p:NuGetAudit=false --output $payload
    if ($LASTEXITCODE -ne 0) { throw "发布 $version 失败" }
    $name = "AniMeido-$version-win-x64.zip"
    $zip = Join-Path $channelRoot $name
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip
    $manifest = Join-Path $channelRoot 'release.json'
    dotnet run --project .\Tools\AniMeido.UpdateSigner --no-restore -- manifest --zip $zip --version $version --channel $channel --url "https://dl.animeido.com/updates/win-x64/$channel/$name" --release-date 2026-10-04 --min-updatable-from 2.2.0 --out $manifest
    if ($LASTEXITCODE -ne 0) { throw "生成 $channel 清单失败" }
    dotnet run --project .\Tools\AniMeido.UpdateSigner --no-restore -- sign --private $private --file $manifest
    if ($LASTEXITCODE -ne 0) { throw "签名 $channel 清单失败" }
    dotnet run --project .\Tools\AniMeido.UpdateSigner --no-restore -- verify --public $public --file $manifest --channel $channel
    if ($LASTEXITCODE -ne 0) { throw "验签 $channel 清单失败" }
}
$env:ANIMEIDO_UPDATE_TEST_ROOT = $source
& (Join-Path $install 'AniMeido.exe')
```

映射根对应 URL 的 `/updates/`，所以两个目录是 `source/win-x64/stable/` 和 `source/win-x64/preview/`，
各包含 ZIP、release.json 和 release.json.sig。其他 URL 原样放行，请勿在本地验收清单填其他域名。
本地映射支持 Range；验签、包大小/哈希和解压仍走正式代码。可加 `--notes-file <UTF-8 文本>` 测试说明。
生成包/清单与签名工具仅操作本地文件；重新签名已存在文件须明确加 `--overwrite`。

### 七项主流程验收（对应 U3 方案 §7）

1. **stable**：2.2.0 启动提示 2.3.0；下载中取消一次，确认 `packages/<sha256>.zip.partial` 保留，
   再重试续传。可用较大的完整包便于点击取消；准备完成后 ZIP 已删除。
2. 选择“立即重启”：启动器等旧 PID 退出，切到 2.3.0；数据库初始化完成且主窗口显示后，
   currentHealthy 为 true，保留 current/previous，清理多余版本和已完成 ZIP。
   `.partial` 最后修改时间在 14 天内保留，早于 14 天才删除。
3. **preview**：设置页切到预览版立即检查到 2.4.0-preview.1；下载后选“下次启动时更新”，
   current 不变、pending 已设置。完整退出，再从 AniMeido.exe 启动，应切到预览版。
4. **切回 stable**：显示已是最新，不降级；切换通道不会撤销已经准备好的 pending。
5. **篡改清单**：在没有 pending 时改 release.json 一个字节、不重签。手动检查显示签名失败，
   提示不出现清单中的版本、说明或包地址；启动检查只记 Warning。之后还原原始字节或重签。
6. **非安装模式**：直接运行不在 `app-<版本> + state.json` 结构里的 AniMeido.App.exe，
   仍读 version.json 并通过浏览器下载；设置页不显示通道，显示旧安装方式说明。
7. **托盘常驻**：开启常驻后选择立即重启，主程序与 PluginHost 均完整退出，不只是隐藏窗口；
   新版本正常启动。启动器缺失/创建失败时提示错误，不退出主程序，pending 保留。
   `--wait-pid` 超过 30 秒由启动器提示手动关闭、不切换，下次启动仍可切换。

### 补充分支与回滚前数据

- 跳过 stable/preview 指定版本：启动不提示；手动仍提示，下载或稍后再说取消跳过。
- failedVersions 中的版本：启动不提示，手动检查警告“曾启动失败并回滚”，仍允许重试。
- 低于 minUpdatableFrom：提示官网重新安装，不能应用内安装。
- 下载、权限、磁盘和校验失败：对话框内显示原因/路径、重试与关闭，不残留后台下载。
  同名 app-* 已存在但不是 pending 时拒绝且不删除；SetPending 失败不改 current。
- U2 迁移后回滚产生 `Backups/update/AniMeido-rollback-from-*` 时，安装模式设置页显示来源版本/时间；
  打开文件夹仅查看所选目录；删除前确认，不能删除当前数据库、更新前备份或其他目录。
  没有数据时隐藏；**不做恢复功能**，恢复入口留到首次结构升级前的独立批次。
- 普通构建不设 AniMeidoUpdateTestSource：PublicKeys 为空、App 无 UpdateTestSourceHandler 类型，
  且没有 ANIMEIDO_UPDATE_TEST_ROOT 映射代码；切勿将开发测试构建用于正式分发。
