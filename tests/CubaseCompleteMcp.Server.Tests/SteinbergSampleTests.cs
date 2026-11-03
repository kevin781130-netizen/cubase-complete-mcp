using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergSampleTests
{
    [Fact]
    public void SAMPLE01_ToolsExposeSearchAndPlanMcpContracts()
    {
        var type = typeof(SteinbergSampleTools);
        var search = McpServerTool.Create(type.GetMethod(nameof(SteinbergSampleTools.Search))!, type);
        var plan = McpServerTool.Create(type.GetMethod(nameof(SteinbergSampleTools.Plan))!, type);
        Assert.Equal("steinberg_sample_search", search.ProtocolTool.Name);
        Assert.Equal("steinberg_sample_plan", plan.ProtocolTool.Name);
        Assert.True(search.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("library_root", out _));
        Assert.True(plan.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("sample_path", out _));
    }

    [Fact]
    public void SAMPLE02_SearchUsesFilenameTagsAndRefusesMissingOrInvalidBpm()
    {
        var root = MakeRoot();
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "Kick"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "punch_120bpm.wav"), FakeWav());
            File.WriteAllBytes(Path.Combine(dir.FullName, "punch_95bpm.wav"), FakeWav());
            File.WriteAllBytes(Path.Combine(dir.FullName, "readme.txt"), [1, 2, 3]);

            var tools = new SteinbergSampleTools();
            var response = tools.Search(root, category: "kick", target_bpm: 120);
            Assert.Equal("Ok", response["status"]!.GetValue<string>());
            Assert.Equal(2, response["matching_files"]!.GetValue<int>());
            var results = (JsonArray)response["results"]!;
            Assert.Equal(120.0, results[0]!["filename_bpm"]!.GetValue<double>());
            Assert.Equal("filename_tag_only", results[0]!["bpm_source"]!.GetValue<string>());
            Assert.Equal("Error", tools.Search(root, target_bpm: -5)["status"]!.GetValue<string>());
            Assert.Equal("Error", tools.Search(Path.Combine(root, "missing"))["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SAMPLE03_PlanDraftPreservesOriginalAndRefusesOverwrite()
    {
        var root = MakeRoot();
        try
        {
            var sample = Path.Combine(root, "snare_110bpm.wav");
            var source = FakeWav();
            File.WriteAllBytes(sample, source);
            var output = Path.Combine(root, "plan.json");
            var tools = new SteinbergSampleTools();
            var response = tools.Plan(root, sample, output, project_bpm: 110, bar: 2, beat: 1);
            Assert.Equal("OkDraft", response["status"]!.GetValue<string>());
            Assert.False(response["host_modified"]!.GetValue<bool>());
            Assert.Equal(3840L, response["tick"]!.GetValue<long>());
            var manifest = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal("draft_requires_approval", manifest["status"]!.GetValue<string>());
            Assert.Equal("none", manifest["host_action"]!.GetValue<string>());
            Assert.Equal(2.181818, manifest["placement"]!["seconds_from_project_start"]!.GetValue<double>(), 6);
            Assert.Equal(source, File.ReadAllBytes(sample));
            Assert.Equal("Error", tools.Plan(root, sample, output, 110)["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SAMPLE04_OutsideLibraryAndBadHeaderAreRejected()
    {
        var root = MakeRoot();
        var outside = MakeRoot();
        try
        {
            var externalSample = Path.Combine(outside, "kick.wav");
            File.WriteAllBytes(externalSample, FakeWav());
            var tools = new SteinbergSampleTools();
            Assert.Equal("Error", tools.Plan(root, externalSample, Path.Combine(root, "out.json"), 120)["status"]!.GetValue<string>());
            var bad = Path.Combine(root, "broken.wav");
            File.WriteAllBytes(bad, new byte[44]);
            Assert.Equal("Error", tools.Plan(root, bad, Path.Combine(root, "bad.json"), 120)["status"]!.GetValue<string>());
            Assert.False(File.Exists(Path.Combine(root, "bad.json")));
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    private static string MakeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steinberg-sample-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] FakeWav()
    {
        // Signature-only fake fixture: tests file metadata and manifest safety, NOT playback.
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes.AsSpan(0));
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        return bytes;
    }
}
