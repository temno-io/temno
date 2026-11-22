using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Serializes tests that mutate TEMNO_REGISTRY_PATH / TEMNO_PROJECTS_DIR (or their
/// PALMIER_* legacy fallbacks) process env, and every test that sets the static
/// <c>ProjectHub.ActiveSession</c> or a <c>ToolExecutor</c> override: two such classes outside
/// this collection run in parallel and replace each other's session.
/// </summary>
[CollectionDefinition("EnvRegistry", DisableParallelization = true)]
public sealed class EnvRegistryCollection : ICollectionFixture<EnvRegistryCollection.Marker>
{
    public sealed class Marker;
}
