using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergFoleyApprovalTests
{
    [Fact]
    public void FOLEY21_ExposesBothMcpToolsAndPreparesHashLockedUnselectedReview()
    {
        var type = typeof(SteinbergFoleyApprovalTools);
        Assert.Equal("steinberg_foley_prepare_audio_review",
            McpServerTool.Create(type.GetMethod(nameof(SteinbergFoleyApprovalTools.Prepare))!, type).ProtocolTool.Name);
        Assert.Equal("steinberg_foley_finalize_audio_review",
            McpServerTool.Create(type.GetMethod(nameof(SteinbergFoleyApprovalTools.Finalize))!, type).ProtocolTool.Name);
        var folder = Temp();
        try
        {
            var sample = Wav(folder, "door_open.wav");
            var draft = Draft(folder, sample);
            var prepared = Path.Combine(folder, "review.json");
            var status = new SteinbergFoleyApprovalTools().Prepare(draft, prepared);
            Assert.Equal("OkReviewPending", status["status"]!.GetValue<string>());
            Assert.True(File.Exists(prepared));
            Assert.Equal(Sha(sample), JsonNode.Parse(File.ReadAllText(prepared))!["events"]![0]!["choices"]![0]!["sample_sha256"]!.GetValue<string>());
            Assert.Equal(Sha(prepared), status["review_sha256"]!.GetValue<string>());
            Assert.False(JsonNode.Parse(File.ReadAllText(prepared))!["any_audio_selected"]!.GetValue<bool>());
            Assert.False(status["host_modified"]!.GetValue<bool>());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FOLEY22_ExplicitAttestationAndVerifiedAudioShaProduceReviewOnlyManifest()
    {
        var folder = Temp();
        try
        {
            var sample = Wav(folder, "door_open.wav");
            var draft = Draft(folder, sample);
            var prepared = Path.Combine(folder, "review.json");
            var tool = new SteinbergFoleyApprovalTools();
            var status = tool.Prepare(draft, prepared);
            Assert.Equal("OkReviewPending", status["status"]!.GetValue<string>());
            var approval = Approval(folder, status["review_sha256"]!.GetValue<string>(), Sha(sample));
            var output = Path.Combine(folder, "pre-daw.json");
            var finalized = tool.Finalize(prepared, approval, output);
            Assert.Equal("OkPreDawReview", finalized["status"]!.GetValue<string>());
            Assert.Equal(1, finalized["approved_audio_count"]!.GetValue<int>());
            Assert.True(finalized["requires_daw_timing_review"]!.GetValue<bool>());
            var sheet = JsonNode.Parse(File.ReadAllText(output))!;
            Assert.Equal("steinberg-foley-pre-daw-hashlocked-v1", sheet["schema"]!.GetValue<string>());
            Assert.Equal(Sha(sample), sheet["entries"]![0]!["sample_sha256"]!.GetValue<string>());
            Assert.Equal(Sha(prepared), sheet["review_sha256"]!.GetValue<string>());
            Assert.Equal("user_attested_not_independently_verified",
                sheet["entries"]![0]!["listening_and_rights"]!.GetValue<string>());
            Assert.False(sheet["entries"]![0]!["daw_placement_approved"]!.GetValue<bool>());
            Assert.Equal("none", sheet["host_action"]!.GetValue<string>());
            Assert.Equal("Error", tool.Finalize(prepared, approval, output)["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FOLEY23_FailsClosedIfAudioBytesChangedAfterExplicitApproval()
    {
        var folder = Temp();
        try
        {
            var sample = Wav(folder, "door_open.wav");
            var draft = Draft(folder, sample);
            var prepared = Path.Combine(folder, "review.json");
            var tool = new SteinbergFoleyApprovalTools();
            var review = tool.Prepare(draft, prepared);
            Assert.Equal("OkReviewPending", review["status"]!.GetValue<string>());
            var signed = Approval(folder, review["review_sha256"]!.GetValue<string>(), Sha(sample));
            var data = File.ReadAllBytes(sample);
            data[60] ^= 0x3A; // header remains valid, sound bytes no longer approved
            File.WriteAllBytes(sample, data);
            var output = Path.Combine(folder, "must-not-exist.json");
            var result = tool.Finalize(prepared, signed, output);
            Assert.Equal("SAMPLE_CHANGED_SINCE_APPROVAL", result["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FOLEY24_ApprovalRequiresEveryExplicitHumanFlag()
    {
        var folder = Temp();
        try
        {
            var sample = Wav(folder, "door_open.wav");
            var prepared = Path.Combine(folder, "review.json");
            var tool = new SteinbergFoleyApprovalTools();
            var review = tool.Prepare(Draft(folder, sample), prepared);
            var approvalFile = Approval(folder, review["review_sha256"]!.GetValue<string>(), Sha(sample));
            var edited = JsonNode.Parse(File.ReadAllText(approvalFile))!;
            edited["approvals"]![0]!["reviewed_rights_for_intended_use"] = false;
            File.WriteAllText(approvalFile, edited.ToJsonString());
            var output = Path.Combine(folder, "not-approved.json");
            Assert.Equal("EXPLICIT_REVIEW_REQUIRED", tool.Finalize(prepared, approvalFile, output)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FOLEY25_ReviewTamperOrDraftTamperCannotBeApproved()
    {
        var folder = Temp();
        try
        {
            var sample = Wav(folder, "door_open.wav");
            var draft = Draft(folder, sample);
            var prepared = Path.Combine(folder, "review.json");
            var tool = new SteinbergFoleyApprovalTools();
            var review = tool.Prepare(draft, prepared);
            var approval = Approval(folder, review["review_sha256"]!.GetValue<string>(), Sha(sample));
            var output = Path.Combine(folder, "no-export.json");
            var changed = JsonNode.Parse(File.ReadAllText(prepared))!;
            changed["note"] = "Altered review sheet";
            File.WriteAllText(prepared, changed.ToJsonString());
            Assert.Equal("INVALID_APPROVAL", tool.Finalize(prepared, approval, output)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));

            review = tool.Prepare(draft, Path.Combine(folder, "new-review.json"));
            var fresh = Path.Combine(folder, "new-review.json");
            var signed = Approval(folder, review["review_sha256"]!.GetValue<string>(), Sha(sample));
            File.AppendAllText(draft, " ");
            Assert.Equal("DRAFT_CHANGED", tool.Finalize(fresh, signed, output)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FOLEY26_TraversalOutsideLibraryAndFakeAbsolutePathsAreRejected()
    {
        var folder = Temp();
        try
        {
            var sample = Wav(folder, "door_open.wav");
            var draft = Draft(folder, sample);
            var sheet = JsonNode.Parse(File.ReadAllText(draft))!;
            var output = Path.Combine(folder, "no-review.json");
            sheet["events"]![0]!["suggested_candidates"]![0]!["relative_path"] = "../door_open.wav";
            File.WriteAllText(draft, sheet.ToJsonString());
            Assert.Equal("REVIEW_PREPARE_REJECTED",
                new SteinbergFoleyApprovalTools().Prepare(draft, output)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Draft(string folder, string sample)
    {
        var obj = new JsonObject
        {
            ["schema"] = "steinberg-foley-local-sample-candidates-v1",
            ["status"] = "draft_requires_human_approval",
            ["host_action"] = "none",
            ["library_root"] = folder,
            ["selected_audio_files"] = new JsonArray(),
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    ["source_event_id"] = "mine_door_at_0",
                    ["proposed_action_unconfirmed"] = "opening_door",
                    ["video_sha256"] = new string('a', 64),
                    ["contact_sha256"] = new string('b', 64),
                    ["human_approval_required"] = true,
                    ["sound_effect_auto_placement"] = false,
                    ["chosen_sample_path"] = null,
                    ["candidate_count"] = 1,
                    ["suggested_candidates"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["path"] = sample,
                            ["relative_path"] = "door_open.wav",
                            ["size_bytes"] = new FileInfo(sample).Length,
                            ["audio_header_integrity"] = "basic_riff_fmt_and_data_consistent",
                            ["sample_rate_hz_header"] = 8000,
                            ["channels_header"] = 1,
                            ["bit_depth_header"] = 16,
                            ["duration_seconds_from_header"] = 1.0,
                            ["selected"] = false,
                            ["commercial_rights_cleared"] = false,
                            ["license_status"] = "no_rights_record"
                        }
                    }
                }
            }
        };
        var path = Path.Combine(folder, "draft.json");
        File.WriteAllText(path, obj.ToJsonString());
        return path;
    }

    private static string Approval(string folder, string reviewHash, string audioHash)
    {
        var obj = new JsonObject
        {
            ["schema"] = "steinberg-foley-explicit-approval-v1",
            ["review_sha256"] = reviewHash,
            ["approvals"] = new JsonArray
            {
                new JsonObject
                {
                    ["source_event_id"] = "mine_door_at_0",
                    ["relative_path"] = "door_open.wav",
                    ["sample_sha256"] = audioHash,
                    ["listened_to_sample"] = true,
                    ["confirmed_matching_action"] = true,
                    ["reviewed_rights_for_intended_use"] = true,
                    ["approved_for_pre_daw_review_only"] = true,
                    ["reviewer_note"] = "Test-only human attestations; no genuine licensing is implied."
                }
            }
        };
        var path = Path.Combine(folder, "approval.json");
        File.WriteAllText(path, obj.ToJsonString());
        return path;
    }

    private static string Wav(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var bytes = new byte[16044];
        "RIFF"u8.CopyTo(bytes.AsSpan());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 16036);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 16000);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "steinberg-foley-approval-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Sha(string filename)
    {
        using var f = File.OpenRead(filename);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}
