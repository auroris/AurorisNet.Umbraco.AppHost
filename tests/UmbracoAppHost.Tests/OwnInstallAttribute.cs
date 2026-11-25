using Xunit.v3;

namespace UmbracoAppHost.Tests;

/// <summary>
/// A test that installs a site of its own, which takes fifteen seconds or more, where the others start from a copy of an
/// install they share. The run of a Dependabot update leaves these out; see <c>.github/workflows/ci.yml</c>. The shared
/// installs still take the new package versions through an install and a first-boot import.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OwnInstallAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Category", "OwnInstall")];
}
