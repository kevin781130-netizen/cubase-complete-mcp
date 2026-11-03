using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// File-only, human/external-observation Foley spotting interface.
/// Does not inspect video frames, classify actions, search for audio, or alter a DAW.
/// </summary>
[McpServerToolType]
public sealed class SteinbergFoleyTools
{
    private const int MaximumCues = 128;
    private const int MaximumCueJsonLength = 64 * 1024;
    private static readonly string[] Tracks =
        ["footsteps", "impacts", "cloth", "doors", "ambience", "vehicles", "other"];

    [McpServerTool(Name = "steinberg_foley_spot", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Convert human- or external-detector-supplied video FRAME indices and Foley SFX search phrases into a new review-only JSON cue sheet. Handles rational FPS (e.g. 30000/1001) without guessing or decoding the video. No sound files are selected, and no DAW is modified.")]
    public JsonNode Spot(string video_path, string output_manifest, string cues_json,
        int fps_numerator, int fps_denominator, long video_frame_count) =>
        SpotCore(video_path, output_manifest, cues_json, fps_numerator, fps_denominator,
            video_frame_count, cutPilot: false);

    internal JsonNode SpotCutPilot(string video_path, string output_manifest, string cues_json,
        int fps_numerator, int fps_denominator, long video_frame_count) =>
        SpotCore(video_path, output_manifest, cues_json, fps_numerator, fps_denominator,
            video_frame_count, cutPilot: true);

    private JsonNode SpotCore(string video_path, string output_manifest, string cues_json,
        int fps_numerator, int fps_denominator, long video_frame_count, bool cutPilot)
    {
        try
        {
            var video = ExistingVideoPath(video_path);
            var output = NewOutputPath(output_manifest);
            if (fps_numerator is < 1 or > 120000 || fps_denominator is < 1 or > 1001 ||
                fps_numerator / (double)fps_denominator is < 1 or > 240)
                return Error("INVALID_FPS", "Provide a rational FPS from 1 through 240.");
            if (video_frame_count is < 1 or > 20_000_000)
                return Error("INVALID_FRAME_COUNT", "Explicit video frame count must be 1 through 20,000,000.");
            if (string.IsNullOrWhiteSpace(cues_json) || cues_json.Length > MaximumCueJsonLength)
                return Error("INVALID_CUES", "Cues must be JSON and no more than 64 KiB.");

            var node = JsonNode.Parse(cues_json);
            if (node is not JsonArray cues || cues.Count is < 1 or > MaximumCues)
                return Error("INVALID_CUES", "Provide a JSON array of 1 to 128 cue objects.");

            var events = new List<(long Frame, int OriginalIndex, JsonObject Value)>();
            for (var i = 0; i < cues.Count; i++)
            {
                if (cues[i] is not JsonObject cue)
                    return Error("INVALID_CUE", "Cue " + i + " is not a JSON object.");
                var frame = RequiredLong(cue, "frame");
                var duration = OptionalLong(cue, "duration_frames", 0);
                if (frame < 0 || frame >= video_frame_count || duration < 0 ||
                    duration > video_frame_count - frame)
                    return Error("INVALID_CUE_FRAME", "Cue " + i + " falls outside the declared video frame range.");

                var label = RequiredText(cue, "event_label", 120);
                var search = RequiredText(cue, "sfx_query", 120);
                var track = RequiredText(cue, "track", 32).ToLowerInvariant();
                if (!Tracks.Contains(track, StringComparer.Ordinal))
                    return Error("INVALID_TRACK", "Use footsteps, impacts, cloth, doors, ambience, vehicles or other.");

                // Frames are integer offsets from zero, not human-readable timecode.
                // The video FPS / frame count are supplied by the caller, not measured.
                var seconds = Math.Round(frame * (double)fps_denominator / fps_numerator, 9);
                var durationSeconds = Math.Round(duration * (double)fps_denominator / fps_numerator, 9);
                events.Add((frame, i, new JsonObject
                {
                    ["source_index"] = i,
                    ["frame"] = frame,
                    ["duration_frames"] = duration,
                    ["seconds_from_video_start"] = seconds,
                    ["duration_seconds"] = durationSeconds,
                    ["event_label"] = label,
                    ["track"] = track,
                    ["sfx_query"] = search,
                    ["selection_status"] = "unmatched_requires_review",
                    ["sample_path"] = null
                }));
            }

            var info = new FileInfo(video);
            var sorted = new JsonArray();
            foreach (var entry in events.OrderBy(x => x.Frame).ThenBy(x => x.OriginalIndex))
                sorted.Add(entry.Value);
            var payload = new JsonObject
            {
                ["schema"] = "steinberg-foley-cues-v1",
                ["status"] = "draft_requires_approval",
                ["source"] = cutPilot ? "cutpilot_histogram_scene_boundaries_not_action_detection" : "caller_supplied_spotting_not_video_inference",
                ["video"] = new JsonObject
                {
                    ["path"] = video,
                    ["size_bytes"] = info.Length,
                    ["fps_numerator"] = fps_numerator,
                    ["fps_denominator"] = fps_denominator,
                    ["total_frames"] = video_frame_count,
                    ["timing_provenance"] = cutPilot ? "ffprobe_average_frame_rate_assumed_cfr" : "caller_supplied_unverified"
                },
                ["events"] = sorted,
                ["host_action"] = "none",
                ["warnings"] = cutPilot
                    ? "CutPilot sampled video frames and used histogram correlation for scene boundaries only. Scene positions are sampling-resolution estimates, not semantic actions or precise SMPTE timecode. Confirm constant FPS, every scene boundary, SFX need, and audio rights. No sound was selected and no DAW project was changed."
                    : "No frames were decoded, no video FPS/duration was probed, no actions were detected, no SFX files were matched, and no DAW project was changed. Confirm source timing, source video origin, SFX rights and event accuracy before import."
            };

            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, payload, new JsonSerializerOptions { WriteIndented = true });

            return new JsonObject
            {
                ["status"] = "OkDraft",
                ["manifest_path"] = output,
                ["cues"] = sorted.Count,
                ["unmatched_samples"] = sorted.Count,
                ["host_modified"] = false,
                ["video_analyzed"] = cutPilot,
                ["requires_review"] = true
            };
        }
        catch (JsonException e) { return Error("INVALID_CUES_JSON", e.Message); }
        catch (IOException e) { return Error("OUTPUT_OR_IO_ERROR", e.Message); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException
            or UnauthorizedAccessException or NotSupportedException or OverflowException)
        { return Error("INVALID_INPUT", e.Message); }
    }

    private static string ExistingVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Video must be an absolute local path.");
        var full = Path.GetFullPath(path);
        if (!new[] { ".mp4", ".mov", ".mkv", ".mxf" }
            .Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Video must be an existing .mp4, .mov, .mkv or .mxf file.");
        if (new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Video links are not accepted.");
        return full;
    }

    private static string NewOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Output must be an absolute path.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.GetDirectoryName(full)))
            throw new ArgumentException("Output must be a new .json in an existing folder.");
        if (Path.Exists(full))
            throw new IOException("Output already exists; choose a new path.");
        return full;
    }

    private static long RequiredLong(JsonObject cue, string key) =>
        cue[key]?.GetValue<long>() ?? throw new ArgumentException("Missing integer " + key);
    private static long OptionalLong(JsonObject cue, string key, long fallback) =>
        cue[key] is null ? fallback : cue[key]!.GetValue<long>();
    private static string RequiredText(JsonObject cue, string key, int max)
    {
        var value = cue[key]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max ||
            value.Any(char.IsControl))
            throw new ArgumentException(key + " must be nonempty text (max " + max + " characters).");
        return value.Trim();
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message,
        ["video_analyzed"] = false, ["host_modified"] = false
    };
}
