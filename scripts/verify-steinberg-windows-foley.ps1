#requires -Version 5.1
<#
.SYNOPSIS
 One-command, read-only Windows Steinberg Foley preimport acceptance.
.EXAMPLE
 .\scripts\verify-steinberg-windows-foley.ps1 -PreDawManifest C:\Foley\pre-daw.json -TimingReview C:\Foley\timing.json
#>
[CmdletBinding()]
param(
    [string]$PreDawManifest,
    [string]$TimingReview,
    [switch]$SelfTest,
    [string]$ResultsDirectory = (Join-Path $env:TEMP 'SteinbergFoleyWindowsAcceptance')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = [guid]::NewGuid().ToString('N')
$run = Join-Path $ResultsDirectory ('run-' + $runId)
New-Item -Path $run -ItemType Directory -Force | Out-Null
$reportFile = Join-Path $run 'foley-windows-acceptance.json'
$report = [ordered]@{
    schema='steinberg-foley-windows-entry-v1'; run_id=$runId
    source_branch=''; source_head=''; source_clean=$false
    status='NOT_RUN'; offline_gate='NOT_RUN'; offline_tests_passed=0
    offline_evidence=''; video_sha256='NOT_RUN'; sample_sha256='NOT_RUN'
    reviewed_timecode_tracks='NOT_RUN'; checked_samples=0; events=@()
    fps_metadata_probed=$false; complete_video_window_alignment_verified=$false
    independent_license_review=$false; cubase_nuendo_import='NOT_RUN'
    host_modified=$false; github_actions='NOT_USED'; message=''
    phase='INITIALIZE'; failure_code='NOT_RUN'; error_type=''
    stage_history=@(); offline_output_log=''
    selftest_requested=$SelfTest.IsPresent
    selftest_scope='NOT_RUN'
    environment=[ordered]@{
        os_name=$env:OS; powershell_version=$PSVersionTable.PSVersion.ToString()
        os_version=[Environment]::OSVersion.Version.ToString()
        is_64bit_os=[Environment]::Is64BitOperatingSystem
        dotnet_sdk_version='NOT_RUN'; git_version='NOT_RUN'
    }
}

function Enter-Stage([string]$name) {
    $report.phase=$name
    $report.stage_history+=([ordered]@{
        name=$name; status='STARTED'; utc=(Get-Date).ToUniversalTime().ToString('o')
    })
}
function Complete-Stage {
    if ($report.stage_history.Count -gt 0) {
        $report.stage_history[-1].status='PASS'
    }
}
function Hash-File([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Is-Sha([string]$value) {
    (-not [string]::IsNullOrWhiteSpace($value) -and $value -cmatch '^[0-9a-f]{64}$')
}
function Must-File([string]$path, [long]$max, [string]$ext='') {
    if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path)) {
        throw 'Absolute local path required.'
    }
    $full = [IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing source: $full" }
    $f = Get-Item -LiteralPath $full -Force
    if ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Symlink source refused: $full" }
    if ($f.Length -lt 4 -or $f.Length -gt $max) { throw "Invalid source size: $full" }
    if ($ext -and -not $full.EndsWith($ext,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsupported file extension: $full"
    }
    $full
}
function Json-File([string]$path) {
    $f = Must-File $path 2097152 '.json'
    Get-Content -LiteralPath $f -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Check-AudioPath([string]$rootPath,[string]$relative,[string]$absolute) {
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Length -gt 512 -or
        $relative.Contains('\') -or $relative.StartsWith('/') -or
        [IO.Path]::IsPathRooted($relative) -or
        @($relative.Split('/') | Where-Object { $_ -in @('','.','..') }).Count -gt 0) {
        throw 'Unsafe relative library path.'
    }
    $base = [IO.Path]::GetFullPath($rootPath).TrimEnd('\','/')
    $full = [IO.Path]::GetFullPath((Join-Path $base $relative.Replace('/',[IO.Path]::DirectorySeparatorChar)))
    $cmp = [StringComparison]::OrdinalIgnoreCase
    if (-not $full.StartsWith($base+[IO.Path]::DirectorySeparatorChar,$cmp) -or
        -not $full.Equals([IO.Path]::GetFullPath($absolute),$cmp)) {
        throw 'Audio does not belong to the approved library.'
    }
    $parent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($full))
    while ($null -ne $parent -and -not $parent.FullName.Equals($base,$cmp)) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Symlink ancestor directory refused.'
        }
        $parent = $parent.Parent
    }
    if ($null -eq $parent) { throw 'Audio parent is outside source library.' }
    Must-File $full 268435456 '.wav'
}
function Read-WavHeader([string]$path) {
    # Stream only headers, not audio; align with .NET SteinbergFoleyAudioProbe.
    $fs = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $br = [IO.BinaryReader]::new($fs)
    try {
        $enc = [Text.Encoding]::ASCII
        if ($enc.GetString($br.ReadBytes(4)) -cne 'RIFF') { throw 'No RIFF header.' }
        $riffSize = [long]$br.ReadUInt32()
        if ($riffSize+8 -ne $fs.Length -or $enc.GetString($br.ReadBytes(4)) -cne 'WAVE') {
            throw 'WAV chunk length or magic mismatched.'
        }
        $fmt=$false; $data=$false; $codec=0; $ch=0; $hz=0; $bps=0; $align=0; $bits=0
        [long]$dataBytes=0; $chunks=0
        while ($fs.Position -lt $fs.Length) {
            $chunks++
            if ($chunks -gt 128 -or $fs.Length-$fs.Position -lt 8) { throw 'WAV chunk limit exceeded.' }
            $tag=$enc.GetString($br.ReadBytes(4)); $size=[long]$br.ReadUInt32()
            $start=$fs.Position; $end=$start+$size+($size%2)
            if ($end -gt $fs.Length) { throw 'Truncated WAV chunk.' }
            if ($tag -ceq 'fmt ') {
                if ($fmt -or $size -lt 16 -or $size -gt 4096) { throw 'WAV fmt invalid.' }
                $codec=[int]$br.ReadUInt16()
                $ch=[int]$br.ReadUInt16()
                $hz=[long]$br.ReadUInt32()
                $bps=[long]$br.ReadUInt32()
                $align=[int]$br.ReadUInt16()
                $bits=[int]$br.ReadUInt16()
                $fmt=$true
            } elseif ($tag -ceq 'data') {
                if ($data) { throw 'Duplicate audio data chunk.' }
                $dataBytes=$size; $data=$true
            }
            $fs.Position=$end
        }
        if (-not $fmt -or -not $data -or $dataBytes -le 0 -or
            $codec -notin @(1,3) -or $ch -lt 1 -or $ch -gt 8 -or
            $hz -lt 8000 -or $hz -gt 192000 -or $bits -notin @(8,16,24,32) -or
            ($codec -eq 3 -and $bits -ne 32) -or $align -ne $ch*$bits/8 -or
            $bps -ne $hz*$align -or $dataBytes%$align -ne 0) {
            throw 'WAV PCM or float technical metadata inconsistent.'
        }
        [pscustomobject]@{hz=$hz; ch=$ch; bits=$bits; duration=[double]$dataBytes/$bps}
    } finally { $br.Dispose(); $fs.Dispose() }
}
function Rate-Nominal([int]$n,[int]$d) {
    $supported=@{
        '24/1'=24; '24000/1001'=24; '25/1'=25
        '30/1'=30; '30000/1001'=30; '50/1'=50
        '60/1'=60; '60000/1001'=60
    }
    $key="$n/$d"
    if (-not $supported.ContainsKey($key)) { throw 'Unsupported human-declared video rate.' }
    [int]$supported[$key]
}
function Frame-TC([long]$frame,[int]$nominal,[bool]$drop) {
    [long]$tc=$frame
    if ($drop) {
        $skip=if ($nominal -eq 30) {2} else {4}
        [long]$ten=$nominal*600-$skip*9
        [long]$one=$nominal*60-$skip
        [long]$dayFrame=$frame%($ten*6*24)
        [long]$blocks=[math]::Floor($dayFrame/$ten)
        [long]$rem=$dayFrame%$ten
        $tc=$dayFrame+$skip*9*$blocks
        if ($rem -ge $skip) { $tc += $skip*[math]::Floor(($rem-$skip)/$one) }
    }
    [long]$sec=[math]::Floor($tc/$nominal)
    $hh=[math]::Floor($sec/3600)%24
    $mm=[math]::Floor($sec/60)%60
    $ss=$sec%60
    $ff=$tc%$nominal
    $sep=if ($drop) {';'} else {':'}
    ('{0:00}:{1:00}:{2:00}{3}{4:00}' -f $hh,$mm,$ss,$sep,$ff)
}
function Verify-Inputs([string]$prePath,[string]$timingPath) {
    $original=Must-File $prePath 2097152 '.json'
    $p=Json-File $prePath
    $t=Json-File $timingPath
    if ($p.schema -cne 'steinberg-foley-pre-daw-hashlocked-v1' -or
        $p.status -cne 'human_attested_samples_pending_daw_timing_review' -or
        $p.host_action -cne 'none' -or $p.automatic_placement -ne $false -or
        $p.sound_event_timing -cne 'NOT_VERIFIED' -or
        @($p.entries).Count -lt 1 -or @($p.entries).Count -gt 8 -or
        [int]$p.approved_audio_count -ne @($p.entries).Count) {
        throw 'Approved audio manifest does not satisfy pre-DAW review contract.'
    }
    if ($t.schema -cne 'steinberg-foley-human-timing-review-v1' -or
        $t.host_action -cne 'none' -or
        $t.pre_daw_manifest_sha256 -cne (Hash-File $original) -or
        @($t.assignments).Count -ne @($p.entries).Count) {
        throw 'Timing review does not bind to original manifest SHA.'
    }
    $lib=[string]$p.library_root
    if (-not [IO.Path]::IsPathRooted($lib) -or -not (Test-Path -LiteralPath $lib -PathType Container)) {
        throw 'Source audio library missing.'
    }
    if ((Get-Item -LiteralPath $lib).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Symlink source library refused.'
    }
    $v=$t.video
    $n=[int]$v.fps_numerator; $d=[int]$v.fps_denominator
    $nom=Rate-Nominal $n $d
    $frames=[long]$v.total_frames
    $drop=$v.timecode_mode -ceq 'drop_frame'
    if ($frames -lt 1 -or $frames -gt 20000000 -or
        $v.timecode_mode -cnotin @('drop_frame','non_drop_frame') -or
        ($drop -and "$n/$d" -cnotin @('30000/1001','60000/1001'))) {
        throw 'Frame rate, count or drop-frame mode not supported.'
    }
    $video=Must-File ([string]$v.path) 2147483648
    if ([IO.Path]::GetExtension($video).ToLowerInvariant() -notin @('.mp4','.mov','.mkv','.webm') -or
        -not (Is-Sha ([string]$v.sha256)) -or (Hash-File $video) -cne $v.sha256) {
        throw 'Full-video bytes differ from declared SHA-256.'
    }
    $report.video_sha256='PASS'
    $entryMap=@{}
    foreach ($e in @($p.entries)) {
        $id=[string]$e.source_event_id
        if ([string]::IsNullOrWhiteSpace($id) -or $id.Length -gt 100 -or $entryMap.ContainsKey($id) -or
            $e.proposed_action_unconfirmed -cnotin @('opening_door','closing_door') -or
            -not (Is-Sha ([string]$e.sample_sha256)) -or
            -not (Is-Sha ([string]$e.video_sha256)) -or
            -not (Is-Sha ([string]$e.contact_sha256)) -or
            $e.audio_integrity_sha256_verified -ne $true -or
            $e.action_timing_verified -ne $false -or
            $e.daw_placement_approved -ne $false -or $e.host_action -cne 'none') {
            throw 'Duplicate or already-placed/invalid pre-DAW audio item.'
        }
        $entryMap[$id]=$e
    }
    $seen=@{}; $evidence=New-Object 'System.Collections.Generic.List[object]'
    foreach ($a in @($t.assignments)) {
        $id=[string]$a.source_event_id
        if (-not $entryMap.ContainsKey($id) -or $seen.ContainsKey($id)) {
            throw 'Unknown or duplicate source event in timing review.'
        }
        $seen[$id]=$true; $e=$entryMap[$id]
        $track=if ($e.proposed_action_unconfirmed -ceq 'opening_door') {
            'FOLEY_DOOR_OPEN'
        } else {
            'FOLEY_DOOR_CLOSE'
        }
        if ($a.track_name -cne $track) { throw 'Door action and track name disagree.' }
        if ($a.sample_sha256 -cne $e.sample_sha256 -or
            $a.reviewed_window_sha256 -cne $e.video_sha256) {
            throw 'Timing review has different audio or source-window SHA.'
        }
        [long]$frame=$a.event_frame; [long]$duration=$a.duration_frames
        if ($frame -lt 0 -or $frame -ge $frames -or
            $duration -lt 0 -or $duration -gt $frames-$frame -or
            $a.human_confirmed_video_frame -ne $true -or
            $a.human_confirmed_track -ne $true -or
            $a.human_confirmed_source_window_to_video_mapping -ne $true -or
            [string]::IsNullOrWhiteSpace([string]$a.reviewer_note) -or
            ([string]$a.reviewer_note).Length -gt 300) {
            throw 'Human review of source window, event frame or track missing.'
        }
        $audio=Check-AudioPath $lib ([string]$e.relative_path) ([string]$e.sample_path)
        if ((Get-Item -LiteralPath $audio).Length -ne [long]$e.sample_size_bytes -or
            (Hash-File $audio) -cne $e.sample_sha256) {
            throw 'Previously approved WAV SHA or size changed.'
        }
        $w=Read-WavHeader $audio
        if ($w.hz -ne [long]$e.sample_rate_hz_header -or
            $w.ch -ne [int]$e.channels_header -or
            $w.bits -ne [int]$e.bit_depth_header -or
            [math]::Abs($w.duration-[double]$e.duration_seconds_header) -gt 0.000001) {
            throw 'WAV header or duration no longer matches approved manifest.'
        }
        $evidence.Add([ordered]@{
            source_event_id=$id; track_name=$track
            event_frame=$frame
            seconds_from_video_start=[math]::Round(([double]$frame*$d/$n),9)
            timecode_from_video_start=(Frame-TC $frame $nom $drop)
            sample_sha256_rechecked=$e.sample_sha256
            reviewed_model_window_sha256=$e.video_sha256
            full_video_mapping='HUMAN_ATTESTED_NOT_INDEPENDENTLY_VERIFIED'
            host_action='none'
        })
        $report.checked_samples++
    }
    if ($seen.Count -ne $entryMap.Count) { throw 'Missing event assignment.' }
    $report.sample_sha256='PASS'
    $report.reviewed_timecode_tracks='PASS_HUMAN_DECLARED'
    $report.events=@($evidence | Sort-Object event_frame,source_event_id)
}

function Invoke-WindowsScriptSelfTest {
    # All mutable inputs are synthetic fixtures inside this unique run folder.
    # Does not inspect user audio, video, licensing or actual Cubase/Nuendo.
    $fixture=Join-Path $run 'selftest-pcm-8000hz.wav'
    $stream=[IO.File]::Open($fixture,[IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,[IO.FileShare]::None)
    $writer=[IO.BinaryWriter]::new($stream)
    try {
        $ascii=[Text.Encoding]::ASCII
        $writer.Write($ascii.GetBytes('RIFF'))
        $writer.Write([uint32]16036)
        $writer.Write($ascii.GetBytes('WAVE'))
        $writer.Write($ascii.GetBytes('fmt '))
        $writer.Write([uint32]16)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $writer.Write([uint32]8000)
        $writer.Write([uint32]16000)
        $writer.Write([uint16]2)
        $writer.Write([uint16]16)
        $writer.Write($ascii.GetBytes('data'))
        $writer.Write([uint32]16000)
        [byte[]]$samples=New-Object 'byte[]' 16000
        $writer.Write($samples)
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
    $file=Must-File $fixture 268435456 '.wav'
    $audio=Read-WavHeader $file
    if ($audio.hz -ne 8000 -or $audio.ch -ne 1 -or
        $audio.bits -ne 16 -or [math]::Abs($audio.duration-1.0) -gt 0.000001) {
        throw 'Native PowerShell RIFF PCM duration/header selftest failed.'
    }
    $before=Hash-File $file
    if (-not (Is-Sha $before)) {
        throw 'Windows SHA-256 native command did not return 64 hexadecimal characters.'
    }
    if ((Rate-Nominal 30000 1001) -ne 30 -or
        (Rate-Nominal 60000 1001) -ne 60 -or
        (Frame-TC 1800 30 $true) -cne '00:01:00;02' -or
        (Frame-TC 17982 30 $true) -cne '00:10:00;00' -or
        (Frame-TC 3600 60 $true) -cne '00:01:00;04' -or
        (Frame-TC 1800 30 $false) -cne '00:01:00:00') {
        throw 'Native PowerShell SMPTE drop-frame/non-drop calculation failed.'
    }
    # A content-only mutation must change the SHA, yet leave RIFF
    # technical header and duration unchanged.
    $mutation=[IO.File]::Open($file,[IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $mutation.Position=72
        $mutation.WriteByte([byte]0x37)
    } finally {
        $mutation.Dispose()
    }
    $after=Hash-File $file
    $metadata=Read-WavHeader $file
    if ($before -ceq $after -or
        [math]::Abs($metadata.duration-1.0) -gt 0.000001) {
        throw 'Native PowerShell byte-mutation SHA-256 regression failed.'
    }
    $report.selftest_scope='WINDOWS_POWERSHELL_SYNTHETIC_WAV_HASH_SMPTE_ONLY'
    $report.sample_sha256='PASS_SYNTHETIC_BYTE_MUTATION'
    $report.reviewed_timecode_tracks='PASS_SYNTHETIC_SMPTE_ONLY'
    $report.checked_samples=1
}

try {
    Enter-Stage 'WINDOWS_HOST'
    # Do not trust the user-editable OS environment variable as proof of
    # Windows: an env:OS override on Linux must never produce a native PASS.
    $isNativeWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
    if (-not $isNativeWindows -or $env:OS -cne 'Windows_NT') {
        $report.status='BLOCKED_NOT_WINDOWS'
        throw 'Native Windows PowerShell host is required for Windows-specific acceptance evidence.'
    }
    Complete-Stage
    if ($SelfTest.IsPresent) {
        Enter-Stage 'WINDOWS_NATIVE_SCRIPT_SELFTEST'
        Invoke-WindowsScriptSelfTest
        Complete-Stage
        $report.failure_code='NONE'
        $report.status='PASS_WINDOWS_SCRIPT_SELFTEST_ONLY'
        $report.message='Native Windows PowerShell synthetic WAV RIFF/header, byte-hash and SMPTE arithmetic passed. Offline .NET tests, real media review and Cubase/Nuendo import are NOT_RUN.'
    } else {
    Enter-Stage 'SOURCE_GIT_IDENTITY'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        $report.status='ENVIRONMENT_BLOCKED'; throw 'Git unavailable.'
    }
    $report.environment.git_version=[string](& git --version)
    $report.source_branch=[string](& git -C $root branch --show-current)
    $report.source_head=[string](& git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or
        $report.source_branch -cne 'feat/steinberg-dualmode-adapters-20261104' -or
        $report.source_head -notmatch '^[0-9a-f]{40}$') {
        $report.status='BLOCKED_SOURCE_BRANCH'
        throw 'Use the named Steinberg feature branch, not main or a detached HEAD.'
    }
    $changes=@(& git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -gt 0) {
        $report.status='BLOCKED_DIRTY_TREE'
        throw 'Working tree has changes not covered by the recorded Git SHA.'
    }
    $report.source_clean=$true
    Complete-Stage
    Enter-Stage 'DOTNET_OFFLINE_XSD_AND_MCP'
    $sdk=Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $sdk) {
        $report.environment.dotnet_sdk_version=[string](& dotnet --version)
    }
    $offlineScript=Join-Path $PSScriptRoot 'verify-steinberg-offline.ps1'
    if (-not (Test-Path -LiteralPath $offlineScript -PathType Leaf)) {
        $report.status='ENVIRONMENT_BLOCKED'; throw 'Strict-offline Steinberg gate missing.'
    }
    $offlineDir=Join-Path $run 'offline-dotnet'
    New-Item -Path $offlineDir -ItemType Directory | Out-Null
    $hostExecutable=(Get-Process -Id $PID).Path
    # Capture the actual child stdout/stderr in this run's evidence folder.
    # No network-policy workarounds, shell evaluation or background workers.
    $offlineLog=Join-Path $run 'dotnet-offline-output.log'
    $report.offline_output_log=$offlineLog
    & $hostExecutable -NoLogo -NoProfile -NonInteractive -File $offlineScript -ResultsDirectory $offlineDir *> $offlineLog
    $exitCode=$LASTEXITCODE
    $offlineFile=Join-Path $offlineDir 'steinberg-offline-verification.json'
    if (-not (Test-Path -LiteralPath $offlineFile -PathType Leaf)) {
        $report.status='ENVIRONMENT_BLOCKED'; throw 'Offline gate did not produce JSON evidence.'
    }
    $proof=Json-File $offlineFile
    $report.offline_evidence=$offlineFile
    $report.offline_gate=[string]$proof.status
    $report.offline_tests_passed=[int]$proof.passed_tests
    if ($exitCode -ne 0 -or $proof.status -cne 'PASS_OFFLINE_ONLY' -or
        [int]$proof.executed_tests -lt 1 -or
        [int]$proof.passed_tests -ne [int]$proof.executed_tests -or
        $proof.official_xsd_test -cne 'PASS' -or
        $proof.head -cne $report.source_head) {
        $report.status='ENVIRONMENT_BLOCKED'
        throw ('Offline dotnet/build/XSD/MCP gate failed: '+[string]$proof.status+'; '+[string]$proof.message)
    }
    Complete-Stage
    Enter-Stage 'HUMAN_REVIEW_INPUTS'
    if ([string]::IsNullOrWhiteSpace($PreDawManifest) -or
        [string]::IsNullOrWhiteSpace($TimingReview)) {
        $report.status='BLOCKED_MISSING_HUMAN_REVIEW_INPUTS'
        throw 'Offline gate passed; human-approved Foley audio manifest and timing review are missing.'
    }
    Complete-Stage
    Enter-Stage 'VIDEO_AND_AUDIO_SHA256_TIMECODE_TRACKS'
    Verify-Inputs $PreDawManifest $TimingReview
    Complete-Stage
    $report.failure_code='NONE'
    $report.status='PASS_FILE_ONLY_WINDOWS_PREFLIGHT'
    $report.message='No DAW mutation. Source SHA, offline tests, audio/video SHA and human-declared timecode/track inputs passed; real import NOT_RUN.'
    }
} catch {
    if ($report.status -eq 'NOT_RUN') { $report.status='FAIL_CLOSED' }
    # Stable machine-readable blocker code + phase: no misleading host PASS.
    $codes=@{
        'WINDOWS_HOST'='NOT_WINDOWS'
        'WINDOWS_NATIVE_SCRIPT_SELFTEST'='WINDOWS_SELFTEST_FAILURE'
        'SOURCE_GIT_IDENTITY'='SOURCE_BRANCH_OR_WORKTREE'
        'DOTNET_OFFLINE_XSD_AND_MCP'='DOTNET_RESTORE_BUILD_TEST_OR_XSD'
        'HUMAN_REVIEW_INPUTS'='HUMAN_APPROVAL_FILES_MISSING'
        'VIDEO_AND_AUDIO_SHA256_TIMECODE_TRACKS'='MEDIA_HASH_OR_TIMECODE_OR_TRACK'
    }
    $report.failure_code=[string]$codes[[string]$report.phase]
    if ([string]::IsNullOrWhiteSpace($report.failure_code)) {
        $report.failure_code='UNCLASSIFIED_PRECHECK_FAILURE'
    }
    $report.error_type=$_.Exception.GetType().FullName
    if ($report.stage_history.Count -gt 0) {
        $report.stage_history[-1].status='BLOCKED'
    }
    $report.message=$_.Exception.Message
} finally {
    ($report | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $reportFile -Encoding UTF8
    Write-Host ('Windows Foley preflight: '+$report.status)
    Write-Host ('Blocked phase: '+$report.phase)
    Write-Host ('Failure code: '+$report.failure_code)
    Write-Host ('Evidence: '+$reportFile)
    Write-Host 'Cubase/Nuendo real import: NOT_RUN'
}
if ($report.status -cnotin @('PASS_FILE_ONLY_WINDOWS_PREFLIGHT','PASS_WINDOWS_SCRIPT_SELFTEST_ONLY')) { exit 1 }
