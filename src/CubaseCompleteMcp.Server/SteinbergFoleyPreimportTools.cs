using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Preimport-only validator for a human-attested Foley sample manifest plus
/// explicit video timing and track assignments. Does not inspect a media
/// stream's frame rate and never contacts Cubase, Nuendo, or any DAW broker.
/// </summary>
[McpServerToolType]
public sealed class SteinbergFoleyPreimportTools
{
    private const long MaxInputJson = 2 * 1024 * 1024;
    private const long MaxVideoBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxAudioBytes = 256L * 1024 * 1024;
    private static readonly Dictionary<string, string> ExpectedTracks = new(StringComparer.Ordinal)
    {
        ["opening_door"] = "FOLEY_DOOR_OPEN",
        ["closing_door"] = "FOLEY_DOOR_CLOSE"
    };

    [McpServerTool(Name = "steinberg_foley_preimport_check", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Validate a human-attested, SHA-256-locked Foley list against real local audio/video bytes, explicit rational FPS, reviewed event frames and strict Cubase/Nuendo door-track labels. Produces only a NEW preimport report; video FPS is user-declared, not probed; never opens, imports or modifies the DAW.")]
    public JsonNode Check(string pre_daw_manifest, string timing_review_json, string output_report)
    {
        try
        {
            var sourcePath = LocalJson(pre_daw_manifest);
            var timingPath = LocalJson(timing_review_json);
            var output = FreshJson(output_report);
            var preBytes = File.ReadAllBytes(sourcePath);
            var timingBytes = File.ReadAllBytes(timingPath);
            var pre = JsonNode.Parse(preBytes) as JsonObject;
            var timing = JsonNode.Parse(timingBytes) as JsonObject;
            if (pre?["schema"]?.GetValue<string>() != "steinberg-foley-pre-daw-hashlocked-v1" ||
                pre["status"]?.GetValue<string>() != "human_attested_samples_pending_daw_timing_review" ||
                pre["host_action"]?.GetValue<string>() != "none" ||
                pre["automatic_placement"]?.GetValue<bool>() != false ||
                pre["sound_event_timing"]?.GetValue<string>() != "NOT_VERIFIED" ||
                pre["entries"] is not JsonArray entries || entries.Count is < 1 or > 8 ||
                pre["approved_audio_count"]?.GetValue<int>() != entries.Count)
                return Error("INVALID_PRE_DAW_MANIFEST", "Expected a bounded, human-attested, no-host-action audio manifest.");

            if (timing?["schema"]?.GetValue<string>() != "steinberg-foley-human-timing-review-v1" ||
                timing["pre_daw_manifest_sha256"]?.GetValue<string>() != Sha(preBytes) ||
                timing["host_action"]?.GetValue<string>() != "none" ||
                timing["video"] is not JsonObject video ||
                timing["assignments"] is not JsonArray assignments ||
                assignments.Count != entries.Count)
                return Error("INVALID_TIMING_REVIEW", "Timing review must bind to the exact pre-DAW manifest SHA-256 and cover every approved event.");

            var audioRoot = LocalRoot(pre["library_root"]?.GetValue<string>());
            var fpsN = ReadInt(video, "fps_numerator");
            var fpsD = ReadInt(video, "fps_denominator");
            var totalFrames = ReadLong(video, "total_frames");
            var mode = video["timecode_mode"]?.GetValue<string>();
            var nominalFps = NominalRate(fpsN, fpsD);
            if (nominalFps == 0 || totalFrames is < 1 or > 20_000_000 ||
                mode is not ("non_drop_frame" or "drop_frame") ||
                (mode == "drop_frame" && !(fpsN == 30000 && fpsD == 1001 ||
                                             fpsN == 60000 && fpsD == 1001)))
                return Error("UNSUPPORTED_TIMECODE", "Use an allowed exact/rational frame rate with explicit non-drop or NTSC drop-frame timecode.");

            var sourceVideo = LocalRegularFile(video["path"]?.GetValue<string>(), MaxVideoBytes);
            if (!new[] { ".mov", ".mp4", ".mkv", ".webm" }.Contains(
                Path.GetExtension(sourceVideo), StringComparer.OrdinalIgnoreCase))
                return Error("UNSUPPORTED_VIDEO_CONTAINER", "A regular local .mov/.mp4/.mkv/.webm reference is required.");
            var expectedVideoHash = video["sha256"]?.GetValue<string>();
            if (!HashText(expectedVideoHash) || HashFile(sourceVideo) != expectedVideoHash)
                return Error("VIDEO_SHA_MISMATCH", "Actual local source video bytes differ from the declared SHA-256.");

            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var node in entries)
            {
                if (node is not JsonObject entry)
                    return Error("INVALID_AUDIO_ENTRY", "Audio entry must be a JSON object.");
                var id = entry["source_event_id"]?.GetValue<string>();
                var action = entry["proposed_action_unconfirmed"]?.GetValue<string>();
                var hash = entry["sample_sha256"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !byId.TryAdd(id, entry) ||
                    !ExpectedTracks.ContainsKey(action ?? "") || !HashText(hash) ||
                    !HashText(entry["video_sha256"]?.GetValue<string>()) ||
                    !HashText(entry["contact_sha256"]?.GetValue<string>()) ||
                    entry["audio_integrity_sha256_verified"]?.GetValue<bool>() != true ||
                    entry["daw_placement_approved"]?.GetValue<bool>() != false ||
                    entry["action_timing_verified"]?.GetValue<bool>() != false ||
                    entry["host_action"]?.GetValue<string>() != "none")
                    return Error("INVALID_AUDIO_ENTRY", "Every entry must preserve unconfirmed timing and reviewed SHA-256 provenance.");
            }

            var rows = new JsonArray();
            var approved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in assignments)
            {
                if (node is not JsonObject assignment)
                    return Error("INVALID_TIMING_ASSIGNMENT", "A timing assignment must be an object.");
                var id = assignment["source_event_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || !approved.Add(id) ||
                    !byId.TryGetValue(id, out var entry))
                    return Error("INVALID_TIMING_ASSIGNMENT", "Unknown or repeated Foley event.");
                var action = entry["proposed_action_unconfirmed"]!.GetValue<string>();
                var track = assignment["track_name"]?.GetValue<string>();
                if (track != ExpectedTracks[action])
                    return Error("TRACK_DIRECTION_MISMATCH", "Door action and Cubase/Nuendo track must match the fixed mapping.");

                var clipHash = assignment["reviewed_window_sha256"]?.GetValue<string>();
                var sampleHash = assignment["sample_sha256"]?.GetValue<string>();
                if (clipHash != entry["video_sha256"]?.GetValue<string>() ||
                    sampleHash != entry["sample_sha256"]?.GetValue<string>())
                    return Error("SOURCE_PROVENANCE_MISMATCH", "Timing and approved sample must refer to the original model window and WAV SHA.");

                var frame = ReadLong(assignment, "event_frame");
                var durationFrames = ReadLong(assignment, "duration_frames");
                var note = assignment["reviewer_note"]?.GetValue<string>();
                if (frame < 0 || frame >= totalFrames ||
                    durationFrames < 0 || durationFrames > totalFrames - frame ||
                    assignment["human_confirmed_video_frame"]?.GetValue<bool>() != true ||
                    assignment["human_confirmed_track"]?.GetValue<bool>() != true ||
                    assignment["human_confirmed_source_window_to_video_mapping"]?.GetValue<bool>() != true ||
                    string.IsNullOrWhiteSpace(note) || note.Length > 300)
                    return Error("TIMING_NOT_APPROVED", "Human must verify the frame, track, source-window mapping and note; all frames must be in range.");

                var sample = BoundAudio(audioRoot, entry["relative_path"]?.GetValue<string>(),
                    entry["sample_path"]?.GetValue<string>());
                var actualSize = new FileInfo(sample).Length;
                if (actualSize != entry["sample_size_bytes"]?.GetValue<long>() ||
                    HashFile(sample) != sampleHash ||
                    !SteinbergFoleyAudioProbe.TryProbe(sample, out var wav, out _) || wav is null ||
                    wav.SampleRate != entry["sample_rate_hz_header"]?.GetValue<int>() ||
                    wav.Channels != entry["channels_header"]?.GetValue<int>() ||
                    wav.BitsPerSample != entry["bit_depth_header"]?.GetValue<int>() ||
                    Math.Abs(wav.DurationSeconds - entry["duration_seconds_header"]!.GetValue<double>()) > 0.000001)
                    return Error("SAMPLE_CHANGED_OR_INVALID", "Preapproved WAV content, size or technical format changed.");

                var seconds = Math.Round((double)frame * fpsD / fpsN, 9);
                var durationSeconds = Math.Round((double)durationFrames * fpsD / fpsN, 9);
                rows.Add(new JsonObject
                {
                    ["source_event_id"] = id,
                    ["unverified_action_label"] = action,
                    ["track_name_reviewed"] = track,
                    ["event_frame"] = frame,
                    ["duration_frames"] = durationFrames,
                    ["seconds_from_video_start"] = seconds,
                    ["duration_seconds"] = durationSeconds,
                    ["timecode_from_video_start"] = FrameToTimecode(frame, nominalFps, mode == "drop_frame"),
                    ["video_fps_provenance"] = "human_declared_not_measured_by_ffprobe",
                    ["video_source_sha256"] = expectedVideoHash,
                    ["reviewed_2s_window_sha256"] = clipHash,
                    ["source_window_to_full_video_mapping"] = "human_attested_not_automatically_verified",
                    ["sample_path"] = sample,
                    ["sample_sha256_revalidated"] = sampleHash,
                    ["sample_duration_seconds_header"] = Math.Round(wav.DurationSeconds, 6),
                    ["sample_audio_format"] = "RIFF/WAV",
                    ["audio_placement_executed"] = false,
                    ["human_review_note"] = note,
                    ["host_action"] = "none"
                });
            }
            if (approved.Count != entries.Count)
                return Error("MISSING_TIMING_ASSIGNMENT", "Every approved audio event requires one timing and track assignment.");

            var ordered = new JsonArray(rows.OrderBy(e => e!["event_frame"]!.GetValue<long>())
                .ThenBy(e => e!["source_event_id"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(e => e!.DeepClone()).ToArray());
            var report = new JsonObject
            {
                ["schema"] = "steinberg-foley-preimport-acceptance-v1",
                ["status"] = "PASS_FILE_ONLY_READY_FOR_MANUAL_DAW_REVIEW",
                ["source_pre_daw_sha256"] = Sha(preBytes),
                ["source_timing_review_sha256"] = Sha(timingBytes),
                ["verified_local_video_sha256"] = expectedVideoHash,
                ["fps_numerator_declared"] = fpsN,
                ["fps_denominator_declared"] = fpsD,
                ["video_total_frames_declared"] = totalFrames,
                ["timecode_mode"] = mode,
                ["timeline_origin"] = "00:00:00",
                ["video_fps_probed"] = false,
                ["media_window_to_full_video_mapping_automatically_verified"] = false,
                ["audio_sha256_rechecked"] = true,
                ["track_mapping"] = "fixed_FOLEY_DOOR_OPEN_or_FOLEY_DOOR_CLOSE",
                ["event_count"] = ordered.Count,
                ["entries"] = ordered,
                ["license_status"] = "human_attestation_not_independently_verified",
                ["automatic_daw_import"] = false,
                ["actual_cubase_nuendo_import"] = "NOT_RUN",
                ["host_action"] = "none",
                ["warning"] = "Preimport evidence only. FPS/frame count and onset are human-declared; FFprobe, actual DAW tracks, Cubase/Nuendo import, video timecode metadata and sound synchronization were NOT verified. Source clips are 2s model windows; mapping to full video is human-attested."
            };
            using (var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(outputStream, report, new JsonSerializerOptions { WriteIndented = true });
            return new JsonObject
            {
                ["status"] = "OkFileOnlyPreimport",
                ["report_path"] = output,
                ["event_count"] = ordered.Count,
                ["host_modified"] = false,
                ["cubase_nuendo_import"] = "NOT_RUN"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or JsonException or InvalidOperationException or NotSupportedException or OverflowException)
        { return Error("PREIMPORT_REJECTED", ex.Message); }
    }

    private static int NominalRate(int n, int d) =>
        (n, d) switch
        {
            (24, 1) or (24000, 1001) => 24,
            (25, 1) => 25,
            (30, 1) or (30000, 1001) => 30,
            (50, 1) => 50,
            (60, 1) or (60000, 1001) => 60,
            _ => 0
        };

    internal static string FrameToTimecode(long frame, int nominal, bool drop)
    {
        if (frame < 0 || nominal is not (24 or 25 or 30 or 50 or 60) ||
            (drop && nominal is not (30 or 60)))
            throw new ArgumentException("Unsupported SMPTE frame counting.");
        long tcFrame = frame;
        if (drop)
        {
            var skip = nominal == 30 ? 2 : 4;
            var perMinute = nominal * 60 - skip;
            var perTenMinutes = nominal * 600 - skip * 9;
            var per24Hours = (long)perTenMinutes * 6 * 24;
            var withinDay = frame % per24Hours;
            var tenMinuteGroups = withinDay / perTenMinutes;
            var remainder = withinDay % perTenMinutes;
            tcFrame = withinDay + skip * 9L * tenMinuteGroups;
            if (remainder >= skip)
                tcFrame += skip * ((remainder - skip) / perMinute);
        }
        var perSecond = (long)nominal;
        var wholeSeconds = tcFrame / perSecond;
        var ff = tcFrame % perSecond;
        var hh = wholeSeconds / 3600 % 24;
        var mm = wholeSeconds / 60 % 60;
        var ss = wholeSeconds % 60;
        return $"{hh:00}:{mm:00}:{ss:00}{(drop ? ';' : ':')}{ff:00}";
    }

    private static string LocalJson(string? path)
    {
        var full = LocalRegularFile(path, MaxInputJson);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected an absolute .json input file.");
        return full;
    }
    private static string LocalRegularFile(string? path, long maximum)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute file path required.");
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || info.Length is < 4 || info.Length > maximum ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("File missing, oversized or redirected.");
        return full;
    }
    private static string FreshJson(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute .json output path required.");
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)) || Path.Exists(full))
            throw new ArgumentException("Output must be new and inside an existing directory.");
        return full;
    }
    private static string LocalRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute library root path required.");
        var full = Path.GetFullPath(path);
        var dir = new DirectoryInfo(full);
        if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Library must exist and not be redirected.");
        return full;
    }
    private static string BoundAudio(string root, string? relative, string? approvedAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 ||
            relative.Contains('\\') || relative.StartsWith('/') ||
            Path.IsPathFullyQualified(relative) ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Unsafe sample library relative path.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(approvedAbsolute) || !full.Equals(Path.GetFullPath(approvedAbsolute), comparison) ||
            !Path.GetRelativePath(root, full).Replace('\\', '/').Equals(relative, comparison))
            throw new ArgumentException("Audio path does not match the approved library file.");
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (dir is not null && !dir.FullName.Equals(root, comparison))
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Redirected audio parent directory.");
            dir = dir.Parent;
        }
        if (dir is null) throw new ArgumentException("File is not in approved library.");
        var info = new FileInfo(full);
        if (!info.Exists || info.Length is < 44 or > MaxAudioBytes ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected unchanged regular local WAV.");
        return full;
    }
    private static long ReadLong(JsonObject node, string key) => node[key]?.GetValue<long>() ??
        throw new ArgumentException("Missing integer " + key);
    private static int ReadInt(JsonObject node, string key) => node[key]?.GetValue<int>() ??
        throw new ArgumentException("Missing integer " + key);
    private static string HashFile(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static bool HashText(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message, ["host_modified"] = false
    };
}
