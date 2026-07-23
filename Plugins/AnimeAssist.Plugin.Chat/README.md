# AniMeido ChatPlugin

ChatPlugin is an optional AniMeido plugin. It is built as a separate project,
is not referenced by `AniMeido.App`, and is not copied into the base
application output.

It is currently retained for local use and architecture validation. There is
no plan to publish or distribute this plugin.

The current `0.1.0` implementation is deliberately a local UI prototype:

- opens an independent chat window from a command navigation item;
- owns its room, message, window, and lifecycle code;
- uses in-memory sample rooms and messages;
- has no account, network service, persistence, presence, or relationship
  model.

Create the installable package:

```powershell
dotnet msbuild .\Plugins\AnimeAssist.Plugin.Chat\AniMeido.Plugin.Chat.csproj `
  /t:PackPlugin /p:Configuration=Debug /p:Platform=x64
```

The package is written to:

```text
artifacts\plugins\AniMeido.Plugin.Chat-0.1.0.animeido-plugin
```

Install it from AniMeido's App Settings page and restart AniMeido. The plugin
can then be opened from the `聊天室` command in the main navigation.
