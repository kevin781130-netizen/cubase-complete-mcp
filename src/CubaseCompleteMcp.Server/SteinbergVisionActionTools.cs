using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Explicitly configured, local-only X-CLIP video/text similarity adapter.
/// No model downloads, no sound selection, no Cubase/Nuendo host mutation.
/// </summary>
[McpServerToolType]
public sealed class SteinbergVisionActionTools
{
    private const string PythonEnv = "STEINBERG_XCLIP_PYTHON";
    private const string FfprobeEnv = "STEINBERG_XCLIP_FFPROBE";
    private const int MaxStdout = 64 * 1024;

    [McpServerTool(Name = "steinberg_foley_xclip_actions", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("OPTIONAL: Classify up to 30 seconds of a local constant-FPS video using separately installed offline Microsoft X-CLIP safetensors. Produces only conservative, review-required action candidate cues. Never confirms exact actions or picks SFX, never modifies a DAW.")]
    public async Task<JsonNode> Classify(
        string video_path, string model_directory, string output_manifest,
        double min_relative_score = 0.48, double min_margin = 0.12,
        CancellationToken cancellationToken = default)
    {
        var python = ConfiguredExecutable(PythonEnv, ["python.exe", "python3.exe", "python3", "python"]);
        var ffprobe = ConfiguredExecutable(FfprobeEnv, ["ffprobe.exe", "ffprobe"]);
        if (python is null || ffprobe is null)
            return Error("PROVIDER_NOT_CONFIGURED",
                "Set STEINBERG_XCLIP_PYTHON and STEINBERG_XCLIP_FFPROBE to absolute installed executable paths.");
        try
        {
            if (!double.IsFinite(min_relative_score) || min_relative_score is < 0.30 or > 0.99 ||
                !double.IsFinite(min_margin) || min_margin is < 0.05 or > 0.95)
                return Error("INVALID_THRESHOLD", "Score must be 0.30..0.99 and margin 0.05..0.95.");

            var video = ExistingVideo(video_path);
            var model = ExistingModel(model_directory);
            var output = NewOutput(output_manifest);
            var script = Path.Combine(AppContext.BaseDirectory, "providers", "xclip_foley_action_provider.py");
            if (!File.Exists(script) || new FileInfo(script).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return Error("PROVIDER_SCRIPT_MISSING", "Bundled offline X-CLIP provider script missing.");

            var file = new FileInfo(video);
            var sourceBytes = file.Length;
            var sourceWrite = file.LastWriteTimeUtc;
            var start = new ProcessStartInfo
            {
                FileName = python, WorkingDirectory = Path.GetDirectoryName(video)!,
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // Disallow model/processor downloads and telemetry even if Python has Internet.
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["TRANSFORMERS_OFFLINE"] = "1";
            start.Environment["HF_DATASETS_OFFLINE"] = "1";
            start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            foreach (var arg in new[]
                {
                    script, "--video", video, "--model-dir", model, "--ffprobe", ffprobe,
                    "--min-score", min_relative_score.ToString("R", CultureInfo.InvariantCulture),
                    "--min-margin", min_margin.ToString("R", CultureInfo.InvariantCulture)
                })
                start.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = start };
            if (!process.Start()) return Error("PROVIDER_START_FAILED", "Local X-CLIP Python could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(300));
            string stdout;
            string stderr;
            try
            {
                var readOut = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var readErr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                stdout = await readOut.ConfigureAwait(false);
                stderr = await readErr.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return Error("PROVIDER_TIMEOUT_OR_CANCELLED", "X-CLIP timed out or was cancelled; no sheet written.");
            }
            if (stdout.Length > MaxStdout || stderr.Length > MaxStdout)
                return Error("PROVIDER_OUTPUT_LIMIT", "Model output exceeded 64 KiB.");
            if (process.ExitCode != 0)
                return Error("INFERENCE_FAILED", "Local model failed: " + stderr[..Math.Min(stderr.Length, 450)]);

            var json = JsonNode.Parse(stdout);
            if (json is not JsonObject result ||
                result["provider"]?.GetValue<string>() != "microsoft.xclip.zero_shot_local" ||
                result["detection_type"]?.GetValue<string>() != "video_text_similarity_action_candidates_only" ||
                result["model_executed"]?.GetValue<bool>() != true ||
                result["human_review_required"]?.GetValue<bool>() != true ||
                result["relative_score_not_calibrated"]?.GetValue<bool>() != true ||
                result["sample_audio_identified"]?.GetValue<bool>() != false ||
                result["automatic_action_recognition"]?.GetValue<bool>() != true)
                return Error("PROVIDER_CONTRACT", "X-CLIP returned unexpected or unverified provenance.");

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(result["video_path"]?.GetValue<string>(), video, comparison) ||
                !string.Equals(result["model_directory"]?.GetValue<string>(), model, comparison) ||
                result["video_size_bytes"]?.GetValue<long>() != sourceBytes)
                return Error("SOURCE_MISMATCH", "Provider returned unexpected video/model identity.");
            file.Refresh();
            if (file.Length != sourceBytes || file.LastWriteTimeUtc != sourceWrite)
                return Error("SOURCE_CHANGED", "Video changed during inference.");
            var fpsNum = result["fps_numerator"]?.GetValue<int>() ?? 0;
            var fpsDen = result["fps_denominator"]?.GetValue<int>() ?? 0;
            var frameCount = result["video_frame_count"]?.GetValue<long>() ?? 0;
            var windows = result["windows_evaluated"]?.GetValue<int>() ?? -1;
            if (fpsNum is < 1 or > 120000 || fpsDen is < 1 or > 1001 ||
                frameCount < 8 || frameCount > 20_000_000 ||
                windows is < 1 or > 12 || result["sampled_frames_per_window"]?.GetValue<int>() != 8 ||
                result["cues"] is not JsonArray cues || cues.Count > windows)
                return Error("PROVIDER_CONTRACT", "Unbounded or invalid X-CLIP frame/window metadata.");

            if (cues.Count == 0)
                return new JsonObject
                {
                    ["status"] = "NoActionCandidates",
                    ["message"] = "All windows abstained or were non-action; no cue sheet written.",
                    ["windows_evaluated"] = windows,
                    ["cues"] = 0, ["host_modified"] = false,
                    ["video_analyzed"] = true, ["requires_review"] = true
                };

            var spotted = new SteinbergFoleyTools().SpotVision(
                video, output, cues.ToJsonString(), fpsNum, fpsDen, frameCount);
            if (spotted["status"]?.GetValue<string>() != "OkDraft") return spotted;
            var response = (JsonObject)spotted.DeepClone();
            response["provider"] = "microsoft.xclip.zero_shot_local";
            response["detection_type"] = "video_text_similarity_action_candidates_only";
            response["model_executed"] = true;
            response["score_calibrated"] = false;
            response["windows_evaluated"] = windows;
            return response;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Text.Json.JsonException
            or Win32Exception or NotSupportedException)
        {
            return Error("VISION_ADAPTER_REJECTED", ex.Message);
        }
    }

    private static string? ConfiguredExecutable(string variable, string[] leafNames)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured)) return null;
        try
        {
            var path = Path.GetFullPath(configured);
            return leafNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) &&
                File.Exists(path) && !new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string ExistingVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Video must use an absolute local path.");
        var full = Path.GetFullPath(path);
        if (!new[] { ".mp4", ".mov", ".mkv", ".mxf" }
            .Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) ||
            !File.Exists(full) || new FileInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Video must be a non-linked existing MP4, MOV, MKV or MXF.");
        return full;
    }

    private static string ExistingModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Model directory must be an absolute local path.");
        var root = Path.GetFullPath(path);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !File.Exists(Path.Combine(root, "config.json")) ||
            !(File.Exists(Path.Combine(root, "model.safetensors")) ||
              File.Exists(Path.Combine(root, "model.safetensors.index.json"))))
            throw new ArgumentException("An existing local X-CLIP model with safetensors is required.");
        return root;
    }

    private static string NewOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Output must be an absolute JSON path.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)) || Path.Exists(full))
            throw new ArgumentException("Choose a new .json file in an existing directory.");
        return full;
    }

    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message,
        ["host_modified"] = false, ["video_analyzed"] = false
    };
}
