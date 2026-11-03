using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergFoleyTests
{
    [Fact]
    public void FOLEY01_ExposesReviewOnlySpottingMcpContract()
    {
        var type = typeof(SteinbergFoleyTools);
        var tool = McpServerTool.Create(type.GetMethod(nameof(SteinbergFoleyTools.Spot))!, type);
        Assert.Equal("steinberg_foley_spot", tool.ProtocolTool.Name);
        var fields = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var field in new[]
            { "video_path", "output_manifest", "cues_json", "fps_numerator", "fps_denominator", "video_frame_count" })
            Assert.True(fields.TryGetProperty(field, out _), field);
    }

    [Fact]
    public void FOLEY02_RationalFpsAndSortedEventsPreserveSourceFrames()
    {
        var root = CreateRoot();
        try
        {
            var video = Path.Combine(root, "shot.mp4");
            File.WriteAllBytes(video, [0, 0, 0, 0]); // identity/path fixture, NOT a decoded or probed video.
            var output = Path.Combine(root, "spotting.json");
            var cues = JsonSerializer.Serialize(new[]
            {
                new { frame = 120, duration_frames = 60, event_label = "Door closes", track = "doors", sfx_query = "wood door close" },
                new { frame = 30, duration_frames = 1, event_label = "腳步", track = "footsteps", sfx_query = "shoes concrete step" }
            });
            var result = new SteinbergFoleyTools().Spot(video, output, cues, 30000, 1001, 240);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());
            Assert.False(result["video_analyzed"]!.GetValue<bool>());
            Assert.False(result["host_modified"]!.GetValue<bool>());
            Assert.Equal(2, result["unmatched_samples"]!.GetValue<int>());
            var manifest = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal("steinberg-foley-cues-v1", manifest["schema"]!.GetValue<string>());
            Assert.Equal("caller_supplied_spotting_not_video_inference", manifest["source"]!.GetValue<string>());
            Assert.Equal("caller_supplied_unverified",
                manifest["video"]!["timing_provenance"]!.GetValue<string>());
            var rows = (JsonArray)manifest["events"]!;
            Assert.Equal(2, rows.Count);
            Assert.Equal(30L, rows[0]!["frame"]!.GetValue<long>());
            Assert.Equal(120L, rows[1]!["frame"]!.GetValue<long>());
            Assert.Equal(1.001, rows[0]!["seconds_from_video_start"]!.GetValue<double>(), 9);
            Assert.Equal(4.004, rows[1]!["seconds_from_video_start"]!.GetValue<double>(), 9);
            Assert.Equal(2.002, rows[1]!["duration_seconds"]!.GetValue<double>(), 9);
            Assert.Equal("腳步", rows[0]!["event_label"]!.GetValue<string>());
            Assert.Equal("unmatched_requires_review", rows[0]!["selection_status"]!.GetValue<string>());
            Assert.Null(rows[0]!["sample_path"]);
            Assert.Equal("none", manifest["host_action"]!.GetValue<string>());
            Assert.Equal("OUTPUT_OR_IO_ERROR",
                new SteinbergFoleyTools().Spot(video, output, cues, 30000, 1001, 240)["code"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY03_InvalidOrOutOfRangeCuesDoNotCreateOutput()
    {
        var root = CreateRoot();
        try
        {
            var video = Path.Combine(root, "scene.mov");
            File.WriteAllBytes(video, [1, 2, 3, 4]);
            var output = Path.Combine(root, "out.json");
            var tools = new SteinbergFoleyTools();
            var invalid = JsonSerializer.Serialize(new[]
            {
                new { frame = 240, duration_frames = 0, event_label = "Footstep", track = "footsteps", sfx_query = "footstep" }
            });
            Assert.Equal("INVALID_CUE_FRAME", tools.Spot(video, output, invalid, 24, 1, 240)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));

            var valid = JsonSerializer.Serialize(new[]
            {
                new { frame = 0, duration_frames = 0, event_label = "Impact", track = "impacts", sfx_query = "hit" }
            });
            Assert.Equal("INVALID_FPS", tools.Spot(video, output, valid, 0, 1, 240)["code"]!.GetValue<string>());
            Assert.Equal("INVALID_CUES_JSON", tools.Spot(video, output, "{bad", 24, 1, 240)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY04_NoVideoProbeAndMissingFilesFailClosed()
    {
        var root = CreateRoot();
        try
        {
            var video = Path.Combine(root, "missing.mp4");
            var output = Path.Combine(root, "out.json");
            var valid = JsonSerializer.Serialize(new[]
            {
                new { frame = 0, event_label = "Wind", track = "ambience", sfx_query = "wind ambience" }
            });
            var result = new SteinbergFoleyTools().Spot(video, output, valid, 25, 1, 250);
            Assert.Equal("Error", result["status"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steinberg-foley-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
