using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class MacNativeInstallerTests
{
    [Fact]
    public void InstallerSignsTheAppWithAStableBundleIdentifier()
    {
        var script = ReadRepositoryFile("scripts/install-macos-native.sh");

        script.Should().Contain(
            "APP_BUNDLE_ID=\"${NETWORK_OPTIMIZER_BUNDLE_ID:-net.ozarkconnect.networkoptimizer}\"");
        script.Should().Contain(
            "codesign --force --sign - --identifier \"$APP_BUNDLE_ID\" NetworkOptimizer.Web");
        script.Should().NotContain("codesign --force --sign - NetworkOptimizer.Web");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "NetworkOptimizer.sln")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must run from inside a NetworkOptimizer checkout");
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }
}
