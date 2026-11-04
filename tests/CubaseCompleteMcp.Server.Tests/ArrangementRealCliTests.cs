using System.Security.Cryptography;
using CubaseCompleteMcp.Server;

namespace CubaseCompleteMcp.Server.Tests;

/// <summary>
/// Optional real external-provider process acceptance. Standard Linux/Windows
/// contract suites do not claim this executed when the opt-in environment is
/// missing. The Render arrangement gate sets the flag and requires this exact
/// named case in a separate fresh TRX.
/// </summary>
public sealed class ArrangementRealCliTests
{
    [Fact]
    public async Task ArrangementRealCli01_McpActuallyExecutesInstalledMidivoltPromptAndRejectsOverwrite()
    {
        if (Environment.GetEnvironmentVariable("STEINBERG_REAL_ARRANGEMENT_E2E") != "1")
            return; // The separate Render gate verifies flag + TRX + independent CLI report.

        var root = Path.Combine(Path.GetTempPath(),
            "steinberg-arrangement-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tool = new SteinbergModesTools();
            var status = tool.Status();
            Assert.True(status["arrangement_mode"]!["midivolt"]!["configured"]!.GetValue<bool>());
            Assert.True(status["arrangement_mode"]!["groovemap_next"]!["configured"]!.GetValue<bool>());

            var path = Path.Combine(root, "mcp-midivolt-real.mid");
            var result = await tool.ArrangeMidi(
                "Full song, 8 bars in D minor at 110 bpm with chords melody bass drums",
                path);
            Assert.Equal("OkFilesOnly", result["status"]?.GetValue<string>());
            Assert.Equal("applied", result["execution_state"]?.GetValue<string>());
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 22);
            Assert.False(File.Exists(path + ".mcp-inflight"));
            var original = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

            var overwrite = await tool.ArrangeMidi("4 bars of drums in F major", path);
            Assert.Equal("OUTPUT_EXISTS", overwrite["code"]?.GetValue<string>());
            Assert.Equal(original, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            // The licensed Beat This! checkpoint is a real required input;
            // do not accept empty model paths or invent a synthetic inference.
            var noWeights = await tool.ArrangeTempo(
                Path.Combine(root, "not-a-drum-stem.wav"),
                Path.Combine(root, "tempo"),
                checkpoint_path: null);
            Assert.Equal("CHECKPOINT_REQUIRED", noWeights["code"]?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
