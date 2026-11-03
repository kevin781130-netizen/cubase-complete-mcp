using System.Text.Json;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergFoleySampleTests
{
    [Fact]
    public void FOLEY12_McpExposesCandidateSearchWithoutAutomaticSelection()
    {
        var type = typeof(SteinbergFoleySampleTools);
        var tool = McpServerTool.Create(type.GetMethod(nameof(SteinbergFoleySampleTools.Recommend))!, type);
        Assert.Equal("steinberg_foley_sample_candidates", tool.ProtocolTool.Name);
        var fields = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var name in new[] { "fusion_report", "library_root", "output_manifest",
                                      "rights_registry", "max_per_event" })
            Assert.True(fields.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void FOLEY13_SuggestsDirectionalLocalMetadataAndKeepsUnknownRightsUnapproved()
    {
        var root = MakeRoot();
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
            var doors = Directory.CreateDirectory(Path.Combine(library, "Doors")).FullName;
            var open = Sample(doors, "wood_door_open.wav");
            var close = Sample(doors, "metal_door_close.wav");
            Sample(doors, "door_generic_creak.wav");
            Sample(doors, "door_open_close_ambiguous.wav");
            Sample(doors, "gate_latch.wav");
            var report = WriteReview(root, opening: true, closing: true);
            var output = Path.Combine(root, "foley-suggest.json");
            var tools = new SteinbergFoleySampleTools();
            var result = tools.Recommend(report, library, output, max_per_event: 3);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());
            Assert.False(result["host_modified"]!.GetValue<bool>());
            Assert.Equal(2, result["event_count"]!.GetValue<int>());
            var manifest = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal("steinberg-foley-local-sample-candidates-v1", manifest["schema"]!.GetValue<string>());
            Assert.Equal("draft_requires_human_approval", manifest["status"]!.GetValue<string>());
            Assert.Equal("none", manifest["host_action"]!.GetValue<string>());
            Assert.Empty((JsonArray)manifest["selected_audio_files"]!);
            var events = (JsonArray)manifest["events"]!;
            Assert.Equal(2, events.Count);
            var openingList = (JsonArray)events[0]!["suggested_candidates"]!;
            var closingList = (JsonArray)events[1]!["suggested_candidates"]!;
            Assert.Equal(open, openingList[0]!["path"]!.GetValue<string>());
            Assert.Equal(close, closingList[0]!["path"]!.GetValue<string>());
            Assert.All(openingList, item =>
            {
                Assert.DoesNotContain("open_close", item!["relative_path"]!.GetValue<string>());
                Assert.DoesNotContain("metal_door_close", item["relative_path"]!.GetValue<string>());
                Assert.Equal("no_rights_record", item["license_status"]!.GetValue<string>());
                Assert.False(item["selected"]!.GetValue<bool>());
            });
            Assert.Null(events[0]!["chosen_sample_path"]);
            Assert.Null(events[1]!["chosen_sample_path"]);
            Assert.True(events[0]!["human_approval_required"]!.GetValue<bool>());
            Assert.Equal("Error", tools.Recommend(report, library, output)["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FOLEY14_DeclaredLicenseIsTraceableButNeverConsideredVerified()
    {
        var root = MakeRoot();
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
            var doors = Directory.CreateDirectory(Path.Combine(library, "Doors")).FullName;
            Sample(doors, "wood_door_open.wav");
            var source = WriteReview(root, opening: true, closing: false);
            var rightsFile = Path.Combine(root, "rights.json");
            var rights = new JsonObject
            {
                ["schema"] = "steinberg-local-sample-rights-v1",
                ["entries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["relative_path"] = "Doors/wood_door_open.wav",
                        ["license_id"] = "CC0-1.0",
                        ["source_url"] = "https://example.org/foley/wood-door",
                        ["attribution"] = "Test fixture, not actual grant"
                    }
                }
            };
            File.WriteAllText(rightsFile, rights.ToJsonString());
            var output = Path.Combine(root, "rights-candidates.json");
            var result = new SteinbergFoleySampleTools().Recommend(source, library, output, rightsFile);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());
            var first = JsonNode.Parse(File.ReadAllText(output))!["events"]![0]!["suggested_candidates"]![0]!;
            Assert.Equal("user_declared_not_verified", first["license_status"]!.GetValue<string>());
            Assert.Equal("CC0-1.0", first["rights_evidence"]!["license_id_declared"]!.GetValue<string>());
            Assert.False(first["selected"]!.GetValue<bool>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY15_UntrustedSourceAndMissingLibraryDoNotWriteManifest()
    {
        var root = MakeRoot();
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
            var source = WriteReview(root, opening: true, closing: false);
            var output = Path.Combine(root, "never.json");
            var invalidLibrary = new SteinbergFoleySampleTools().Recommend(
                source, Path.Combine(root, "nonexistent"), output);
            Assert.Equal("Error", invalidLibrary["status"]!.GetValue<string>());
            Assert.False(File.Exists(output));
            var fake = JsonNode.Parse(File.ReadAllText(source))!;
            fake["door_roi_motion_experimental"]!["motion_gated_rows"]![0]!["host_action"] = "insert_audio";
            File.WriteAllText(source, fake.ToJsonString());
            Assert.Equal("INVALID_REVIEW_ROW", new SteinbergFoleySampleTools().Recommend(
                source, library, output)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY16_FilenameQueryDoesNotMissDoorAudioBeyondFirstHundredFiles()
    {
        var root = MakeRoot();
        try
        {
            for (int i = 0; i < 160; i++)
                Sample(root, $"ambient_{i:000}.wav");
            var wanted = Sample(root, "door_open.wav");
            var finder = new SteinbergSampleTools();
            var matching = finder.Search(root, limit: 3, filename_query: "door");
            Assert.Equal("Ok", matching["status"]!.GetValue<string>());
            Assert.Equal(1, matching["matching_files"]!.GetValue<int>());
            Assert.Equal(wanted, matching["results"]![0]!["path"]!.GetValue<string>());
            Assert.Equal("INVALID_QUERY",
                finder.Search(root, filename_query: "../door")["code"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY17_InvalidRightsRegistryFailsClosed()
    {
        var root = MakeRoot();
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
            var source = WriteReview(root, opening: true, closing: false);
            var rightsPath = Path.Combine(root, "invalid-rights.json");
            File.WriteAllText(rightsPath, """
                {"schema":"steinberg-local-sample-rights-v1","entries":[
                  {"relative_path":"../outside.wav","license_id":"CC0","source_url":"https://example.org"}]}
                """);
            var outFile = Path.Combine(root, "no-output.json");
            var response = new SteinbergFoleySampleTools().Recommend(source, library, outFile, rightsPath);
            Assert.Equal("Error", response["status"]!.GetValue<string>());
            Assert.False(File.Exists(outFile));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string WriteReview(string root, bool opening, bool closing)
    {
        var rows = new JsonArray();
        if (opening) rows.Add(Row("mine_door_at_0", "opening_door"));
        if (closing) rows.Add(Row("subway_door_at_4", "closing_door"));
        rows.Add(Row("mine_door_at_4", null));
        var payload = new JsonObject
        {
            ["status"] = "PASS_REAL_BOTH_MODELS_FOUR_REVIEWED_VIDEO_WINDOWS_NOT_ACCURACY_VALIDATED",
            ["door_roi_motion_experimental"] = new JsonObject
            {
                ["status"] = "REVIEW_ONLY_NOT_DOOR_GEOMETRY_VALIDATED",
                ["motion_gated_rows"] = rows
            }
        };
        var file = Path.Combine(root, "model-review.json");
        File.WriteAllText(file, payload.ToJsonString());
        return file;
    }

    private static JsonObject Row(string id, string? action) => new()
    {
        ["id"] = id, ["status"] = action is null ? "abstained" : "review_proposal_not_confirmed",
        ["proposed_door_action"] = action,
        ["door_panel_geometry_verified"] = false,
        ["requires_human_review"] = true,
        ["video_sha256"] = new string('a', 64),
        ["contact_sha256"] = new string('b', 64),
        ["host_action"] = "none",
        ["selected_audio_file"] = null
    };

    private static string MakeRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "steinberg-foley-candidates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Sample(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes.AsSpan());
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
