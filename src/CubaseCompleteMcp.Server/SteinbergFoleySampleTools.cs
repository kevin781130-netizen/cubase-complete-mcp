using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Review-only local Foley sample recommendations. Consumes conservative
/// dual-model + temporal ROI door proposals; never chooses/plays/places audio.
/// Uses the existing bounded SteinbergSampleTools.Search path for discovery.
/// </summary>
[McpServerToolType]
public sealed class SteinbergFoleySampleTools
{
    private static readonly string[] SearchKeywords = ["door", "gate", "hinge", "latch"];
    private static readonly Regex Opening = new(
        @"(?<![a-z])(?:open|opening|creak|unlock)(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Closing = new(
        @"(?<![a-z])(?:close|closing|shut|slam|latch|lock)(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [McpServerTool(Name = "steinberg_foley_sample_candidates", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Search LOCAL filename/folder metadata for sound candidates corresponding to review-only door proposals in an existing X-CLIP+SigLIP+ROI report. Optionally attach USER-DECLARED license evidence from an explicit JSON registry. Produces a NEW review-only JSON manifest; no samples are approved, copied, decoded, played, or placed in Cubase/Nuendo.")]
    public JsonNode Recommend(string fusion_report, string library_root, string output_manifest,
        string? rights_registry = null, int max_per_event = 3, double max_duration_seconds = 20.0)
    {
        try
        {
            if (max_per_event is < 1 or > 5)
                return Error("INVALID_LIMIT", "Use 1 to 5 sample candidates per door event.");
            if (!double.IsFinite(max_duration_seconds) || max_duration_seconds is < 0.1 or > 120)
                return Error("INVALID_DURATION", "Maximum Foley duration must be 0.1..120 seconds.");
            var source = RequireLocalJson(fusion_report, 2 * 1024 * 1024);
            var output = NewJsonOutput(output_manifest);
            var report = JsonNode.Parse(File.ReadAllText(source));
            if (report is not JsonObject root ||
                root["status"]?.GetValue<string>() !=
                    "PASS_REAL_BOTH_MODELS_FOUR_REVIEWED_VIDEO_WINDOWS_NOT_ACCURACY_VALIDATED" ||
                root["door_roi_motion_experimental"] is not JsonObject motion ||
                motion["status"]?.GetValue<string>() != "REVIEW_ONLY_NOT_DOOR_GEOMETRY_VALIDATED" ||
                motion["motion_gated_rows"] is not JsonArray rows || rows.Count is < 1 or > 16)
                return Error("UNSUPPORTED_REVIEW_REPORT", "Provide a bounded review-only dual-model plus ROI JSON report.");
            var events = new List<(string Id, string Class, string VideoHash, string ContactHash)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in rows)
            {
                if (entry is not JsonObject row)
                    return Error("INVALID_REVIEW_ROW", "A fusion row must be an object.");
                var id = row["id"]?.GetValue<string>();
                var state = row["status"]?.GetValue<string>();
                var action = row["proposed_door_action"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !seen.Add(id))
                    return Error("INVALID_REVIEW_ROW", "Review event IDs must be unique and bounded.");
                if (row["host_action"]?.GetValue<string>() != "none" ||
                    row["selected_audio_file"] is not null)
                    return Error("INVALID_REVIEW_ROW", "Source must contain no prior host action or selected audio.");
                if (state == "abstained" && action is null) continue;
                if (state != "review_proposal_not_confirmed" ||
                    action is not ("opening_door" or "closing_door") ||
                    row["door_panel_geometry_verified"]?.GetValue<bool>() != false ||
                    row["requires_human_review"]?.GetValue<bool>() != true)
                    return Error("INVALID_REVIEW_ROW", "Only unconfirmed, no-host-action door proposals are accepted.");
                var videoHash = row["video_sha256"]?.GetValue<string>();
                var contactHash = row["contact_sha256"]?.GetValue<string>();
                if (!Sha256Text(videoHash) || !Sha256Text(contactHash))
                    return Error("INVALID_SOURCE_HASH", "Review candidates require both source SHA-256 values.");
                events.Add((id, action, videoHash!, contactHash!));
            }
            if (events.Count > 8)
                return Error("TOO_MANY_EVENTS", "Only eight unconfirmed door proposals allowed per manifest.");

            // Existing search owns the library traversal and symlink/entry/depth limits.
            var finder = new SteinbergSampleTools();
            var found = new Dictionary<string, (string Path, long Size)>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            string? approvedRoot = null;
            var truncated = false;
            foreach (var keyword in SearchKeywords)
            {
                var result = finder.Search(library_root, category: "all", limit: 100, filename_query: keyword);
                if (result["status"]?.GetValue<string>() != "Ok")
                    return Error("SAMPLE_LIBRARY_REJECTED", "Sample search refused the library or reached a scan bound: " +
                        (result["code"]?.GetValue<string>() ?? "Unknown"));
                approvedRoot = result["library_root"]!.GetValue<string>();
                var total = result["matching_files"]!.GetValue<int>();
                if (total > 100) truncated = true;
                foreach (var sample in (JsonArray)result["results"]!)
                {
                    var full = sample!["path"]!.GetValue<string>();
                    var bytes = sample["size_bytes"]!.GetValue<long>();
                    if (bytes is < 12 or > 268_435_456) continue;
                    found[full] = (full, bytes);
                }
            }
            if (approvedRoot is null) return Error("SAMPLE_LIBRARY_REJECTED", "No usable local library.");

            var rights = ReadRights(rights_registry);
            var usable = new Dictionary<string, SteinbergFoleyAudioProbe.AudioMetadata>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
            void Exclude(string why)
            {
                excluded.TryGetValue(why, out var count);
                excluded[why] = count + 1;
            }
            foreach (var sample in found.Values)
            {
                if (!SteinbergFoleyAudioProbe.TryProbe(sample.Path, out var audio, out var issue) || audio is null)
                {
                    Exclude(issue);
                    continue;
                }
                if (audio.DurationSeconds < 0.05 || audio.DurationSeconds > max_duration_seconds)
                {
                    Exclude("AUDIO_DURATION_OUTSIDE_FOLEY_RANGE");
                    continue;
                }
                var relative = Path.GetRelativePath(approvedRoot, sample.Path).Replace('\\', '/');
                rights.TryGetValue(relative, out var declaration);
                if (DeclaredRightsRestriction(declaration))
                {
                    Exclude("USER_DECLARED_NONCOMMERCIAL_OR_NO_DERIVATIVES");
                    continue;
                }
                usable.Add(sample.Path, audio);
            }
            var suggestions = new JsonArray();
            foreach (var cue in events)
            {
                var ranked = usable.Select(item =>
                {
                    var sample = found[item.Key];
                    var name = Path.GetFileNameWithoutExtension(sample.Path) + " " +
                               (Path.GetFileName(Path.GetDirectoryName(sample.Path)) ?? "");
                    var score = DoorScore(name, cue.Class);
                    var relative = Path.GetRelativePath(approvedRoot, sample.Path).Replace('\\', '/');
                    rights.TryGetValue(relative, out var license);
                    return new { sample.Path, sample.Size, Audio = item.Value,
                                 Relative = relative, Score = score, License = license };
                }).Where(item => item.Score > 0)
                  .OrderByDescending(item => item.Score)
                  .ThenBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
                  .Take(max_per_event).ToArray();

                var candidates = new JsonArray();
                foreach (var sample in ranked)
                {
                    candidates.Add(new JsonObject
                    {
                        ["path"] = sample.Path,
                        ["relative_path"] = sample.Relative,
                        ["size_bytes"] = sample.Size,
                        ["audio_container_header"] = sample.Audio.Container,
                        ["audio_encoding_header"] = sample.Audio.Encoding,
                        ["sample_rate_hz_header"] = sample.Audio.SampleRate,
                        ["channels_header"] = sample.Audio.Channels,
                        ["bit_depth_header"] = sample.Audio.BitsPerSample,
                        ["duration_seconds_from_header"] = Math.Round(sample.Audio.DurationSeconds, 6),
                        ["audio_header_integrity"] = "basic_riff_fmt_and_data_consistent",
                        ["metadata_score_unvalidated"] = sample.Score,
                        ["match_source"] = "filename_and_parent_folder_tokens_only",
                        ["license_status"] = sample.License is null ? "no_rights_record" : "user_declared_not_verified",
                        ["commercial_rights_cleared"] = false,
                        ["rights_evidence"] = sample.License?.DeepClone(),
                        ["audio_content_decoded"] = false,
                        ["audio_content_sha256_verified"] = false,
                        ["selected"] = false
                    });
                }
                suggestions.Add(new JsonObject
                {
                    ["source_event_id"] = cue.Id,
                    ["video_sha256"] = cue.VideoHash,
                    ["contact_sha256"] = cue.ContactHash,
                    ["proposed_action_unconfirmed"] = cue.Class,
                    ["suggested_candidates"] = candidates,
                    ["candidate_count"] = candidates.Count,
                    ["chosen_sample_path"] = null,
                    ["human_approval_required"] = true,
                    ["sound_effect_auto_placement"] = false
                });
            }
            var document = new JsonObject
            {
                ["schema"] = "steinberg-foley-local-sample-candidates-v1",
                ["status"] = "draft_requires_human_approval",
                ["source"] = "dual_model_plus_roi_review_proposals_not_verified_actions",
                ["source_review_report_path"] = source,
                ["library_root"] = approvedRoot,
                ["rights_registry_path"] = rights_registry,
                ["license_verification"] = "user_declarations_only_no_independent_rights_audit",
                ["search_exhaustive"] = !truncated,
                ["maximum_duration_seconds"] = max_duration_seconds,
                ["sample_filenames_found"] = found.Count,
                ["audio_header_preflight_passed"] = usable.Count,
                ["preflight_passed_files"] = usable.Count,
                ["preflight_excluded_files"] = excluded.Values.Sum(),
                ["excluded_by_reason"] = JsonSerializer.SerializeToNode(excluded),
                ["commercial_rights_cleared"] = false,
                ["audio_preflight_scope"] = "RIFF_WAV_PCM_FLOAT_header_only_AIFF_FLAC_RF64_excluded",
                ["search_truncated_at_100_per_keyword"] = truncated,
                ["sample_ranking_basis"] = "filename_folder_tokens_only_no_audio_listening",
                ["selected_audio_files"] = new JsonArray(),
                ["events"] = suggestions,
                ["host_action"] = "none",
                ["warnings"] = "Action/onset unverified. RIFF/WAV headers only are checked; PCM/float audio has NOT been listened to or decoded, and AIFF/FLAC/RF64 are excluded pending parsers. User-declared licenses do NOT establish commercial rights. Manual listening, rights verification and approval required; no audio is selected or placed."
            };
            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, document, new JsonSerializerOptions { WriteIndented = true });
            return new JsonObject
            {
                ["status"] = "OkDraft",
                ["manifest_path"] = output,
                ["event_count"] = events.Count,
                ["candidate_count"] = suggestions.Sum(x => x!["candidate_count"]!.GetValue<int>()),
                ["search_truncated"] = truncated,
                ["preflight_passed_files"] = usable.Count,
                ["preflight_excluded_files"] = excluded.Values.Sum(),
                ["host_modified"] = false,
                ["requires_review"] = true
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or NotSupportedException or JsonException or OverflowException)
        { return Error("RECOMMENDATION_REJECTED", e.Message); }
    }

    private static bool DeclaredRightsRestriction(JsonObject? declaration)
    {
        var identifier = declaration?["license_id_declared"]?.GetValue<string>();
        // User-declared NC/ND restrictions require separate commercial clearance.
        // Other licenses ALSO remain user-declared and unverified.
        return identifier is not null &&
            (identifier.Contains("-NC", StringComparison.OrdinalIgnoreCase) ||
             identifier.Contains("-ND", StringComparison.OrdinalIgnoreCase));
    }

    private static int DoorScore(string name, string action)
    {
        var open = Opening.IsMatch(name);
        var close = Closing.IsMatch(name);
        if (open && close) return 0; // Ambiguous direction: never guess.
        if (action == "opening_door" && close || action == "closing_door" && open)
            return 0;
        if (!name.Contains("door", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("gate", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("hinge", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("latch", StringComparison.OrdinalIgnoreCase))
            return 0;
        var matchingDirection = action == "opening_door" ? open : close;
        var score = matchingDirection ? 100 : 20;
        // Direct open/close tags describe a door action more specifically
        // than a generic latch or creak; keep the ranking metadata-only.
        if (action == "opening_door" && Regex.IsMatch(name,
                @"(?<![a-z])(?:open|opening)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            score += 30;
        if (action == "closing_door" && Regex.IsMatch(name,
                @"(?<![a-z])(?:close|closing|shut|slam)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            score += 30;
        if (name.Contains("door", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (name.Contains("wood", StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }

    private static Dictionary<string, JsonObject> ReadRights(string? path)
    {
        var records = new Dictionary<string, JsonObject>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(path)) return records;
        var source = JsonNode.Parse(File.ReadAllText(RequireLocalJson(path, 1024 * 1024)));
        if (source?["schema"]?.GetValue<string>() != "steinberg-local-sample-rights-v1" ||
            source["entries"] is not JsonArray entries || entries.Count > 5000)
            throw new ArgumentException("Expected rights JSON schema and at most 5000 entries.");
        foreach (var e in entries)
        {
            if (e is not JsonObject item) throw new ArgumentException("Rights entry must be an object.");
            var relative = item["relative_path"]?.GetValue<string>()?.Replace('\\', '/');
            var license = item["license_id"]?.GetValue<string>();
            var url = item["source_url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 ||
                relative.StartsWith('/') || Path.IsPathFullyQualified(relative) ||
                relative.Split('/').Any(part => part is "" or "." or "..") ||
                string.IsNullOrWhiteSpace(license) || license.Length > 80 ||
                string.IsNullOrWhiteSpace(url) || url.Length > 1024 ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !records.TryAdd(relative, new JsonObject
                {
                    ["license_id_declared"] = license,
                    ["source_url_declared"] = url,
                    ["attribution_declared"] = item["attribution"]?.GetValue<string>() ?? ""
                }))
                throw new ArgumentException("Invalid, duplicate, or untraceable rights registry entry.");
        }
        return records;
    }

    private static string RequireLocalJson(string path, long maximum)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute local .json path is required.");
        var full = Path.GetFullPath(path);
        var file = new FileInfo(full);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !file.Exists || file.Length > maximum || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("JSON input must exist, be bounded and not be a symlink.");
        return full;
    }

    private static string NewJsonOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute new output .json path is required.");
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)) || Path.Exists(full))
            throw new ArgumentException("Choose a nonexistent .json file in an existing directory.");
        return full;
    }

    private static bool Sha256Text(string? sha) =>
        sha is { Length: 64 } && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message,
        ["host_modified"] = false
    };
}
