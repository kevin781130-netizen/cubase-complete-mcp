using System.Buffers.Binary;
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
                                      "rights_registry", "max_per_event", "max_duration_seconds" })
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
            Assert.Equal("RIFF/WAV", openingList[0]!["audio_container_header"]!.GetValue<string>());
            Assert.Equal("PCM", openingList[0]!["audio_encoding_header"]!.GetValue<string>());
            Assert.Equal(8000, openingList[0]!["sample_rate_hz_header"]!.GetValue<int>());
            Assert.Equal(1.0, openingList[0]!["duration_seconds_from_header"]!.GetValue<double>(), 6);
            Assert.False(openingList[0]!["commercial_rights_cleared"]!.GetValue<bool>());
            Assert.Equal(0, manifest["preflight_excluded_files"]!.GetValue<int>());
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
            Assert.False(first["commercial_rights_cleared"]!.GetValue<bool>());
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

    [Fact]
    public void FOLEY18_BrokenTruncatedAndOverlongAudioAreExcluded()
    {
        var root = MakeRoot();
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "Samples")).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(library, "Doors")).FullName;
            var good = Sample(folder, "door_open_valid.wav");
            File.WriteAllBytes(Path.Combine(folder, "door_open_truncated.wav"), new byte[44]);
            var inconsistent = Sample(folder, "door_open_bad_rate.wav");
            var bytes = File.ReadAllBytes(inconsistent);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 999);
            File.WriteAllBytes(inconsistent, bytes);
            Sample(folder, "door_open_long.wav", seconds: 3);
            File.WriteAllBytes(Path.Combine(folder, "door_open_unknown.flac"), new byte[50]);
            var review = WriteReview(root, opening: true, closing: false);
            var output = Path.Combine(root, "filtered.json");
            var result = new SteinbergFoleySampleTools().Recommend(
                review, library, output, max_duration_seconds: 2.0);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());
            var manifest = JsonNode.Parse(File.ReadAllText(output))!;
            var candidates = (JsonArray)manifest["events"]![0]!["suggested_candidates"]!;
            Assert.Single(candidates);
            Assert.Equal(good, candidates[0]!["path"]!.GetValue<string>());
            Assert.Equal(4, result["preflight_excluded_files"]!.GetValue<int>());
            Assert.Equal(1, manifest["excluded_by_reason"]!["AUDIO_DURATION_OUTSIDE_FOLEY_RANGE"]!.GetValue<int>());
            Assert.Equal(1, manifest["excluded_by_reason"]!["RIFF_LENGTH_MISMATCH"]!.GetValue<int>());
            Assert.Equal(1, manifest["excluded_by_reason"]!["UNSUPPORTED_OR_INCONSISTENT_WAV_FMT"]!.GetValue<int>());
            Assert.Equal(1, manifest["excluded_by_reason"]!["UNSUPPORTED_OR_UNVERIFIED_AUDIO_FORMAT"]!.GetValue<int>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY19_NonCommercialRightsExcludedAndUnknownRightsNeverApproved()
    {
        var root = MakeRoot();
        try
        {
            var library = Directory.CreateDirectory(Path.Combine(root, "Library")).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(library, "Doors")).FullName;
            Sample(folder, "door_open_restricted.wav");
            var allowed = Sample(folder, "door_open_unknown.wav");
            var review = WriteReview(root, opening: true, closing: false);
            var registry = Path.Combine(root, "rights.json");
            File.WriteAllText(registry, new JsonObject
            {
                ["schema"] = "steinberg-local-sample-rights-v1",
                ["entries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["relative_path"] = "Doors/door_open_restricted.wav",
                        ["license_id"] = "CC-BY-NC-4.0",
                        ["source_url"] = "https://example.org/rights",
                        ["attribution"] = "Test declaration; no independent grant"
                    }
                }
            }.ToJsonString());
            var output = Path.Combine(root, "recommendations.json");
            var response = new SteinbergFoleySampleTools().Recommend(
                review, library, output, registry);
            Assert.Equal("OkDraft", response["status"]!.GetValue<string>());
            var sheet = JsonNode.Parse(File.ReadAllText(output))!;
            var samples = (JsonArray)sheet["events"]![0]!["suggested_candidates"]!;
            Assert.Single(samples);
            Assert.Equal(allowed, samples[0]!["path"]!.GetValue<string>());
            Assert.Equal("no_rights_record", samples[0]!["license_status"]!.GetValue<string>());
            Assert.False(samples[0]!["commercial_rights_cleared"]!.GetValue<bool>());
            Assert.Equal(1, sheet["excluded_by_reason"]!["USER_DECLARED_NONCOMMERCIAL_OR_NO_DERIVATIVES"]!.GetValue<int>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FOLEY20_InvalidDurationDoesNotCreateOutput()
    {
        var root = MakeRoot();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Doors")).FullName;
            Sample(folder, "door_open.wav");
            var review = WriteReview(root, opening: true, closing: false);
            var output = Path.Combine(root, "no-output.json");
            var tool = new SteinbergFoleySampleTools();
            Assert.Equal("INVALID_DURATION", tool.Recommend(review, folder, output,
                max_duration_seconds: double.NaN)["code"]!.GetValue<string>());
            Assert.Equal("INVALID_DURATION", tool.Recommend(review, folder, output,
                max_duration_seconds: 0.0)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
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

    private static string Sample(string directory, string name, int seconds = 1)
    {
        // Valid silent PCM RIFF/WAV fixture: 8kHz mono, 16-bit, 1..N seconds.
        // This tests header integrity and length, not actual audio listening.
        var path = Path.Combine(directory, name);
        var bytes = new byte[44 + 16000 * seconds];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        "WAVE"u8.CopyTo(bytes.AsSpan(8, 4));
        "fmt "u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), checked((uint)(bytes.Length - 44)));
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
