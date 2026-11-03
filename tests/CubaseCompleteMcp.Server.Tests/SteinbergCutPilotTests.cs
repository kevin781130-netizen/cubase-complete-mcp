using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergCutPilotTests
{
    [Fact]
    public void FOLEY05_McpToolExposesExplicitOptionalExternalCutPilotDetector()
    {
        var type = typeof(SteinbergCutPilotTools);
        var mcp = McpServerTool.Create(type.GetMethod(nameof(SteinbergCutPilotTools.DetectSceneCuts))!, type);
        Assert.Equal("steinberg_foley_cutpilot_scenes", mcp.ProtocolTool.Name);
        var schema = mcp.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var name in new[] { "video_path", "cutpilot_root", "output_manifest",
            "sampling_interval", "threshold" })
            Assert.True(schema.TryGetProperty(name, out _), name);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "providers",
            "cutpilot_foley_scene_provider.py")), "Packaged CutPilot Python provider is missing.");
    }

    [Fact]
    public async Task FOLEY06_MissingPythonAndFfprobeRejectWithoutOutput()
    {
        // Deliberately set no global environment variables in this test:
        // nonexistent input is always refused even when a provider is configured.
        var root = Path.Combine(Path.GetTempPath(), "steinberg-cutpilot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "cues.json");
            var result = await new SteinbergCutPilotTools().DetectSceneCuts(
                Path.Combine(root, "not-a-video.mp4"), root, output);
            Assert.Equal("Error", result["status"]!.GetValue<string>());
            Assert.False(File.Exists(output));
            Assert.False(result["host_modified"]!.GetValue<bool>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY07_CutPilotProvenanceNeverClaimsSemanticMotionOrSfxDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "steinberg-cutpilot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Path.Combine(root, "scene.mov");
            File.WriteAllBytes(video, [0, 0, 0, 0]);
            var output = Path.Combine(root, "scene-cut-cues.json");
            var cues = "[{\"frame\":30,\"duration_frames\":0,\"event_label\":\"Visual scene boundary - review Foley\",\"track\":\"other\",\"sfx_query\":\"review whether a scene transition sound is needed\"}]";
            var result = new SteinbergFoleyTools().SpotCutPilot(video, output, cues, 30000, 1001, 60);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());
            Assert.True(result["video_analyzed"]!.GetValue<bool>());
            var doc = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal("cutpilot_histogram_scene_boundaries_not_action_detection",
                doc["source"]!.GetValue<string>());
            Assert.Equal("ffprobe_average_frame_rate_assumed_cfr",
                doc["video"]!["timing_provenance"]!.GetValue<string>());
            Assert.Equal("draft_requires_approval", doc["status"]!.GetValue<string>());
            Assert.Equal("unmatched_requires_review",
                doc["events"]![0]!["selection_status"]!.GetValue<string>());
            Assert.Null(doc["events"]![0]!["sample_path"]);
            Assert.Equal(1.001, doc["events"]![0]!["seconds_from_video_start"]!.GetValue<double>(), 9);
        }
        finally { Directory.Delete(root, true); }
    }
}
