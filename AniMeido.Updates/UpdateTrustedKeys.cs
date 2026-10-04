namespace AniMeido.Updates;

public static class UpdateTrustedKeys
{
    public static IReadOnlyList<string> PublicKeys { get; } = Array.AsReadOnly(new string[]
    {
        // 2026-10-04 生成的生产公钥；私钥离线保存，绝不放入源码或 CI。
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOJeoMG4coWpH3j9W3928GGN7ARuQ0VrYv9YvlD6QmghBy9I6YN4AdAAym/9lmfIaonp5VSaGuyE/9rNUEIB10A==",
#if UPDATE_TEST_SOURCE
        // 仅用于本地开发验收，私钥在仓库外保管；正式产物不含此公钥。
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEqb1QRo/Hi1LW5mvX1rSnRdz3UU6XVpIKMN/sDXwsljLTyP8gn5lPrm+PtDqQCyUsfDnyt0hpM1p0+qrPEXC4Ew==",
#endif
    });
}
