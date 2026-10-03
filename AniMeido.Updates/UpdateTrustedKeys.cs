namespace AniMeido.Updates;

public static class UpdateTrustedKeys
{
    // U4 由用户生成密钥后填入。空列表会使验签失败，不能跳过验签。
    public static IReadOnlyList<string> PublicKeys { get; } = Array.AsReadOnly(Array.Empty<string>());
}
