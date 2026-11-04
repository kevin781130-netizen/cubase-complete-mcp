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

public sealed class SteinbergFoleyDawprojectTests
{
    [Fact]
    public void FOLEY_EXPORT01_ReviewedFrameProducesSchemaValidSecondsTimeline()
    {
        using var f = new Fixture();
        f.SetFps30000Over1001();
        var result = f.Export();
        Assert.Equal("OkPortableExport", result["status"]!.GetValue<string>());
        Assert.False(result["host_modified"]!.GetValue<bool>());
        Assert.Equal("NOT_RUN", result["actual_cubase_nuendo_import"]!.GetValue<string>());
        Assert.Equal(f.Hash(f.Pre), result["pre_daw_sha256"]!.GetValue<string>());
        using var zip = ZipFile.OpenRead(f.Output);
        Validate(zip, "project.xml", "Project.xsd");
        Validate(zip, "metadata.xml", "MetaData.xsd");
        var project = XDocument.Load(zip.GetEntry("project.xml")!.Open());
        Assert.Equal("seconds", project.Descendants("Lanes").First().Attribute("timeUnit")!.Value);
        var clip = project.Descendants("Clip").Single();
        Assert.Equal(60.06, double.Parse(clip.Attribute("time")!.Value, CultureInfo.InvariantCulture), 8);
        Assert.Equal(1.0, double.Parse(clip.Attribute("duration")!.Value, CultureInfo.InvariantCulture), 8);
        Assert.Contains(project.Descendants("Track"), t => (string?)t.Attribute("name") == "FOLEY_DOOR_OPEN");
        Assert.Single(zip.Entries, x => x.FullName.StartsWith("audio/", StringComparison.Ordinal));
    }

    [Fact]
    public void FOLEY_EXPORT02_ExplicitApprovalIsMandatoryAndNoFilesAreWritten()
    {
        using var f = new Fixture();
        var result = f.Export(approved: false);
        Assert.Equal("APPROVAL_REQUIRED", result["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Output));
    }

    [Fact]
    public void FOLEY_EXPORT03_TwoDirectionalTracksDeduplicateApprovedWav()
    {
        using var f = new Fixture();
        var pre = JsonNode.Parse(File.ReadAllText(f.Pre))!;
        var additional = pre["entries"]![0]!.DeepClone();
        additional["source_event_id"] = "door_close_later";
        additional["proposed_action_unconfirmed"] = "closing_door";
        pre["entries"]!.AsArray().Add(additional);
        pre["approved_audio_count"] = 2;
        File.WriteAllText(f.Pre, pre.ToJsonString());
        var timing = JsonNode.Parse(File.ReadAllText(f.Timing))!;
        timing["pre_daw_manifest_sha256"] = f.Hash(f.Pre);
        var assignment = timing["assignments"]![0]!.DeepClone();
        assignment["source_event_id"] = "door_close_later";
        assignment["track_name"] = "FOLEY_DOOR_CLOSE";
        assignment["event_frame"] = 90L;
        timing["assignments"]!.AsArray().Add(assignment);
        File.WriteAllText(f.Timing, timing.ToJsonString());

        var result = f.Export();
        Assert.Equal("OkPortableExport", result["status"]!.GetValue<string>());
        Assert.Equal(2, result["clips"]!.GetValue<int>());
        Assert.Equal(2, result["tracks"]!.GetValue<int>());
        Assert.Equal(1, result["audio_files_embedded"]!.GetValue<int>());
        using var zip = ZipFile.OpenRead(f.Output);
        Validate(zip, "project.xml", "Project.xsd");
        var xml = XDocument.Load(zip.GetEntry("project.xml")!.Open());
        Assert.Contains(xml.Descendants("Track"), t => (string?)t.Attribute("name") == "FOLEY_DOOR_OPEN");
        Assert.Contains(xml.Descendants("Track"), t => (string?)t.Attribute("name") == "FOLEY_DOOR_CLOSE");
        Assert.Equal(2, xml.Descendants("Clip").Count());
        Assert.Single(zip.Entries, x => x.FullName.StartsWith("audio/", StringComparison.Ordinal));
    }

    [Fact]
    public void FOLEY_EXPORT04_ChangedTimingHashIsRefused()
    {
        using var f = new Fixture();
        var timing = JsonNode.Parse(File.ReadAllText(f.Timing))!;
        timing["pre_daw_manifest_sha256"] = new string('f', 64);
        File.WriteAllText(f.Timing, timing.ToJsonString());
        var result = f.Export();
        Assert.Equal("PREIMPORT_BLOCKED", result["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Output));
    }

    [Fact]
    public void FOLEY_EXPORT05_ChangedSampleAndExistingDestinationAreRefused()
    {
        using var f = new Fixture();
        File.AppendAllText(f.Wav, "tamper");
        var result = f.Export();
        Assert.Equal("PREIMPORT_BLOCKED", result["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Output));
        File.WriteAllText(f.Output, "preserve");
        var retry = f.Export();
        Assert.Equal("EXPORT_REJECTED", retry["code"]!.GetValue<string>());
        Assert.Equal("preserve", File.ReadAllText(f.Output));
    }

    [Fact]
    public void FOLEY_EXPORT06_InvalidBpmIsRefusedWithoutWriting()
    {
        using var f = new Fixture();
        var result = f.Export(bpm: 0);
        Assert.Equal("INVALID_BPM", result["code"]!.GetValue<string>());
        Assert.False(File.Exists(f.Output));
    }

    private static void Validate(ZipArchive zip, string name, string schemaName)
    {
        var schema = Path.Combine(AppContext.BaseDirectory, "DAWprojectSchema", schemaName);
        Assert.True(File.Exists(schema));
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, ValidationType = ValidationType.Schema };
        settings.Schemas.Add(null, schema);
        using var stream = zip.GetEntry(name)!.Open();
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read()) { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "steinberg-foley-export-" + Guid.NewGuid().ToString("N"));
        public string Pre => Path.Combine(root, "approved.json");
        public string Timing => Path.Combine(root, "timing.json");
        public string Wav => Path.Combine(root, "door.wav");
        public string Video => Path.Combine(root, "source.mp4");
        public string Output => Path.Combine(root, "review.dawproject");

        public Fixture()
        {
            Directory.CreateDirectory(root);
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
            File.WriteAllBytes(Wav, wav);
            File.WriteAllBytes(Video, "opaque-video-fixture"u8.ToArray());
            var pre = new JsonObject
            {
                ["schema"] = "steinberg-foley-pre-daw-hashlocked-v1",
                ["status"] = "human_attested_samples_pending_daw_timing_review",
                ["host_action"] = "none",
                ["automatic_placement"] = false,
                ["sound_event_timing"] = "NOT_VERIFIED",
                ["approved_audio_count"] = 1,
                ["library_root"] = root,
                ["entries"] = new JsonArray { new JsonObject
                {
                    ["source_event_id"] = "door_open",
                    ["proposed_action_unconfirmed"] = "opening_door",
                    ["sample_path"] = Wav,
                    ["relative_path"] = "door.wav",
                    ["sample_sha256"] = Hash(Wav),
                    ["sample_size_bytes"] = wav.Length,
                    ["sample_rate_hz_header"] = 8000,
                    ["channels_header"] = 1,
                    ["bit_depth_header"] = 16,
                    ["duration_seconds_header"] = 1.0,
                    ["video_sha256"] = new string('a', 64),
                    ["contact_sha256"] = new string('b', 64),
                    ["audio_integrity_sha256_verified"] = true,
                    ["action_timing_verified"] = false,
                    ["daw_placement_approved"] = false,
                    ["host_action"] = "none"
                }}
            };
            File.WriteAllText(Pre, pre.ToJsonString());
            var timing = new JsonObject
            {
                ["schema"] = "steinberg-foley-human-timing-review-v1",
                ["pre_daw_manifest_sha256"] = Hash(Pre),
                ["host_action"] = "none",
                ["video"] = new JsonObject
                {
                    ["path"] = Video, ["sha256"] = Hash(Video),
                    ["fps_numerator"] = 30, ["fps_denominator"] = 1,
                    ["total_frames"] = 6000L, ["timecode_mode"] = "non_drop_frame"
                },
                ["assignments"] = new JsonArray { new JsonObject
                {
                    ["source_event_id"] = "door_open",
                    ["sample_sha256"] = Hash(Wav),
                    ["reviewed_window_sha256"] = new string('a', 64),
                    ["track_name"] = "FOLEY_DOOR_OPEN",
                    ["event_frame"] = 60L,
                    ["duration_frames"] = 12L,
                    ["human_confirmed_video_frame"] = true,
                    ["human_confirmed_track"] = true,
                    ["human_confirmed_source_window_to_video_mapping"] = true,
                    ["reviewer_note"] = "Reviewed source-frame mapping and timeline, synthetic offline fixture"
                }}
            };
            File.WriteAllText(Timing, timing.ToJsonString());
        }

        public void SetFps30000Over1001()
        {
            var doc = JsonNode.Parse(File.ReadAllText(Timing))!;
            doc["video"]!["fps_numerator"] = 30000;
            doc["video"]!["fps_denominator"] = 1001;
            doc["video"]!["timecode_mode"] = "drop_frame";
            doc["assignments"]![0]!["event_frame"] = 1800L;
            File.WriteAllText(Timing, doc.ToJsonString());
        }

        public JsonNode Export(bool approved = true, double bpm = 120) =>
            new SteinbergFoleyDawprojectTools().Export(Pre, Timing, Output, bpm, approved);

        public string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
