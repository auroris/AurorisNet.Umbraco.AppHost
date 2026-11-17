namespace UmbracoAppHost.Tests;

/// <summary>The testing packages stay usable from any test runner: neither references a test framework.</summary>
public sealed class TestingPackageTests
{
    private static readonly string[] TestFrameworks = ["xunit", "nunit", "MSTest", "Microsoft.VisualStudio.TestPlatform", "Microsoft.Testing"];

    [Theory]
    [InlineData(typeof(TestSite))]
    [InlineData(typeof(FormsClient))]
    public void A_testing_package_references_no_test_framework(Type type)
    {
        var frameworks = type.Assembly.GetReferencedAssemblies().Select(reference => reference.Name!)
            .Where(name => TestFrameworks.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.Empty(frameworks);
    }
}
