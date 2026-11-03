using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergFoleyPreimportTests
{
    [Fact]
    public void FOLEY27_McpToolContractAndReviewOnlyPreimportReport()
    {
        var type = typeof(SteinbergFoleyPreimportTools);
        var tool = McpServerTool.Create(type.GetMethod(nameof(SteinbergFoleyPreimportTools.Check))!, type);
        Assert.Equal("steinberg_foley_preimport_check", tool.ProtocolTool.Name);
        foreach (var field in new[] { "pre_daw_manifest", "timing_review_json", "output_report" })
            Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty(field, out _), field);

        using var f = new Fixture();
        var result = f.Tool.Check(f.PreDaw, f.Timing, f.Out);
        Assert.Equal("OkFileOnlyPreimport", result["status"]!.GetValue<string>());
        var report = JsonNode.Parse(File.ReadAllText(f.Out))!;
        Assert.Equal("PASS_FILE_ONLY_READY_FOR_MANUAL_DAW_REVIEW", report["status"]!.GetValue<string>());
        Assert.Equal("FOLEY_DOOR_OPEN", report["entries"]![0]!["track_name_reviewed"]!.GetValue<string>());
        Assert.Equal("00:00:02:00", report["entries"]![0]!["timecode_from_video_start"]!.GetValue<string>());
        Assert.Equal(2.0, report["entries"]![0]!["seconds_from_video_start"]!.GetValue<double>(), 9);
        Assert.Equal(f.AudioHash, report["entries"]![0]!["sample_sha256_revalidated"]!.GetValue<string>());
        Assert.Equal(f.VideoHash, report["verified_local_video_sha256"]!.GetValue<string>());
        Assert.False(report["video_fps_probed"]!.GetValue<bool>());
        Assert.False(report["media_window_to_full_video_mapping_automatically_verified"]!.GetValue<bool>());
        Assert.False(report["automatic_daw_import"]!.GetValue<bool>());
        Assert.Equal("NOT_RUN", report["actual_cubase_nuendo_import"]!.GetValue<string>());
        Assert.Equal("none", report["host_action"]!.GetValue<string>());
        Assert.False(result["host_modified"]!.GetValue<bool>());
        Assert.Equal("Error", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["status"]!.GetValue<string>());
    }

    [Fact]
    public void FOLEY28_AccurateNtscDropFrameLabelsWithoutClaimingProbedFps()
    {
        using var f = new Fixture();
        f.EditTiming(node =>
        {
            node["video"]!["fps_numerator"] = 30000;
            node["video"]!["fps_denominator"] = 1001;
            node["video"]!["total_frames"] = 200000;
            node["video"]!["timecode_mode"] = "drop_frame";
            node["assignments"]![0]!["event_frame"] = 1800L;
        });
        var result = f.Tool.Check(f.PreDaw, f.Timing, f.Out);
        Assert.Equal("OkFileOnlyPreimport", result["status"]!.GetValue<string>());
        var report = JsonNode.Parse(File.ReadAllText(f.Out))!;
        Assert.Equal("00:01:00;02", report["entries"]![0]!["timecode_from_video_start"]!.GetValue<string>());
        Assert.Equal(60.06, report["entries"]![0]!["seconds_from_video_start"]!.GetValue<double>(), 6);
        Assert.False(report["video_fps_probed"]!.GetValue<bool>());
        Assert.Equal("00:10:00;00", SteinbergFoleyPreimportTools.FrameToTimecode(17982, 30, true));
        Assert.Equal("01:00:00;00", SteinbergFoleyPreimportTools.FrameToTimecode(107892, 30, true));
        Assert.Equal("00:01:00;04", SteinbergFoleyPreimportTools.FrameToTimecode(3600, 60, true));
        Assert.Equal("00:01:00:00", SteinbergFoleyPreimportTools.FrameToTimecode(1800, 30, false));
    }

    [Fact]
    public void FOLEY29_ChangedWavBytesBlockOutputWithoutRelyingOnHeaderChanges()
    {
        using var f = new Fixture();
        var bytes = File.ReadAllBytes(f.Audio);
        bytes[72] ^= 0x45; // intact RIFF header and duration, wrong content SHA
        File.WriteAllBytes(f.Audio, bytes);
        Assert.Equal("SAMPLE_CHANGED_OR_INVALID", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    [Fact]
    public void FOLEY30_ChangedSourceVideoIsBlockedEvenWithPlausibleTimestamp()
    {
        using var f = new Fixture();
        File.AppendAllText(f.Video, "one changed video byte");
        Assert.Equal("VIDEO_SHA_MISMATCH", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    [Fact]
    public void FOLEY31_TrackDoesNotMatchDirectionalDoorAction()
    {
        using var f = new Fixture();
        f.EditTiming(x => x["assignments"]![0]!["track_name"] = "FOLEY_DOOR_CLOSE");
        Assert.Equal("TRACK_DIRECTION_MISMATCH", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    [Fact]
    public void FOLEY32_MissingApprovalOrWrongSourceWindowShaFailsClosed()
    {
        using var f = new Fixture();
        f.EditTiming(x => x["assignments"]![0]!["human_confirmed_video_frame"] = false);
        Assert.Equal("TIMING_NOT_APPROVED", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
        f.EditTiming(x =>
        {
            x["assignments"]![0]!["human_confirmed_video_frame"] = true;
            x["assignments"]![0]!["reviewed_window_sha256"] = new string('f', 64);
        });
        Assert.Equal("SOURCE_PROVENANCE_MISMATCH", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    [Fact]
    public void FOLEY33_UnsupportedFpsDropModeAndOutOfRangeFramesFailClosed()
    {
        using var f = new Fixture();
        f.EditTiming(x => x["video"]!["timecode_mode"] = "drop_frame");
        Assert.Equal("UNSUPPORTED_TIMECODE", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        f.EditTiming(x =>
        {
            x["video"]!["timecode_mode"] = "non_drop_frame";
            x["assignments"]![0]!["event_frame"] = 10000L;
        });
        Assert.Equal("TIMING_NOT_APPROVED", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    [Fact]
    public void FOLEY34_TimingReviewMustBindToExactOriginalPreDawManifestBytes()
    {
        using var f = new Fixture();
        File.AppendAllText(f.PreDaw, " ");
        Assert.Equal("INVALID_TIMING_REVIEW", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    [Fact]
    public void FOLEY35_SourceEventMustNotPretendToHaveDawPlacementApproval()
    {
        using var f = new Fixture();
        var pre = JsonNode.Parse(File.ReadAllText(f.PreDaw))!;
        pre["entries"]![0]!["daw_placement_approved"] = true;
        File.WriteAllText(f.PreDaw, pre.ToJsonString());
        f.RefreshPreDawHash();
        Assert.Equal("INVALID_AUDIO_ENTRY", f.Tool.Check(f.PreDaw, f.Timing, f.Out)["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Out));
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "steinberg-foley-preimport-" + Guid.NewGuid().ToString("N"));
        public string PreDaw => Path.Combine(Folder, "approved.json");
        public string Timing => Path.Combine(Folder, "timing.json");
        public string Out => Path.Combine(Folder, "preflight.json");
        public string Audio => Path.Combine(Folder, "door_open.wav");
        public string Video => Path.Combine(Folder, "video.mp4");
        public string AudioHash => Hash(Audio);
        public string VideoHash => Hash(Video);
        public SteinbergFoleyPreimportTools Tool { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(Folder);
            var wav = new byte[16044];
            "RIFF"u8.CopyTo(wav.AsSpan());
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, 4), 16036);
            "WAVE"u8.CopyTo(wav.AsSpan(8));
            "fmt "u8.CopyTo(wav.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, 4), 8000);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, 4), 16000);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 16);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), 16000);
            File.WriteAllBytes(Audio, wav);
            // Deliberately opaque fixture: validator hashes source video bytes but
            // does not pretend to decode or measure stream FPS.
            File.WriteAllBytes(Video, "video-integrity-only"u8.ToArray());
            var pre = new JsonObject
            {
                ["schema"] = "steinberg-foley-pre-daw-hashlocked-v1",
                ["status"] = "human_attested_samples_pending_daw_timing_review",
                ["host_action"] = "none",
                ["automatic_placement"] = false,
                ["sound_event_timing"] = "NOT_VERIFIED",
                ["approved_audio_count"] = 1,
                ["library_root"] = Folder,
                ["entries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["source_event_id"] = "mine_door_at_0",
                        ["proposed_action_unconfirmed"] = "opening_door",
                        ["sample_sha256"] = AudioHash,
                        ["sample_path"] = Audio,
                        ["relative_path"] = "door_open.wav",
                        ["sample_size_bytes"] = wav.Length,
                        ["duration_seconds_header"] = 1.0,
                        ["sample_rate_hz_header"] = 8000,
                        ["channels_header"] = 1,
                        ["bit_depth_header"] = 16,
                        ["video_sha256"] = new string('a', 64), // distinct reviewed 2s window
                        ["contact_sha256"] = new string('b', 64),
                        ["audio_integrity_sha256_verified"] = true,
                        ["daw_placement_approved"] = false,
                        ["action_timing_verified"] = false,
                        ["host_action"] = "none"
                    }
                }
            };
            File.WriteAllText(PreDaw, pre.ToJsonString());
            var timing = new JsonObject
            {
                ["schema"] = "steinberg-foley-human-timing-review-v1",
                ["pre_daw_manifest_sha256"] = Hash(PreDaw),
                ["host_action"] = "none",
                ["video"] = new JsonObject
                {
                    ["path"] = Video,
                    ["sha256"] = VideoHash,
                    ["fps_numerator"] = 30,
                    ["fps_denominator"] = 1,
                    ["total_frames"] = 600L,
                    ["timecode_mode"] = "non_drop_frame"
                },
                ["assignments"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["source_event_id"] = "mine_door_at_0",
                        ["sample_sha256"] = AudioHash,
                        ["reviewed_window_sha256"] = new string('a', 64),
                        ["track_name"] = "FOLEY_DOOR_OPEN",
                        ["event_frame"] = 60L,
                        ["duration_frames"] = 12L,
                        ["human_confirmed_video_frame"] = true,
                        ["human_confirmed_track"] = true,
                        ["human_confirmed_source_window_to_video_mapping"] = true,
                        ["reviewer_note"] = "Person manually established frame offset and track; test only"
                    }
                }
            };
            File.WriteAllText(Timing, timing.ToJsonString());
        }

        public void RefreshPreDawHash() =>
            EditTiming(t => t["pre_daw_manifest_sha256"] = Hash(PreDaw));

        public void EditTiming(Action<JsonNode> edit)
        {
            var node = JsonNode.Parse(File.ReadAllText(Timing))!;
            edit(node);
            File.WriteAllText(Timing, node.ToJsonString());
        }

        public void Dispose() => Directory.Delete(Folder, true);
        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
