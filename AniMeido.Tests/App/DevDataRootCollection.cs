namespace AniMeido.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DevDataRootCollection
{
    public const string Name = "Development data root environment";
}

internal sealed class DevDataRootEnvironment : IDisposable
{
    private const string Variable = "ANIMEIDO_DATA_ROOT";
    private readonly string? _previous = Environment.GetEnvironmentVariable(Variable);

    internal DevDataRootEnvironment(string? value)
        => Environment.SetEnvironmentVariable(Variable, value);

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);
}
