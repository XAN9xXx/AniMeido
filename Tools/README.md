# AniMeido plugin packaging

`pack-plugin.ps1` creates a `.animeido-plugin` ZIP package with a versioned
manifest and SHA-256 hashes for every packaged dependency and resource.

Package a published plugin directory:

```powershell
.\Tools\pack-plugin.ps1 `
  -PluginDir .\artifacts\ChatPlugin `
  -PluginId AniMeido.Plugin.Chat `
  -DisplayName ChatPlugin `
  -Version 1.0.0 `
  -MinAppVersion 1.1.0 `
  -EntryAssembly AniMeido.Plugin.Chat.dll `
  -OutputPath .\artifacts\AniMeido.Plugin.Chat-1.0.0.animeido-plugin
```

The package is installed from AniMeido's App Settings page. Installation,
enable/disable, rollback, and uninstall changes take effect after restart.

AniMeido plugins run with the same local permissions as the application. The
package is not publisher-signed, so install only plugins from a trusted source.
The hashes detect accidental corruption after packaging or installation; they
do not establish who created the package.
