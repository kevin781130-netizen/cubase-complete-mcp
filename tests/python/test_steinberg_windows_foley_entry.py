"""Static safety contract for PowerShell preflight; NOT Windows execution.

The real .NET preimport method has its own separately executed .NET regression
tests. This test only examines the Windows entrypoint's source to ensure that
its wiring and safety boundaries cannot accidentally be removed.
"""
from pathlib import Path
import re
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "verify-steinberg-windows-foley.ps1"


class WindowsFoleyEntrypointSourceContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SCRIPT.read_text(encoding="utf-8")

    def test_single_entrypoint_reuses_existing_offline_gate(self):
        s = self.source
        self.assertIn("#requires -Version 5.1", s)
        self.assertIn("verify-steinberg-offline.ps1", s)
        self.assertIn("PASS_OFFLINE_ONLY", s)
        self.assertIn("official_xsd_test", s)
        self.assertIn("offline_tests_passed", s)
        self.assertIn("$proof.head -cne $report.source_head", s)

    def test_scm_pins_branch_sha_and_clean_worktree(self):
        s = self.source
        self.assertIn("feat/steinberg-dualmode-adapters-20261104", s)
        self.assertIn("git -C $root rev-parse HEAD", s)
        self.assertIn("git -C $root status --porcelain", s)
        self.assertIn("BLOCKED_DIRTY_TREE", s)
        self.assertIn("BLOCKED_SOURCE_BRANCH", s)

    def test_only_windows_host_can_pass_and_cubase_is_not_run(self):
        s = self.source
        self.assertIn("BLOCKED_NOT_WINDOWS", s)
        self.assertIn("Windows_NT", s)
        self.assertIn("cubase_nuendo_import='NOT_RUN'", s)
        self.assertIn("host_modified=$false", s)
        self.assertIn("github_actions='NOT_USED'", s)
        self.assertNotIn("PASS_WINDOWS_CUBASE_IMPORT", s)
        self.assertNotRegex(s, r"(?im)\b(?:Start-Process|Invoke-Expression|Invoke-WebRequest|Remove-Item)\b")

    def test_all_user_reviewed_file_inputs_are_mandatory(self):
        s = self.source
        self.assertIn("BLOCKED_MISSING_HUMAN_REVIEW_INPUTS", s)
        self.assertIn("steinberg-foley-pre-daw-hashlocked-v1", s)
        self.assertIn("steinberg-foley-human-timing-review-v1", s)
        self.assertIn("human_confirmed_source_window_to_video_mapping", s)
        self.assertIn("human_confirmed_video_frame", s)
        self.assertIn("human_confirmed_track", s)
        self.assertIn("FAIL_CLOSED", s)

    def test_sha256_actual_video_and_audio_recheck(self):
        s = self.source
        self.assertIn("Get-FileHash -LiteralPath $path -Algorithm SHA256", s)
        self.assertIn("(Hash-File $video) -cne $v.sha256", s)
        self.assertIn("(Hash-File $audio) -cne $e.sample_sha256", s)
        self.assertIn("Read-WavHeader $audio", s)
        self.assertIn("ReparsePoint", s)

    def test_ntsc_drop_frame_and_fixed_track_names(self):
        s = self.source
        for term in ("24000/1001", "30000/1001", "60000/1001",
                     "FOLEY_DOOR_OPEN", "FOLEY_DOOR_CLOSE"):
            self.assertIn(term, s)
        self.assertIn("Frame-TC $frame $nom $drop", s)
        self.assertIn("seconds_from_video_start", s)
        self.assertIn("reviewed_model_window_sha256", s)

    def test_no_host_pass_and_no_stale_evidence_reuse(self):
        s = self.source
        self.assertIn("[guid]::NewGuid()", s)
        self.assertIn("foley-windows-acceptance.json", s)
        self.assertIn("PASS_FILE_ONLY_WINDOWS_PREFLIGHT", s)
        self.assertIn("if ($report.status -cnotin @('PASS_FILE_ONLY_WINDOWS_PREFLIGHT','PASS_WINDOWS_SCRIPT_SELFTEST_ONLY')) { exit 1 }", s)
        self.assertIn("fps_metadata_probed=$false", s)
        self.assertIn("complete_video_window_alignment_verified=$false", s)
        self.assertIn("independent_license_review=$false", s)


    def test_structured_stage_history_and_failure_cause(self):
        s = self.source
        self.assertIn("stage_history=@", s)
        self.assertIn("function Enter-Stage", s)
        self.assertIn("function Complete-Stage", s)
        for stage in ("WINDOWS_HOST", "SOURCE_GIT_IDENTITY",
                      "DOTNET_OFFLINE_XSD_AND_MCP", "HUMAN_REVIEW_INPUTS",
                      "VIDEO_AND_AUDIO_SHA256_TIMECODE_TRACKS"):
            self.assertIn("Enter-Stage '"+stage+"'", s)
        self.assertIn("failure_code='NOT_RUN'", s)
        self.assertIn("$report.failure_code='NONE'", s)
        self.assertIn("UNCLASSIFIED_PRECHECK_FAILURE", s)
        self.assertIn("$report.stage_history[-1].status='BLOCKED'", s)
        self.assertIn("$report.error_type=$_.Exception.GetType().FullName", s)

    def test_environment_diagnostics_are_non_destructive(self):
        s = self.source
        for item in ("powershell_version", "os_version", "is_64bit_os",
                     "dotnet_sdk_version", "git_version"):
            self.assertIn(item, s)
        self.assertIn("[Environment]::OSVersion.Version.ToString()", s)
        self.assertIn("Get-Command dotnet -ErrorAction SilentlyContinue", s)
        self.assertIn("Get-Command git -ErrorAction SilentlyContinue", s)
        self.assertNotIn("Get-ChildItem Env:", s)

    def test_retain_real_offline_log_and_evidence_in_current_run(self):
        s = self.source
        self.assertIn("dotnet-offline-output.log", s)
        self.assertIn("$report.offline_output_log=$offlineLog", s)
        self.assertIn("-ResultsDirectory $offlineDir *> $offlineLog", s)
        self.assertIn("$exitCode=$LASTEXITCODE", s)
        self.assertIn("steinberg-offline-verification.json", s)
        self.assertIn("'BLOCKED_MISSING_HUMAN_REVIEW_INPUTS'", s)
        self.assertIn("ConvertTo-Json -Depth 12", s)

    def test_fail_closed_when_offline_gate_has_failing_or_stale_evidence(self):
        s = self.source
        self.assertIn("$exitCode -ne 0", s)
        self.assertIn("$proof.status -cne 'PASS_OFFLINE_ONLY'", s)
        self.assertIn("$proof.passed_tests -ne [int]$proof.executed_tests", s)
        self.assertIn("$proof.official_xsd_test -cne 'PASS'", s)
        self.assertIn("$proof.head -cne $report.source_head", s)
        self.assertIn("$report.status='ENVIRONMENT_BLOCKED'", s)

    def test_phase_names_and_status_cannot_fake_windows_host_acceptance(self):
        s = self.source
        self.assertIn("cubase_nuendo_import='NOT_RUN'", s)
        self.assertIn("fps_metadata_probed=$false", s)
        self.assertIn("independent_license_review=$false", s)
        self.assertIn("if ($report.status -cnotin @('PASS_FILE_ONLY_WINDOWS_PREFLIGHT','PASS_WINDOWS_SCRIPT_SELFTEST_ONLY')) { exit 1 }", s)
        self.assertNotIn("PASS_WINDOWS_CUBASE_IMPORT", s)


    def test_selftest_is_explicit_not_the_full_foley_gate(self):
        s = self.source
        self.assertIn("[switch]$SelfTest", s)
        self.assertIn("if ($SelfTest.IsPresent)", s)
        self.assertEqual(s.count("Enter-Stage 'SOURCE_GIT_IDENTITY'"), 1)
        self.assertLess(
            s.index("Enter-Stage 'SOURCE_GIT_IDENTITY'"),
            s.index("if ($SelfTest.IsPresent) {", s.index("Enter-Stage 'WINDOWS_HOST'"))
        )
        self.assertIn("PASS_WINDOWS_SCRIPT_SELFTEST_ONLY", s)
        self.assertIn("cubase_nuendo_import='NOT_RUN'", s)
        self.assertIn("offline_gate='NOT_RUN'", s)
        self.assertIn("selftest_scope='NOT_RUN'", s)

    def test_native_selftest_uses_only_guid_isolated_synthetic_pcm(self):
        s = self.source
        self.assertIn("Invoke-WindowsScriptSelfTest", s)
        self.assertIn("Join-Path $run 'selftest-pcm-8000hz.wav'", s)
        self.assertIn("[IO.FileMode]::CreateNew", s)
        self.assertIn("$writer.Write([uint32]16036)", s)
        self.assertIn("$writer.Write([uint32]16000)", s)
        self.assertIn("Read-WavHeader $file", s)
        self.assertIn("Must-File $fixture", s)
        self.assertIn("New-Object 'byte[]' 16000", s)

    def test_native_selftest_has_actual_file_sha_mutation_negative(self):
        s = self.source
        self.assertIn("$before=Hash-File $file", s)
        self.assertIn("$mutation.Position=72", s)
        self.assertIn("$mutation.WriteByte([byte]0x37)", s)
        self.assertIn("$after=Hash-File $file", s)
        self.assertIn("$before -ceq $after", s)
        self.assertIn("WINDOWS_SELFTEST_FAILURE", s)

    def test_native_selftest_covers_ntsc_drop_frame_and_non_drop(self):
        s = self.source
        self.assertIn("(Frame-TC 1800 30 $true)", s)
        self.assertIn("'00:01:00;02'", s)
        self.assertIn("(Frame-TC 17982 30 $true)", s)
        self.assertIn("'00:10:00;00'", s)
        self.assertIn("(Frame-TC 3600 60 $true)", s)
        self.assertIn("'00:01:00;04'", s)
        self.assertIn("(Frame-TC 1800 30 $false)", s)
        self.assertIn("'00:01:00:00'", s)

    def test_selftest_maintains_precise_success_and_failure_outputs(self):
        s = self.source
        self.assertIn("Enter-Stage 'WINDOWS_NATIVE_SCRIPT_SELFTEST'", s)
        self.assertIn("Complete-Stage", s)
        self.assertIn("$report.failure_code='NONE'", s)
        self.assertIn("WINDOWS_POWERSHELL_SYNTHETIC_WAV_HASH_SMPTE_ONLY", s)
        self.assertIn("PASS_WINDOWS_SCRIPT_SELFTEST_ONLY", s)
        self.assertIn("if (-not $isNativeWindows -or $env:OS -cne 'Windows_NT')", s)
        self.assertIn("failure_code=[string]$codes[[string]$report.phase]", s)

    def test_native_windows_runtime_cannot_be_spoofed_by_env_os(self):
        s = self.source
        self.assertIn("[System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(", s)
        self.assertIn("[System.Runtime.InteropServices.OSPlatform]::Windows)", s)
        self.assertIn("if (-not $isNativeWindows -or $env:OS -cne 'Windows_NT')", s)
        self.assertIn("BLOCKED_NOT_WINDOWS", s)
        self.assertIn("PASS_WINDOWS_SCRIPT_SELFTEST_ONLY", s)


    def test_selftest_and_full_gate_are_bound_to_the_same_origin_commit(self):
        s = self.source
        self.assertIn("origin_feature_head=''", s)
        self.assertIn("git -C $root rev-parse --verify refs/remotes/origin/feat/steinberg-dualmode-adapters-20261104", s)
        self.assertIn("$report.origin_feature_head -cne $report.source_head", s)
        self.assertIn("Local HEAD differs from origin feature branch", s)
        self.assertEqual(s.count("Enter-Stage 'SOURCE_GIT_IDENTITY'"), 1)
        self.assertLess(
            s.index("Enter-Stage 'SOURCE_GIT_IDENTITY'"),
            s.index("if ($SelfTest.IsPresent) {", s.index("Enter-Stage 'WINDOWS_HOST'"))
        )
        self.assertIn("BLOCKED_DIRTY_TREE", s)


if __name__ == "__main__":
    unittest.main()
