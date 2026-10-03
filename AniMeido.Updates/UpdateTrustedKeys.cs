namespace AniMeido.Updates;

public static class UpdateTrustedKeys
{
    // U4 由用户生成密钥后填入。空列表会使验签失败，不能跳过验签。
    public static IReadOnlyList<string> PublicKeys { get; } = Array.AsReadOnly(new string[]
    {
#if UPDATE_TEST_SOURCE
        // 仅用于本地开发验收，私钥在仓库外保管；正式产物不含此公钥。
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEqb1QRo/Hi1LW5mvX1rSnRdz3UU6XVpIKMN/sDXwsljLTyP8gn5lPrm+PtDqQCyUsfDnyt0hpM1p0+qrPEXC4Ew==",
#endif
    });
}
