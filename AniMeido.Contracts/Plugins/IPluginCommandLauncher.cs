namespace AniMeido.Contracts.Plugins;

/// <summary>由独立宿主中的插件执行其命令贡献，不在 App 内加载插件界面。</summary>
public interface IPluginCommandLauncher
{
    /// <summary>执行本插件内的命令标识；取消请求不保证已开始的动作尚未生效。</summary>
    Task InvokeCommandAsync(
        string commandId,
        CancellationToken cancellationToken = default);
}
