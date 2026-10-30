using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AniMeido.Updates.Tests")]

namespace AniMeido.UpdateSigner;

internal static class Program
{
    private static Task<int> Main(string[] args) => SignerCommands.RunAsync(args, Console.Out, Console.Error);
}
