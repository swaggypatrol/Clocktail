using System.Runtime.CompilerServices;

namespace Clocktail.Tests.Broker;

public class ElevatedLabPathTests
{
    [Fact]
    public void Elevated_game_verifier_only_uses_the_bundled_lab()
    {
        // Source guard: exercising Verify itself requires a live, matching GPU and starts a child.
        string source = File.ReadAllText(Path.Combine(Repository(), "src", "Clocktail.Broker", "WindowsGameAccess.cs"));
        Assert.DoesNotContain("CLOCKTAIL_LAB_EXE", source);
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, BrokerInstallBundle.LabName)", source);
    }

    private static string Repository([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", ".."));
}
