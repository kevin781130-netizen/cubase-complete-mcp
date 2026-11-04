using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Linq;
using CubaseCompleteMcp.Server;

namespace CubaseCompleteMcp.Server.Tests;

/// <summary>
/// Full synthetic *file-only* workflow: model output -> candidate list ->
/// sample review -> explicitly scripted approval -> human timing fixture ->
/// preimport gate -> portable DAWproject and XML/media readback.
/// Synthetic approvals DO NOT establish human listening, legal rights, real
/// video timing, real model accuracy or host import.
/// </summary>
public sealed class SteinbergFoleyPipelineE2ETests
{
    [Fact]
    public void SteinbergFoleyPipeline01_TwoEventsReachXsdValidHashMatchedDawproject()
    {
        using var fixture = new Fixture();
        fixture.BuildApprovedPipeline();

        var checkedResult = new SteinbergFoleyPreimportTools().Check(
            fixture.PreDaw, fixture.Timing, fixture.PreimportReport);
        Assert.Equal("OkFileOnlyPreimport", checkedResult["status"]!.GetValue<string>());
        var checkedDocument = JsonNode.Parse(File.ReadAllText(fixture.PreimportReport))!;
        Assert.Equal("PASS_FILE_ONLY_READY_FOR_MANUAL_DAW_REVIEW",
            checkedDocument["status"]!.GetValue<string>());
        Assert.False(checkedDocument["video_fps_probed"]!.GetValue<bool>());
        Assert.Equal("NOT_RUN", checkedDocument["actual_cubase_nuendo_import"]!.GetValue<string>());
        Assert.Equal(2, checkedDocument["event_count"]!.GetValue<int>());

        var exported = new SteinbergFoleyDawprojectTools().Export(
            fixture.PreDaw, fixture.Timing, fixture.ExportPath, project_bpm: 120, approved: true);
        Assert.Equal("OkPortableExport", exported["status"]!.GetValue<string>());
        Assert.Equal(2, exported["tracks"]!.GetValue<int>());
        Assert.Equal(2, exported["clips"]!.GetValue<int>());
        Assert.Equal(2, exported["audio_files_embedded"]!.GetValue<int>());
        Assert.False(exported["host_modified"]!.GetValue<bool>());
        Assert.Equal("NOT_RUN", exported["actual_cubase_nuendo_import"]!.GetValue<string>());

        using var zip = ZipFile.OpenRead(fixture.ExportPath);
        Assert.Equal(4, zip.Entries.Count);
        ValidateXsd(zip, "project.xml", "Project.xsd");
        ValidateXsd(zip, "metadata.xml", "MetaData.xsd");
        var xml = XDocument.Load(zip.GetEntry("project.xml")!.Open());
        Assert.Equal("seconds", xml.Descendants("Lanes").First().Attribute("timeUnit")!.Value);
        var lanes = xml.Descendants("Lanes")
            .Where(e => e.Attribute("track") is not null).ToArray();
        Assert.Equal(2, lanes.Length);
        var tracks = xml.Descendants("Track")
            .Where(t => (string?)t.Attribute("name") is "FOLEY_DOOR_OPEN" or "FOLEY_DOOR_CLOSE")
            .ToDictionary(t => (string)t.Attribute("id")!, t => (string)t.Attribute("name")!);
        Assert.Equal(2, tracks.Count);
        var positions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            var track = tracks[(string)lane.Attribute("track")!];
            var clip = Assert.Single(lane.Descendants("Clip"));
            positions[track] = double.Parse((string)clip.Attribute("time")!, CultureInfo.InvariantCulture);
            Assert.Equal(1.0, double.Parse((string)clip.Attribute("duration")!, CultureInfo.InvariantCulture), 8);
        }
        Assert.Equal(60.06, positions["FOLEY_DOOR_OPEN"], 8);
        Assert.Equal(120.12, positions["FOLEY_DOOR_CLOSE"], 8);
        foreach (var source in new[] { fixture.OpenWav, fixture.CloseWav })
        {
            var sha = Sha(source);
            var embedded = zip.GetEntry("audio/" + sha + ".wav");
            Assert.NotNull(embedded);
            using var stream = embedded!.Open();
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
    }

    [Fact]
    public void SteinbergFoleyPipeline02_SourceVideoTamperAfterApprovalRefusesBothGates()
    {
        using var fixture = new Fixture();
        fixture.BuildApprovedPipeline();
        File.AppendAllText(fixture.VideoPath, "changed-after-timing-review");
        var checkedResult = new SteinbergFoleyPreimportTools().Check(
            fixture.PreDaw, fixture.Timing, fixture.PreimportReport);
        Assert.Equal("VIDEO_SHA_MISMATCH", checkedResult["code"]!.GetValue<string>());
        Assert.False(File.Exists(fixture.PreimportReport));

        var exported = new SteinbergFoleyDawprojectTools().Export(
            fixture.PreDaw, fixture.Timing, fixture.ExportPath, project_bpm: 120, approved: true);
        Assert.Equal("PREIMPORT_BLOCKED", exported["code"]!.GetValue<string>());
        Assert.False(File.Exists(fixture.ExportPath));
    }

    private static void ValidateXsd(ZipArchive zip, string entry, string schema)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "DAWprojectSchema", schema);
        Assert.True(File.Exists(file), "Pinned XSD missing: " + file);
        var options = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            ValidationType = ValidationType.Schema
        };
        options.Schemas.Add(null, file);
        using var zipStream = zip.GetEntry(entry)!.Open();
        using var reader = XmlReader.Create(zipStream, options);
        while (reader.Read()) { }
    }

    private static string Sha(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory =
            Path.Combine(Path.GetTempPath(), "steinberg-foley-pipeline-" + Guid.NewGuid().ToString("N"));
        public string OpenWav => Path.Combine(directory, "wood_door_open.wav");
        public string CloseWav => Path.Combine(directory, "metal_door_close.wav");
        public string VideoPath => Path.Combine(directory, "full-video.mp4");
        public string PreDaw => Path.Combine(directory, "pre-daw.json");
        public string Timing => Path.Combine(directory, "timing.json");
        public string PreimportReport => Path.Combine(directory, "preimport.json");
        public string ExportPath => Path.Combine(directory, "reviewed.dawproject");

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            CreateWav(OpenWav);
            CreateWav(CloseWav);
            // Opaque synthetic video bytes: identity is tested, FPS is NOT decoded.
            File.WriteAllBytes(VideoPath, "synthetic-source-video-not-decodable"u8.ToArray());
        }

        public void BuildApprovedPipeline()
        {
            var modelReport = Path.Combine(directory, "reviewed-model.json");
            File.WriteAllText(modelReport, new JsonObject
            {
                ["status"] = "PASS_REAL_BOTH_MODELS_FOUR_REVIEWED_VIDEO_WINDOWS_NOT_ACCURACY_VALIDATED",
                ["door_roi_motion_experimental"] = new JsonObject
                {
                    ["status"] = "REVIEW_ONLY_NOT_DOOR_GEOMETRY_VALIDATED",
                    ["motion_gated_rows"] = new JsonArray
                    {
                        ModelRow("open-event", "opening_door"),
                        ModelRow("close-event", "closing_door"),
                        ModelRow("abstained-event", null)
                    }
                }
            }.ToJsonString());
            var candidates = Path.Combine(directory, "candidates.json");
            Assert.Equal("OkDraft", new SteinbergFoleySampleTools().Recommend(
                modelReport, directory, candidates, max_per_event: 1)["status"]!.GetValue<string>());
            var candidateDocument = JsonNode.Parse(File.ReadAllText(candidates))!;
            Assert.Equal(2, candidateDocument["event_count"]!.GetValue<int>());
            Assert.Empty((JsonArray)candidateDocument["selected_audio_files"]!);
            foreach (var evt in (JsonArray)candidateDocument["events"]!)
            {
                var picks = evt!["suggested_candidates"] as JsonArray;
                Assert.True(picks is { Count: > 0 }, "No candidate for event: " + evt.ToJsonString());
                Assert.False(picks[0]!["commercial_rights_cleared"]!.GetValue<bool>());
            }

            var review = Path.Combine(directory, "sample-review.json");
            var approvals = new SteinbergFoleyApprovalTools();
            var prepared = approvals.Prepare(candidates, review);
            Assert.Equal("OkReviewPending", prepared["status"]!.GetValue<string>());
            var reviewDocument = JsonNode.Parse(File.ReadAllText(review))!;
            var approvalsArray = new JsonArray();
            foreach (var evt in (JsonArray)reviewDocument["events"]!)
            {
                var option = evt!["choices"]![0]!;
                approvalsArray.Add(new JsonObject
                {
                    ["source_event_id"] = evt["source_event_id"]!.GetValue<string>(),
                    ["relative_path"] = option["relative_path"]!.GetValue<string>(),
                    ["sample_sha256"] = option["sample_sha256"]!.GetValue<string>(),
                    ["listened_to_sample"] = true,
                    ["confirmed_matching_action"] = true,
                    ["reviewed_rights_for_intended_use"] = true,
                    ["approved_for_pre_daw_review_only"] = true,
                    ["reviewer_note"] = "SYNTHETIC TEST FLAGS ONLY: no human listened or licensed the sample."
                });
            }
            var approvalsFile = Path.Combine(directory, "scripted-approvals.json");
            File.WriteAllText(approvalsFile, new JsonObject
            {
                ["schema"] = "steinberg-foley-explicit-approval-v1",
                ["review_sha256"] = prepared["review_sha256"]!.GetValue<string>(),
                ["approvals"] = approvalsArray
            }.ToJsonString());

            var finalized = approvals.Finalize(review, approvalsFile, PreDaw);
            Assert.Equal("OkPreDawReview", finalized["status"]!.GetValue<string>());
            var pre = JsonNode.Parse(File.ReadAllText(PreDaw))!;
            Assert.Equal(2, pre["approved_audio_count"]!.GetValue<int>());
            Assert.Equal("human_attestation_not_independently_verified",
                pre["license_status"]!.GetValue<string>());

            var assigned = new JsonArray();
            foreach (var entry in (JsonArray)pre["entries"]!)
            {
                var isOpen = entry!["proposed_action_unconfirmed"]!.GetValue<string>() == "opening_door";
                assigned.Add(new JsonObject
                {
                    ["source_event_id"] = entry["source_event_id"]!.GetValue<string>(),
                    ["sample_sha256"] = entry["sample_sha256"]!.GetValue<string>(),
                    ["reviewed_window_sha256"] = entry["video_sha256"]!.GetValue<string>(),
                    ["track_name"] = isOpen ? "FOLEY_DOOR_OPEN" : "FOLEY_DOOR_CLOSE",
                    ["event_frame"] = isOpen ? 1800L : 3600L,
                    ["duration_frames"] = 12L,
                    ["human_confirmed_video_frame"] = true,
                    ["human_confirmed_track"] = true,
                    ["human_confirmed_source_window_to_video_mapping"] = true,
                    ["reviewer_note"] = "SYNTHETIC TEST: scripted frame/track assertion only."
                });
            }
            File.WriteAllText(Timing, new JsonObject
            {
                ["schema"] = "steinberg-foley-human-timing-review-v1",
                ["pre_daw_manifest_sha256"] = Sha(PreDaw),
                ["host_action"] = "none",
                ["video"] = new JsonObject
                {
                    ["path"] = VideoPath,
                    ["sha256"] = Sha(VideoPath),
                    ["fps_numerator"] = 30000,
                    ["fps_denominator"] = 1001,
                    ["total_frames"] = 9000L,
                    ["timecode_mode"] = "drop_frame"
                },
                ["assignments"] = assigned
            }.ToJsonString());
        }

        private static JsonObject ModelRow(string id, string? action) => new()
        {
            ["id"] = id,
            ["status"] = action is null ? "abstained" : "review_proposal_not_confirmed",
            ["proposed_door_action"] = action,
            ["door_panel_geometry_verified"] = false,
            ["requires_human_review"] = true,
            ["video_sha256"] = new string('a', 64),
            ["contact_sha256"] = new string('b', 64),
            ["host_action"] = "none",
            ["selected_audio_file"] = null
        };

        private static void CreateWav(string path)
        {
            var bytes = new byte[16044];
            "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 16036);
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
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 16000);
            // Give the two silent fixtures different content hashes to verify ZIP media count.
            if (Path.GetFileName(path).Contains("close", StringComparison.OrdinalIgnoreCase))
                bytes[128] = 0x37;
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
