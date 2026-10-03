using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AniMeido.Updates.Tests")]

namespace AniMeido.UpdateSigner;

internal static class Program
{
    private static int Main(string[] args) => SignerCommands.Run(args, Console.Out, Console.Error);
}
