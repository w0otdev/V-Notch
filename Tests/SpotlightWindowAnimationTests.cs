using Xunit;

namespace VNotch.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpotlightWindowAnimationCollection
{
    private SpotlightWindowAnimationCollection() { }
    public const string Name = "Spotlight window animation";
}
