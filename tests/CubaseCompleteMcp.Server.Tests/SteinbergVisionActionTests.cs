using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergVisionActionTests
{
    [Fact]
    public void FOLEY08_OptionalXclipMcpToolHasExplicitVideoAndWeightsInputs()
    {
        var type = typeof(SteinbergVisionActionTools);
        var tool = McpServerTool.Create(type.GetMethod(nameof(SteinbergVisionActionTools.Classify))!, type);
        Assert.Equal("steinberg_foley_xclip_actions", tool.ProtocolTool.Name);
        var fields = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var field in new[]
            { "video_path", "model_directory", "output_manifest", "min_relative_score", "min_margin" })
            Assert.True(fields.TryGetProperty(field, out _), field);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "providers",
            "xclip_foley_action_provider.py")), "Bundled XCLIP provider is missing.");
    }

    [Fact]
    public void FOLEY09_ModelCandidateSheetPreservesScoresButNeverClaimsConfirmedActionOrSfx()
    {
        var directory = Path.Combine(Path.GetTempPath(), "steinberg-xclip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var video = Path.Combine(directory, "sample.mp4");
            File.WriteAllBytes(video, [0, 0, 0, 0]);
            var output = Path.Combine(directory, "review.json");
            var cues = """
                [{"frame":30,"duration_frames":0,"track":"footsteps",
                  "event_label":"Possible action: walking",
                  "sfx_query":"review possible footsteps",
                  "action_class":"walking","relative_score":0.77,"score_margin":0.70,
                  "window_start_frame":0,"window_end_frame":60}]
                """;
            var result = new SteinbergFoleyTools().SpotVision(
                video, output, cues, 30000, 1001, 120);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());
            Assert.True(result["video_analyzed"]!.GetValue<bool>());
            Assert.False(result["host_modified"]!.GetValue<bool>());
            var manifest = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal("draft_requires_approval", manifest["status"]!.GetValue<string>());
            Assert.Equal("local_xclip_video_text_similarity_candidates_not_confirmed_actions",
                manifest["source"]!.GetValue<string>());
            Assert.Equal("none", manifest["host_action"]!.GetValue<string>());
            var row = manifest["events"]![0]!;
            Assert.Equal("walking", row["action_candidate_label"]!.GetValue<string>());
            Assert.Equal(0.77, row["relative_prompt_score"]!.GetValue<double>());
            Assert.Equal(0.70, row["score_margin"]!.GetValue<double>());
            Assert.Equal(1.001, row["seconds_from_video_start"]!.GetValue<double>(), 9);
            Assert.Equal("unverified_video_text_similarity", row["classification_status"]!.GetValue<string>());
            Assert.Equal("unmatched_requires_review", row["selection_status"]!.GetValue<string>());
            Assert.Null(row["sample_path"]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FOLEY10_InvalidWindowAndScoreRefuseManifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "steinberg-xclip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var video = Path.Combine(directory, "sample.mp4");
            File.WriteAllBytes(video, [0, 0, 0, 0]);
            var output = Path.Combine(directory, "review.json");
            var cues = """
                [{"frame":30,"duration_frames":0,"track":"footsteps",
                  "event_label":"Possible walking","sfx_query":"review footsteps",
                  "action_class":"walking","relative_score":0.1,"score_margin":0.2,
                  "window_start_frame":0,"window_end_frame":60}]
                """;
            Assert.Equal("INVALID_MODEL_CANDIDATE",
                new SteinbergFoleyTools().SpotVision(video, output, cues, 30, 1, 120)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
            var illegal = cues.Replace("0.1,", "0.75,").Replace("walking\",\"relative_score",
                "not_a_real_class\",\"relative_score");
            Assert.Equal("INVALID_ACTION_CLASS",
                new SteinbergFoleyTools().SpotVision(video, output, illegal, 30, 1, 120)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FOLEY11_UnconfiguredOrMissingVideoCannotClaimInference()
    {
        var response = await new SteinbergVisionActionTools().Classify(
            Path.Combine(Path.GetTempPath(), "missing-vision.mp4"),
            Path.Combine(Path.GetTempPath(), "missing-local-model"),
            Path.Combine(Path.GetTempPath(), "never-create-vision.json"));
        Assert.Equal("Error", response["status"]!.GetValue<string>());
        Assert.False(response["video_analyzed"]!.GetValue<bool>());
        Assert.False(response["host_modified"]!.GetValue<bool>());
    }
}
