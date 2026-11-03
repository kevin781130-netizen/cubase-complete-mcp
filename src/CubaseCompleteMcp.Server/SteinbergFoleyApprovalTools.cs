using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Two-stage, local-only and review-only Foley approval workflow.
/// A reviewer must explicitly approve the precise SHA-256 audio bytes, not a
/// filename. This module cannot launch, control, export or edit any DAW project.
/// </summary>
[McpServerToolType]
public sealed class SteinbergFoleyApprovalTools
{
    private const long MaxJsonBytes = 2 * 1024 * 1024;
    private const long MaxAudioBytes = 256L * 1024 * 1024;

    [McpServerTool(Name = "steinberg_foley_prepare_audio_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Hash the actual local WAV bytes of bounded Foley draft suggestions and create a NEW human-review sheet; approval and rights are not assumed. No DAW action.")]
    public JsonNode Prepare(string candidate_manifest, string output_review)
    {
        try
        {
            var source = LocalJson(candidate_manifest);
            var output = FreshOutput(output_review);
            var bytes = File.ReadAllBytes(source);
            var document = JsonNode.Parse(bytes) as JsonObject;
            if (document?["schema"]?.GetValue<string>() != "steinberg-foley-local-sample-candidates-v1" ||
                document["status"]?.GetValue<string>() != "draft_requires_human_approval" ||
                document["host_action"]?.GetValue<string>() != "none" ||
                document["selected_audio_files"] is not JsonArray selected || selected.Count != 0 ||
                document["events"] is not JsonArray events || events.Count is < 1 or > 16)
                return Error("INVALID_DRAFT", "Expected a review-only Foley candidates manifest with no selected sounds.");

            var root = LocalRoot(document["library_root"]?.GetValue<string>());
            var reviews = new JsonArray();
            var seenEvents = new HashSet<string>(StringComparer.Ordinal);
            int total = 0;
            foreach (var evt in events)
            {
                if (evt is not JsonObject e || e["human_approval_required"]?.GetValue<bool>() != true ||
                    e["sound_effect_auto_placement"]?.GetValue<bool>() != false ||
                    e["chosen_sample_path"] is not null)
                    return Error("INVALID_EVENT", "A draft event must require manual approval and contain no placement.");
                var id = e["source_event_id"]?.GetValue<string>();
                var action = e["proposed_action_unconfirmed"]?.GetValue<string>();
                var videoHash = e["video_sha256"]?.GetValue<string>();
                var contactHash = e["contact_sha256"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !seenEvents.Add(id) ||
                    action is not ("opening_door" or "closing_door") ||
                    !HashText(videoHash) || !HashText(contactHash) ||
                    e["suggested_candidates"] is not JsonArray candidates ||
                    candidates.Count > 5 || candidates.Count != e["candidate_count"]?.GetValue<int>())
                    return Error("INVALID_EVENT", "Only unique, bounded, unconfirmed opening/closing proposals are supported.");
                var choices = new JsonArray();
                var paths = new HashSet<string>(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var value in candidates)
                {
                    if (++total > 40) return Error("TOO_MANY_CHOICES", "At most 40 audio candidates may be hashed.");
                    if (value is not JsonObject c || c["selected"]?.GetValue<bool>() != false ||
                        c["commercial_rights_cleared"]?.GetValue<bool>() != false ||
                        c["audio_header_integrity"]?.GetValue<string>() != "basic_riff_fmt_and_data_consistent")
                        return Error("INVALID_CANDIDATE", "Expected an unselected, preflighted WAV candidate.");
                    var relative = c["relative_path"]?.GetValue<string>();
                    var inputPath = c["path"]?.GetValue<string>();
                    var audio = BoundAudio(root, relative, inputPath);
                    if (!paths.Add(audio))
                        return Error("DUPLICATE_CANDIDATE", "Repeated source audio within the same event.");
                    var headerOk = SteinbergFoleyAudioProbe.TryProbe(audio, out var header, out var issue);
                    if (!headerOk || header is null ||
                        c["size_bytes"]?.GetValue<long>() != new FileInfo(audio).Length ||
                        c["sample_rate_hz_header"]?.GetValue<int>() != header.SampleRate ||
                        c["channels_header"]?.GetValue<int>() != header.Channels ||
                        c["bit_depth_header"]?.GetValue<int>() != header.BitsPerSample ||
                        Math.Abs(c["duration_seconds_from_header"]!.GetValue<double>() - header.DurationSeconds) > 0.000001)
                        return Error("AUDIO_CHANGED_OR_INVALID", "Header or source WAV changed since draft: " + issue);

                    var digest = HashFile(audio);
                    choices.Add(new JsonObject
                    {
                        ["relative_path"] = relative,
                        ["sample_sha256"] = digest,
                        ["sample_bytes"] = new FileInfo(audio).Length,
                        ["sample_rate_hz"] = header.SampleRate,
                        ["channels"] = header.Channels,
                        ["bits_per_sample"] = header.BitsPerSample,
                        ["duration_seconds"] = Math.Round(header.DurationSeconds, 6),
                        ["rights_status_at_draft"] = c["license_status"]?.GetValue<string>() ?? "no_rights_record",
                        ["rights_evidence_at_draft"] = c["rights_evidence"]?.DeepClone(),
                        ["listened_to_by_human"] = false,
                        ["licensed_for_intended_use_verified_by_human"] = false,
                        ["selected"] = false
                    });
                }
                reviews.Add(new JsonObject
                {
                    ["source_event_id"] = id,
                    ["proposed_action_unconfirmed"] = action,
                    ["video_sha256"] = videoHash,
                    ["contact_sha256"] = contactHash,
                    ["choices"] = choices
                });
            }
            var review = new JsonObject
            {
                ["schema"] = "steinberg-foley-audio-review-sheet-v1",
                ["status"] = "awaiting_explicit_human_approval",
                ["candidate_manifest_sha256"] = Hex(SHA256.HashData(bytes)),
                ["candidate_manifest_path"] = source,
                ["library_root"] = root,
                ["events"] = reviews,
                ["all_rights_verified"] = false,
                ["any_audio_selected"] = false,
                ["audio_quality_reviewed"] = false,
                ["host_action"] = "none",
                ["note"] = "Sample SHA-256 is pinned. Human must listen, confirm the matching action and independently review the commercial usage rights before authorizing a specific SHA. No timing approval or DAW import."
            };
            WriteNew(output, review);
            var reviewHash = HashFile(output);
            return new JsonObject
            {
                ["status"] = "OkReviewPending",
                ["review_path"] = output,
                ["review_sha256"] = reviewHash,
                ["event_count"] = reviews.Count,
                ["candidate_count"] = total,
                ["host_modified"] = false
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or
            UnauthorizedAccessException or InvalidOperationException or NotSupportedException or OverflowException)
        { return Error("REVIEW_PREPARE_REJECTED", ex.Message); }
    }

    [McpServerTool(Name = "steinberg_foley_finalize_audio_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("After a person explicitly confirms each chosen audio SHA, listening and rights assessment in an approval JSON, rehash local WAV files and write a NEW immutable-by-hash pre-DAW Foley manifest. No DAW placement.")]
    public JsonNode Finalize(string review_sheet, string explicit_approval_json, string output_manifest)
    {
        try
        {
            var reviewFile = LocalJson(review_sheet);
            var approvalsFile = LocalJson(explicit_approval_json);
            var target = FreshOutput(output_manifest);
            var reviewBytes = File.ReadAllBytes(reviewFile);
            var review = JsonNode.Parse(reviewBytes) as JsonObject;
            var approval = JsonNode.Parse(File.ReadAllBytes(approvalsFile)) as JsonObject;
            if (review?["schema"]?.GetValue<string>() != "steinberg-foley-audio-review-sheet-v1" ||
                review["status"]?.GetValue<string>() != "awaiting_explicit_human_approval" ||
                review["host_action"]?.GetValue<string>() != "none" ||
                review["any_audio_selected"]?.GetValue<bool>() != false ||
                review["events"] is not JsonArray events || events.Count is < 1 or > 16 ||
                approval?["schema"]?.GetValue<string>() != "steinberg-foley-explicit-approval-v1" ||
                approval["review_sha256"]?.GetValue<string>() != Hex(SHA256.HashData(reviewBytes)) ||
                approval["approvals"] is not JsonArray choices || choices.Count is < 1 or > 8)
                return Error("INVALID_APPROVAL", "Approval must bind exactly to the original review-sheet SHA-256.");

            // Also confirm the review sheet points to the same draft bytes, if still present.
            var draft = LocalJson(review["candidate_manifest_path"]?.GetValue<string>());
            if (HashFile(draft) != review["candidate_manifest_sha256"]?.GetValue<string>())
                return Error("DRAFT_CHANGED", "Original candidate manifest changed after review sheet generation.");

            var root = LocalRoot(review["library_root"]?.GetValue<string>());
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var entry in events)
            {
                if (entry is not JsonObject evt ||
                    string.IsNullOrWhiteSpace(evt["source_event_id"]?.GetValue<string>()) ||
                    !byId.TryAdd(evt["source_event_id"]!.GetValue<string>(), evt))
                    return Error("INVALID_REVIEW_EVENTS", "Invalid or duplicated review event.");
            }
            var exports = new JsonArray();
            var approvedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in choices)
            {
                if (item is not JsonObject choice)
                    return Error("INVALID_APPROVAL_ITEM", "Approval must be an object.");
                var id = choice["source_event_id"]?.GetValue<string>();
                var expectedHash = choice["sample_sha256"]?.GetValue<string>();
                var relative = choice["relative_path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || !approvedIds.Add(id) || !byId.TryGetValue(id, out var e) ||
                    !HashText(expectedHash) ||
                    choice["listened_to_sample"]?.GetValue<bool>() != true ||
                    choice["confirmed_matching_action"]?.GetValue<bool>() != true ||
                    choice["reviewed_rights_for_intended_use"]?.GetValue<bool>() != true ||
                    choice["approved_for_pre_daw_review_only"]?.GetValue<bool>() != true ||
                    choice["reviewer_note"]?.GetValue<string>() is not string note ||
                    string.IsNullOrWhiteSpace(note) || note.Length > 300)
                    return Error("EXPLICIT_REVIEW_REQUIRED", "An authorized human must affirm listening, sound/action, rights and pre-DAW review for every chosen SHA.");
                if (e["choices"] is not JsonArray candidates)
                    return Error("INVALID_REVIEW_CHOICES", "Missing review choices.");
                var matching = candidates.OfType<JsonObject>().Where(c =>
                    c["relative_path"]?.GetValue<string>() == relative &&
                    c["sample_sha256"]?.GetValue<string>() == expectedHash).ToArray();
                if (matching.Length != 1)
                    return Error("NOT_A_REVIEWED_CANDIDATE", "Approval must exactly identify a listed sample SHA.");
                var candidate = matching[0];
                var path = BoundAudio(root, relative, null);
                var length = new FileInfo(path).Length;
                if (length != candidate["sample_bytes"]?.GetValue<long>() ||
                    HashFile(path) != expectedHash ||
                    !SteinbergFoleyAudioProbe.TryProbe(path, out var header, out _) || header is null ||
                    header.SampleRate != candidate["sample_rate_hz"]?.GetValue<int>() ||
                    header.Channels != candidate["channels"]?.GetValue<int>() ||
                    header.BitsPerSample != candidate["bits_per_sample"]?.GetValue<int>() ||
                    Math.Abs(header.DurationSeconds - candidate["duration_seconds"]!.GetValue<double>()) > 0.000001)
                    return Error("SAMPLE_CHANGED_SINCE_APPROVAL", "Sample bytes or header no longer match the reviewed SHA-256.");

                exports.Add(new JsonObject
                {
                    ["source_event_id"] = id,
                    ["video_sha256"] = e["video_sha256"]?.GetValue<string>(),
                    ["contact_sha256"] = e["contact_sha256"]?.GetValue<string>(),
                    ["proposed_action_unconfirmed"] = e["proposed_action_unconfirmed"]?.GetValue<string>(),
                    ["sample_path"] = path,
                    ["relative_path"] = relative,
                    ["sample_sha256"] = expectedHash,
                    ["sample_size_bytes"] = length,
                    ["duration_seconds_header"] = Math.Round(header.DurationSeconds, 6),
                    ["sample_rate_hz_header"] = header.SampleRate,
                    ["channels_header"] = header.Channels,
                    ["bit_depth_header"] = header.BitsPerSample,
                    ["listening_and_rights"] = "user_attested_not_independently_verified",
                    ["reviewer_note"] = note,
                    ["audio_integrity_sha256_verified"] = true,
                    ["action_timing_verified"] = false,
                    ["daw_placement_approved"] = false,
                    ["host_action"] = "none"
                });
            }
            var result = new JsonObject
            {
                ["schema"] = "steinberg-foley-pre-daw-hashlocked-v1",
                ["status"] = "human_attested_samples_pending_daw_timing_review",
                ["review_sha256"] = Hex(SHA256.HashData(reviewBytes)),
                ["approval_file_sha256"] = HashFile(approvalsFile),
                ["library_root"] = root,
                ["approved_audio_count"] = exports.Count,
                ["not_approved_event_count"] = byId.Count - exports.Count,
                ["entries"] = exports,
                ["license_status"] = "human_attestation_not_independently_verified",
                ["sound_event_timing"] = "NOT_VERIFIED",
                ["automatic_placement"] = false,
                ["host_action"] = "none",
                ["warning"] = "Recheck SHA-256 immediately before any future DAW import; human rights claims are not independently verified. No timeline or DAW export was performed."
            };
            WriteNew(target, result);
            return new JsonObject
            {
                ["status"] = "OkPreDawReview",
                ["manifest_path"] = target,
                ["approved_audio_count"] = exports.Count,
                ["host_modified"] = false,
                ["requires_daw_timing_review"] = true
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or
            UnauthorizedAccessException or InvalidOperationException or NotSupportedException or OverflowException)
        { return Error("APPROVAL_REJECTED", ex.Message); }
    }

    private static string LocalJson(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute local JSON path required.");
        var full = Path.GetFullPath(path);
        var f = new FileInfo(full);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !f.Exists || f.Length is < 4 or > MaxJsonBytes ||
            f.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("JSON must be a small, regular local file.");
        return full;
    }

    private static string FreshOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute new output JSON path required.");
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)) || Path.Exists(full))
            throw new ArgumentException("Output must not exist and directory must exist.");
        return full;
    }

    private static string LocalRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute local library root required.");
        var full = Path.GetFullPath(path);
        var info = new DirectoryInfo(full);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Source library missing or redirected.");
        return full;
    }

    private static string BoundAudio(string root, string? relative, string? draftAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 ||
            relative.Contains('\\') || relative.StartsWith('/') ||
            relative.Split('/').Any(part => part is "" or "." or ".." or null) ||
            Path.IsPathFullyQualified(relative))
            throw new ArgumentException("Sample path must be a simple relative library file.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (draftAbsolute is not null && !full.Equals(Path.GetFullPath(draftAbsolute), comparison))
            throw new ArgumentException("Candidate's absolute path does not match the library-relative path.");
        var actualRelative = Path.GetRelativePath(root, full);
        if (actualRelative == ".." || actualRelative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            Path.IsPathRooted(actualRelative) ||
            !actualRelative.Replace('\\', '/').Equals(relative, comparison))
            throw new ArgumentException("Sample escapes the specified library root.");
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (dir is not null && dir.FullName.Length >= root.Length)
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Redirected sample directory is prohibited.");
            if (dir.FullName.Equals(root, comparison)) break;
            dir = dir.Parent;
        }
        if (dir is null || !dir.FullName.Equals(root, comparison))
            throw new ArgumentException("Sample is outside the library root.");
        var f = new FileInfo(full);
        if (!f.Exists || f.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            f.Length is < 44 or > MaxAudioBytes ||
            !full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sample must be a bounded, regular local WAV file.");
        return full;
    }

    private static void WriteNew(string output, JsonObject obj)
    {
        using var f = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(f, obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string HashFile(string file)
    {
        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Hex(SHA256.HashData(f));
    }
    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
    private static bool HashText(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message, ["host_modified"] = false
    };
}
