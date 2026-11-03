using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Metadata-only sample discovery and an explicit single-placement draft.
/// No DAW/host mutation, no external processes, no audio transcription, and no network.
/// </summary>
[McpServerToolType]
public sealed class SteinbergSampleTools
{
    private const int MaximumEntries = 12_000;
    private const int MaximumDirectories = 400;
    private const int MaximumDepth = 8;
    private const long MaximumSampleBytes = 256L * 1024 * 1024;
    private static readonly string[] Categories =
        ["kick", "snare", "clap", "hihat", "tom", "cymbal", "percussion", "bass", "loop", "fx", "other"];
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".aif", ".aiff", ".flac" };
    private static readonly Regex TempoPattern = new(
        @"(?<![0-9])(?<bpm>[4-9][0-9]|1[0-9][0-9]|2[0-9][0-9]|300)[\s_-]*bpm(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [McpServerTool(Name = "steinberg_sample_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search a chosen LOCAL sample folder using file and folder name tags only (kick, snare, loop etc.) and explicit filename BPM. Does not listen to or analyze audio. Bounded traversal; symlinks/reparse points skipped. No modification of Cubase/Nuendo.")]
    public JsonNode Search(string library_root, string category = "all", double? target_bpm = null, int limit = 30,
        string? filename_query = null)
    {
        try
        {
            var root = RequireRoot(library_root);
            if (category != "all" && !Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                return Error("INVALID_CATEGORY", "Use all, kick, snare, clap, hihat, tom, cymbal, percussion, bass, loop, fx or other.");
            if (target_bpm is not null && (!double.IsFinite(target_bpm.Value) || target_bpm < 30 || target_bpm > 300))
                return Error("INVALID_BPM", "BPM must be between 30 and 300.");
            if (limit is < 1 or > 100)
                return Error("INVALID_LIMIT", "Limit must be between 1 and 100.");
            if (filename_query is not null &&
                (filename_query.Length is < 1 or > 48 || string.IsNullOrWhiteSpace(filename_query) ||
                 filename_query.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')))
                return Error("INVALID_QUERY", "Filename query must contain 1..48 simple letters, numbers, dash or underscore.");

            var candidates = new List<SampleRecord>();
            var dirs = new Stack<(DirectoryInfo Dir, int Depth)>();
            dirs.Push((new DirectoryInfo(root), 0));
            int examined = 0, directories = 0;
            while (dirs.Count > 0)
            {
                var (dir, depth) = dirs.Pop();
                if (++directories > MaximumDirectories)
                    return Error("SCAN_LIMIT", "Maximum directory count exceeded; select a narrower library folder.");
                foreach (var item in dir.EnumerateFileSystemInfos())
                {
                    if (++examined > MaximumEntries)
                        return Error("SCAN_LIMIT", "Maximum entry count exceeded; select a narrower library folder.");
                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (item is DirectoryInfo child)
                    {
                        if (depth < MaximumDepth) dirs.Push((child, depth + 1));
                        continue;
                    }
                    if (item is not FileInfo file || !Extensions.Contains(file.Extension)) continue;
                    var searchable = Path.GetFileNameWithoutExtension(file.Name) + " " + dir.Name;
                    if (filename_query is not null &&
                        !searchable.Contains(filename_query, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var tags = TagsFor(searchable);
                    var detected = tags.Count > 0 ? tags[0] : "other";
                    if (category != "all" && !tags.Contains(category, StringComparer.OrdinalIgnoreCase) &&
                        !(category.Equals("other", StringComparison.OrdinalIgnoreCase) && tags.Count == 0))
                        continue;
                    candidates.Add(new SampleRecord(file.FullName, detected, ParseBpm(file.Name), tags, file.Length));
                }
            }

            var results = candidates.OrderBy(s => target_bpm.HasValue && !s.FilenameBpm.HasValue ? 1 : 0)
                .ThenBy(s => target_bpm.HasValue && s.FilenameBpm.HasValue
                    ? Math.Abs(s.FilenameBpm.Value - target_bpm.Value) : 0)
                .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .Take(limit).Select(s => new JsonObject
                {
                    ["path"] = s.Path, ["category_guess"] = s.Category,
                    ["category_source"] = "filename_or_parent_folder", ["tags"] = JsonSerializer.SerializeToNode(s.Tags),
                    ["filename_bpm"] = s.FilenameBpm,
                    ["bpm_source"] = s.FilenameBpm.HasValue ? "filename_tag_only" : "unknown",
                    ["size_bytes"] = s.Size
                }).ToArray();

            return new JsonObject
            {
                ["status"] = "Ok", ["source"] = "local_metadata_only",
                ["library_root"] = root, ["filename_query"] = filename_query,
                ["examined_entries"] = examined,
                ["matching_files"] = candidates.Count, ["results"] = new JsonArray(results),
                ["incomplete"] = false,
                ["limitations"] = "No waveform BPM detection, key detection, listening, license validation, or audio-quality ranking."
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        { return Error("LIBRARY_UNAVAILABLE", e.Message); }
    }

    [McpServerTool(Name = "steinberg_sample_plan", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Draft ONE sample placement in musical bars/beats as a new JSON manifest. Explicit local sample path, 4-denominator constant tempo, no overwrite. Does not place audio in Cubase/Nuendo or stretch loops; manual approval is required.")]
    public JsonNode Plan(string library_root, string sample_path, string output_manifest, double project_bpm,
        int bar = 1, int beat = 1, int sixteenth = 0, int beats_per_bar = 4)
    {
        try
        {
            var root = RequireRoot(library_root);
            var sample = RequireSampleInsideRoot(root, sample_path);
            var dest = RequireNewManifest(output_manifest);
            if (!double.IsFinite(project_bpm) || project_bpm < 30 || project_bpm > 300)
                return Error("INVALID_BPM", "Project BPM must be 30..300.");
            if (beats_per_bar is < 1 or > 16 || bar is < 1 or > 100_000 ||
                beat < 1 || beat > beats_per_bar || sixteenth is < 0 or > 3)
                return Error("INVALID_GRID", "Use bar 1..100000, beat 1..beats_per_bar and sixteenth 0..3.");

            var info = new FileInfo(sample);
            if (info.Length is < 12 or > MaximumSampleBytes)
                return Error("SAMPLE_SIZE_UNSUPPORTED", "Sample must be 12 bytes to 256 MiB.");
            if (!HasKnownAudioHeader(sample))
                return Error("UNRECOGNIZED_AUDIO", "WAV/AIFF/FLAC file signature does not match the extension.");
            var originalLength = info.Length;
            var originalModified = info.LastWriteTimeUtc;
            string hash;
            using (var stream = File.OpenRead(sample)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            info.Refresh();
            if (info.Length != originalLength || info.LastWriteTimeUtc != originalModified)
                return Error("SOURCE_CHANGED", "The sample changed during inspection. No plan written.");

            var label = Path.GetFileNameWithoutExtension(sample);
            var tags = TagsFor(label + " " + Path.GetFileName(Path.GetDirectoryName(sample)));
            var sourceBpm = ParseBpm(Path.GetFileName(sample));
            var tick = checked(((long)(bar - 1) * beats_per_bar + (beat - 1)) * 960L + sixteenth * 240L);
            var seconds = Math.Round(tick / 960.0 * 60.0 / project_bpm, 6);
            var doc = new JsonObject
            {
                ["schema"] = "steinberg-sample-placement-plan-v1",
                ["status"] = "draft_requires_approval",
                ["origin"] = "local_file_metadata_not_audio_analysis",
                ["source"] = new JsonObject {
                    ["path"] = sample, ["sha256"] = hash, ["size_bytes"] = originalLength,
                    ["category_guess"] = tags.Count > 0 ? tags[0] : "other",
                    ["filename_bpm"] = sourceBpm, ["bpm_source"] = sourceBpm.HasValue ? "filename_tag_only" : "unknown"
                },
                ["project"] = new JsonObject { ["bpm"] = project_bpm, ["beats_per_bar"] = beats_per_bar, ["beat_unit"] = 4, ["ppq"] = 960 },
                ["placement"] = new JsonObject { ["bar"] = bar, ["beat"] = beat, ["sixteenth"] = sixteenth,
                    ["tick"] = tick, ["seconds_from_project_start"] = seconds },
                ["time_stretch"] = "not_applied_requires_review",
                ["host_action"] = "none",
                ["warnings"] = "Fixed-tempo draft only. Source BPM is a filename tag, not measured; bar-1 origin and actual project timing must be checked in DAW. Audio license not verified."
            };
            // CreateNew refuses any concurrent writer or overwrite of an existing plan.
            using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, doc, new JsonSerializerOptions { WriteIndented = true });
            return new JsonObject { ["status"] = "OkDraft", ["manifest_path"] = dest,
                ["tick"] = tick, ["seconds"] = seconds, ["host_modified"] = false };
        }
        catch (IOException e) { return Error("OUTPUT_OR_IO_ERROR", e.Message); }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or OverflowException)
        { return Error("INVALID_PLAN", e.Message); }
    }

    private sealed record SampleRecord(string Path, string Category, double? FilenameBpm, List<string> Tags, long Size);

    private static double? ParseBpm(string name)
    {
        var m = TempoPattern.Match(name);
        return m.Success && double.TryParse(m.Groups["bpm"].Value, out var bpm) ? bpm : null;
    }

    private static List<string> TagsFor(string text)
    {
        text = text.ToLowerInvariant();
        var tags = new List<string>();
        foreach (var (tag, pattern) in new (string, string)[] {
            ("kick", @"(?<![a-z])(?:kick|bd)(?![a-z])"),
            ("snare", @"(?<![a-z])(?:snare|sd)(?![a-z])"),
            ("clap", @"(?<![a-z])clap(?![a-z])"),
            ("hihat", @"(?<![a-z])(?:hi[\s_-]?hat|hh)(?![a-z])"),
            ("tom", @"(?<![a-z])tom(?![a-z])"),
            ("cymbal", @"(?<![a-z])(?:cymbal|crash|ride)(?![a-z])"),
            ("percussion", @"(?<![a-z])(?:percussion|perc)(?![a-z])"),
            ("bass", @"(?<![a-z])bass(?![a-z])"),
            ("loop", @"(?<![a-z])loop(?![a-z])"),
            ("fx", @"(?<![a-z])(?:fx|sfx|effect)(?![a-z])")
        })
            if (Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant)) tags.Add(tag);
        return tags;
    }

    private static string RequireRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The library root must be an absolute path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var dir = new DirectoryInfo(root);
        if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("The library root does not exist or is a reparse-point directory.");
        return root;
    }

    private static string RequireSampleInsideRoot(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Sample path must be absolute.");
        var sample = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!sample.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Sample must be inside the approved sample library.");
        if (!Extensions.Contains(Path.GetExtension(sample)) || !File.Exists(sample))
            throw new ArgumentException("The sample must be an existing WAV, AIFF or FLAC file.");
        for (var current = new FileInfo(sample) as FileSystemInfo; current is not null; current = current switch
        {
            FileInfo f => f.Directory,
            DirectoryInfo d => d.Parent,
            _ => null
        })
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Links and reparse points are not allowed.");
            if (current.FullName.Equals(root, comparison)) break;
        }
        return sample;
    }

    private static string RequireNewManifest(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Manifest destination must be an absolute path.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)))
            throw new ArgumentException("Manifest must be a new .json in an existing folder.");
        if (File.Exists(full)) throw new IOException("Manifest already exists; choose a new path.");
        return full;
    }

    private static bool HasKnownAudioHeader(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[12];
        var count = stream.Read(head);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return count >= 12 && (head[..4].SequenceEqual("RIFF"u8) || head[..4].SequenceEqual("RF64"u8)) &&
                head.Slice(8, 4).SequenceEqual("WAVE"u8);
        if (ext.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase))
            return count >= 12 && head[..4].SequenceEqual("FORM"u8) &&
                (head.Slice(8, 4).SequenceEqual("AIFF"u8) || head.Slice(8, 4).SequenceEqual("AIFC"u8));
        return count >= 4 && head[..4].SequenceEqual("fLaC"u8);
    }

    private static JsonObject Error(string code, string message) => new()
    { ["status"] = "Error", ["code"] = code, ["message"] = message, ["host_modified"] = false };
}
