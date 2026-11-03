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
        self.assertIn("if ($report.status -cne 'PASS_FILE_ONLY_WINDOWS_PREFLIGHT') { exit 1 }", s)
        self.assertIn("fps_metadata_probed=$false", s)
        self.assertIn("complete_video_window_alignment_verified=$false", s)
        self.assertIn("independent_license_review=$false", s)


if __name__ == "__main__":
    unittest.main()
