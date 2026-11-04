using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;

namespace CubaseCompleteMcp.Server.Tests;

/// <summary>
/// Separate Render-only test: execute the *real* installed GrooveMap CLI
/// through SteinbergModesTools.ArrangeTempo with checksum-pinned Beat This!
/// final0 and the newly synthesized WAV from the independent model gate.
/// No fake MCP provider, no Windows broker and no DAW host side effects.
/// </summary>
public sealed class ArrangementRealTempoMcpTests
{
    private const string Final0Sha256 =
        "8c328b45f59d8dd3dff219253ff6a8d6482be57d0133a29140e2febbf8eb8331";

    [Fact]
    public async Task ArrangementRealTempoMcp01_ActualGrooveMapWithFinal0ProducesTempoAndDrumMidi()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("STEINBERG_REAL_TEMPO_MCP_E2E"));
        var sample = Environment.GetEnvironmentVariable("STEINBERG_REAL_TEMPO_MCP_WAV");
        var checkpoint = Environment.GetEnvironmentVariable("STEINBERG_REAL_TEMPO_MCP_CHECKPOINT");
        var outputRoot = Environment.GetEnvironmentVariable("STEINBERG_REAL_TEMPO_MCP_OUTDIR");
        Assert.True(sample is not null && Path.IsPathFullyQualified(sample) && File.Exists(sample),
            "A real generated PCM WAV is required, not a mock file path.");
        Assert.True(checkpoint is not null && Path.IsPathFullyQualified(checkpoint) && File.Exists(checkpoint),
            "The already checksum-pinned final0 weights must be present.");
        Assert.True(outputRoot is not null && Path.IsPathFullyQualified(outputRoot) && Directory.Exists(outputRoot),
            "The isolated Render output directory must already exist.");

        using (var modelStream = File.OpenRead(checkpoint!))
        {
            Assert.Equal(Final0Sha256,
                Convert.ToHexString(SHA256.HashData(modelStream)).ToLowerInvariant());
        }
        var wavShaBefore = HashFile(sample!);
        var prefix = Path.Combine(outputRoot!, "mcp-arrange-real");
        var expected = new[]
        {
            prefix + "_tempo.mid",
            prefix + "_drums.mid",
            prefix + "_timing.json"
        };
        Assert.All(expected, p => Assert.False(File.Exists(p), "Output must be fresh: " + p));

        var tools = new SteinbergModesTools();
        Assert.True(tools.Status()["arrangement_mode"]!["groovemap_next"]!["configured"]!.GetValue<bool>());

        var result = await tools.ArrangeTempo(sample!, prefix, checkpoint_path: checkpoint);
        Assert.Equal("OkFilesOnly", result["status"]?.GetValue<string>());
        Assert.Equal("applied", result["execution_state"]?.GetValue<string>());
        Assert.Equal(3, result["files"]!.AsArray().Count);
        Assert.All(expected, p => Assert.True(File.Exists(p) && new FileInfo(p).Length > 22,
            "Real GrooveMap must produce nonempty files: " + p));
        Assert.False(File.Exists(expected[0] + ".mcp-inflight"));
        Assert.Equal(wavShaBefore, HashFile(sample!));

        var timing = JsonNode.Parse(File.ReadAllText(expected[2]))!;
        Assert.Equal("groovemap-next-timing-v2", timing["schema"]?.GetValue<string>());
        Assert.Equal("CPJKU/beat_this:minimal", timing["beat_source"]?.GetValue<string>());
        Assert.Equal("cleanroom-dsp-3class", timing["transcription_source"]?.GetValue<string>());
        Assert.True(timing["beats_sec"]!.AsArray().Count >= 5);
        Assert.True(timing["tempo_points"]!.AsArray().Count >= 4);

        var hashes = expected.Select(HashFile).ToArray();
        var overwrite = await tools.ArrangeTempo(sample!, prefix, checkpoint_path: checkpoint);
        Assert.Equal("OUTPUT_EXISTS", overwrite["code"]?.GetValue<string>());
        Assert.Equal("not_verified", overwrite["execution_state"]?.GetValue<string>());
        Assert.Equal(hashes, expected.Select(HashFile).ToArray());
        Assert.Equal(wavShaBefore, HashFile(sample!));

        var noCheckpoint = await tools.ArrangeTempo(sample!, prefix + "-missing", checkpoint_path: null);
        Assert.Equal("CHECKPOINT_REQUIRED", noCheckpoint["code"]?.GetValue<string>());
        Assert.False(File.Exists(prefix + "-missing_tempo.mid"));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
