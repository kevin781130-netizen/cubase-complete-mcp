using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Opt-in bridge to a *separately installed* CutPilot OpenCV histogram scene detector.
/// Scene boundaries are only spotting candidates, not recognized physical actions.
/// </summary>
[McpServerToolType]
public sealed class SteinbergCutPilotTools
{
    private const string PythonEnv = "STEINBERG_CUTPILOT_PYTHON";
    private const string FfprobeEnv = "STEINBERG_CUTPILOT_FFPROBE";
    private const int MaxProviderCharacters = 64 * 1024;

    [McpServerTool(Name = "steinberg_foley_cutpilot_scenes", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("OPTIONAL: Run a separately installed CutPilot histogram scene-boundary detector with an explicitly configured Python and ffprobe, then write a NEW review-only Foley cue sheet. Does not recognize actions, select sounds, or modify a DAW.")]
    public async Task<JsonNode> DetectSceneCuts(
        string video_path, string cutpilot_root, string output_manifest,
        double sampling_interval = 0.25, double threshold = 0.52,
        CancellationToken cancellationToken = default)
    {
        var python = ConfiguredExecutable(PythonEnv, ["python.exe", "python3.exe", "python", "python3"]);
        var ffprobe = ConfiguredExecutable(FfprobeEnv, ["ffprobe.exe", "ffprobe"]);
        if (python is null || ffprobe is null)
            return Error("PROVIDER_NOT_CONFIGURED",
                "Explicitly configure STEINBERG_CUTPILOT_PYTHON and STEINBERG_CUTPILOT_FFPROBE with existing absolute executable paths.");
        try
        {
            if (double.IsNaN(sampling_interval) || double.IsInfinity(sampling_interval) ||
                sampling_interval < 0.10 || sampling_interval > 1.0 ||
                !double.IsFinite(threshold) || threshold < 0.20 || threshold > 1.50)
                return Error("INVALID_SETTINGS", "Interval 0.10..1.0 seconds, threshold 0.20..1.50.");
            var video = ExistingVideo(video_path);
            var root = ExistingCutPilotRoot(cutpilot_root);
            var output = NewManifest(output_manifest);
            var script = Path.Combine(AppContext.BaseDirectory, "providers", "cutpilot_foley_scene_provider.py");
            if (!File.Exists(script))
                return Error("SCRIPT_MISSING", "The CutPilot provider script was not packaged beside the MCP server.");

            var before = new FileInfo(video);
            var originalSize = before.Length;
            var originalWriteUtc = before.LastWriteTimeUtc;
            var start = new ProcessStartInfo
            {
                FileName = python, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = root
            };
            foreach (var argument in new[]
                {
                    script, "--video", video, "--cutpilot-root", root, "--ffprobe", ffprobe,
                    "--interval", sampling_interval.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    "--threshold", threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                })
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return Error("PROVIDER_START_FAILED", "Could not launch local CutPilot Python.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            string stdOut;
            string stdErr;
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                stdOut = await stdout.ConfigureAwait(false);
                stdErr = await stderr.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return Error("PROVIDER_TIMEOUT_OR_CANCELLED", "CutPilot timed out or was cancelled. Output was not created.");
            }
            if (stdOut.Length > MaxProviderCharacters || stdErr.Length > MaxProviderCharacters)
                return Error("PROVIDER_OUTPUT_LIMIT", "CutPilot output exceeded the allowed size.");
            if (process.ExitCode != 0)
                return Error("CUTPILOT_FAILED", "External CutPilot detector failed: " + stdErr[..Math.Min(600, stdErr.Length)]);
            var node = JsonNode.Parse(stdOut);
            if (node is not JsonObject result ||
                result["provider"]?.GetValue<string>() != "cutpilot.scenes.detect_scene_cuts" ||
                result["detection_type"]?.GetValue<string>() != "visual_histogram_scene_boundary_only" ||
                result["automatic_action_recognition"]?.GetValue<bool>() != false ||
                result["human_review_required"]?.GetValue<bool>() != true)
                return Error("PROVIDER_CONTRACT", "Unexpected CutPilot provider output or provenance.");

            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(result["video_path"]?.GetValue<string>(), video, cmp) ||
                result["video_size_bytes"]?.GetValue<long>() != originalSize)
                return Error("SOURCE_MISMATCH", "CutPilot returned an unexpected source video.");
            before.Refresh();
            if (before.Length != originalSize || before.LastWriteTimeUtc != originalWriteUtc)
                return Error("SOURCE_CHANGED", "Video changed during the scene scan.");
            var fpsNum = result["fps_numerator"]?.GetValue<int>() ?? 0;
            var fpsDen = result["fps_denominator"]?.GetValue<int>() ?? 0;
            var totalFrames = result["video_frame_count"]?.GetValue<long>() ?? 0;
            if (result["cues"] is not JsonArray cues || cues.Count > 128)
                return Error("PROVIDER_CONTRACT", "Provider returned invalid or excessive scene cues.");
            if (cues.Count == 0)
                return new JsonObject
                {
                    ["status"] = "NoSceneBoundaries",
                    ["cues"] = 0,
                    ["message"] = "No histogram scene cuts found at the selected threshold. No manifest created.",
                    ["host_modified"] = false,
                    ["video_analyzed"] = true,
                    ["action_recognized"] = false
                };

            var spotted = new SteinbergFoleyTools().SpotCutPilot(
                video, output, cues.ToJsonString(), fpsNum, fpsDen, totalFrames);
            if (spotted["status"]?.GetValue<string>() != "OkDraft") return spotted;
            var copy = (JsonObject)spotted.DeepClone();
            copy["provider"] = "cutpilot.scenes.detect_scene_cuts";
            copy["observation_type"] = "visual_scene_boundary_not_action";
            copy["sampled_video_frames"] = true;
            copy["action_recognized"] = false;
            return copy;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            return Error("CUTPILOT_ADAPTER_REJECTED", ex.Message);
        }
    }

    private static string? ConfiguredExecutable(string variable, string[] leafNames)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        try
        {
            var path = Path.GetFullPath(value);
            var name = Path.GetFileName(path);
            return leafNames.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(path) &&
                !new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ExistingVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Video must be an absolute path.");
        var full = Path.GetFullPath(path);
        if (!new[] { ".mp4", ".mov", ".mkv", ".mxf" }
            .Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) ||
            !File.Exists(full) || new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Video must be an existing local MP4, MOV, MKV or MXF file.");
        return full;
    }

    private static string ExistingCutPilotRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("CutPilot root must be absolute.");
        var root = Path.GetFullPath(path);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !File.Exists(Path.Combine(root, "cutpilot", "scenes.py")))
            throw new ArgumentException("CutPilot root must contain cutpilot/scenes.py.");
        return root;
    }

    private static string NewManifest(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Output must be an absolute .json path.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)) || Path.Exists(full))
            throw new ArgumentException("Choose a new .json file in an existing output folder.");
        return full;
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message,
        ["video_analyzed"] = false, ["host_modified"] = false
    };
}
