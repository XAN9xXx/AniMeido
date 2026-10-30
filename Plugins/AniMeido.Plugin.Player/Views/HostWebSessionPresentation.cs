using AniMeido.Plugin.Player.Sources.Web;

namespace AniMeido.Plugin.Player.Views;

internal static class HostWebSessionPresentation
{
    internal const string ReadFailureNotice =
        "无法读取站点登录记录。可使用“清除全部会话”清除所有站点的登录信息，并恢复记录列表。";

    internal static string GetClearResultText(string? host, HostWebSessionClearResult result)
    {
        if (result.MetadataFailure is null)
        {
            return host is null
                ? "已清除全部站点登录会话。"
                : $"已清除 {host} 的登录会话。";
        }

        return host is null
            ? "已清除全部站点的登录 Cookie，但记录文件未能清空。请重试“清除全部会话”。"
            : $"已清除 {host} 的登录 Cookie，但未能保存站点记录。原记录文件已保留。";
    }
}
