using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Human-approved Foley -> portable DAWproject. The existing preimport gate
/// is the sole validator of approval, video identity, source paths and frames.
/// This tool never calls the Cubase broker or changes a live DAW session.
/// </summary>
[McpServerToolType]
public sealed class SteinbergFoleyDawprojectTools
{
    private const long MaxAudioBytes = 256L * 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    private sealed record FoleyClip(string EventId, string Track, string Path, string Hash,
        long Bytes, double Start, double Duration, int Channels, int SampleRate);

    [McpServerTool(Name = "steinberg_foley_export_dawproject", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("After independent human sample and frame/track review, export a NEW file-only DAWproject with FOLEY_DOOR_OPEN/CLOSE tracks at exact frame-derived seconds. Requires approved=true and a declared project BPM. No automatic Cubase/Nuendo import, no audio trimming or time-stretching.")]
    public JsonNode Export(string pre_daw_manifest, string timing_review_json,
        string output_dawproject, double project_bpm, bool approved = false)
    {
        if (!approved)
            return Error("APPROVAL_REQUIRED", "Explicitly approve copying the reviewed WAV files into a portable DAWproject.");
        if (!double.IsFinite(project_bpm) || project_bpm is < 30 or > 300)
            return Error("INVALID_BPM", "Choose a declared project BPM between 30 and 300; clips use seconds, not beat quantization.");

        string? gateReport = null;
        string? partial = null;
        try
        {
            var output = OutputPath(output_dawproject);
            gateReport = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "steinberg-foley-export-gate-" + Guid.NewGuid().ToString("N") + ".json");
            var check = new SteinbergFoleyPreimportTools().Check(pre_daw_manifest, timing_review_json, gateReport);
            if (check["status"]?.GetValue<string>() != "OkFileOnlyPreimport")
                return Error("PREIMPORT_BLOCKED", check["code"]?.GetValue<string>() ?? "Preimport file-only gate did not pass.");

            var report = JsonNode.Parse(File.ReadAllBytes(gateReport)) as JsonObject
                ?? throw new InvalidDataException("Missing preimport evidence.");
            if (report["schema"]?.GetValue<string>() != "steinberg-foley-preimport-acceptance-v1" ||
                report["status"]?.GetValue<string>() != "PASS_FILE_ONLY_READY_FOR_MANUAL_DAW_REVIEW" ||
                report["host_action"]?.GetValue<string>() != "none" ||
                report["actual_cubase_nuendo_import"]?.GetValue<string>() != "NOT_RUN" ||
                report["entries"] is not JsonArray entries || entries.Count is < 1 or > 8)
                return Error("INVALID_PREFLIGHT", "File-only preimport evidence missing or inconsistent.");

            // Reject changes to either human approval artifact after the preimport gate.
            var preSha = report["source_pre_daw_sha256"]!.GetValue<string>();
            var timingSha = report["source_timing_review_sha256"]!.GetValue<string>();
            if (!HashFile(pre_daw_manifest).Equals(preSha, StringComparison.Ordinal) ||
                !HashFile(timing_review_json).Equals(timingSha, StringComparison.Ordinal))
                return Error("APPROVAL_CHANGED", "Human-reviewed input changed after preimport validation.");

            var clips = new List<FoleyClip>();
            foreach (var value in entries)
            {
                if (value is not JsonObject e) throw new InvalidDataException("Invalid preimport entry.");
                var track = e["track_name_reviewed"]!.GetValue<string>();
                if (track is not ("FOLEY_DOOR_OPEN" or "FOLEY_DOOR_CLOSE"))
                    throw new InvalidDataException("Unexpected Foley track mapping.");
                var path = e["sample_path"]!.GetValue<string>();
                var hash = e["sample_sha256_revalidated"]!.GetValue<string>();
                var start = e["seconds_from_video_start"]!.GetValue<double>();
                if (!double.IsFinite(start) || start < 0 || hash.Length != 64)
                    throw new InvalidDataException("Invalid time or SHA evidence.");
                if (!SteinbergFoleyAudioProbe.TryProbe(path, out var wave, out _) || wave is null ||
                    wave.Channels is < 1 or > 2)
                    return Error("UNSUPPORTED_AUDIO", "Only validated mono/stereo PCM or IEEE-float WAV is exportable.");
                var bytes = new FileInfo(path).Length;
                if (bytes is < 44 or > MaxAudioBytes ||
                    !HashFile(path).Equals(hash, StringComparison.Ordinal))
                    return Error("SOURCE_CHANGED", "Approved source WAV changed after review.");
                clips.Add(new FoleyClip(e["source_event_id"]!.GetValue<string>(), track, path,
                    hash, bytes, start, wave.DurationSeconds, wave.Channels, wave.SampleRate));
            }

            var unique = clips.GroupBy(x => x.Hash, StringComparer.Ordinal)
                .Select(x => x.First()).ToList();
            if (unique.Sum(x => x.Bytes) > MaxPackageBytes)
                return Error("PACKAGE_TOO_LARGE", "Unique audio exceeds 512 MiB.");
            var zipPaths = unique.ToDictionary(x => x.Hash, x => "audio/" + x.Hash + ".wav",
                StringComparer.Ordinal);

            partial = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!,
                "." + System.IO.Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N") + ".partial");
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteXml(zip, "project.xml", ProjectXml(clips, zipPaths, project_bpm));
                WriteXml(zip, "metadata.xml", new XDocument(new XElement("MetaData",
                    new XElement("Title", "Foley Review Export"),
                    new XElement("Comment", "Manually confirmed cue frames; verify synchronization, media rights and clip lengths in the DAW."))));
                foreach (var clip in unique)
                {
                    var entry = zip.CreateEntry(zipPaths[clip.Hash], CompressionLevel.NoCompression);
                    using var destination = entry.Open();
                    using var source = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long copied = 0;
                    int count;
                    while ((count = source.Read(buffer)) > 0)
                    {
                        copied += count;
                        if (copied > MaxAudioBytes) throw new InvalidDataException("Audio grew during export.");
                        sha.AppendData(buffer, 0, count);
                        destination.Write(buffer, 0, count);
                    }
                    if (copied != clip.Bytes ||
                        !Convert.ToHexString(sha.GetHashAndReset()).Equals(clip.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Approved audio SHA-256 changed during packaging.");
                }
            }
            File.Move(partial, output, overwrite: false);
            partial = null;
            return new JsonObject
            {
                ["status"] = "OkPortableExport",
                ["output"] = output,
                ["tracks"] = clips.Select(x => x.Track).Distinct(StringComparer.Ordinal).Count(),
                ["clips"] = clips.Count,
                ["audio_files_embedded"] = unique.Count,
                ["pre_daw_sha256"] = preSha,
                ["timing_review_sha256"] = timingSha,
                ["time_unit"] = "seconds_from_video_start_human_confirmed",
                ["audio_duration"] = "full_WAV_untrimmed",
                ["actual_cubase_nuendo_import"] = "NOT_RUN",
                ["host_modified"] = false,
                ["warning"] = "Portable export only: source FPS and onset were human-declared; check sync, sample rights and complete audio durations after manual import."
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or InvalidOperationException or NotSupportedException or OverflowException)
        { return Error("EXPORT_REJECTED", e.Message); }
        finally
        {
            if (gateReport is not null) { try { File.Delete(gateReport); } catch (IOException) { } }
            if (partial is not null) { try { File.Delete(partial); } catch (IOException) { } }
        }
    }

    private static XDocument ProjectXml(List<FoleyClip> clips, Dictionary<string, string> media, double bpm)
    {
        int serial = 0;
        string Id() => "id" + (serial++).ToString(CultureInfo.InvariantCulture);
        var masterId = Id();
        var masterChannel = Id();
        var structure = new XElement("Structure");
        var lanes = new XElement("Lanes", new XAttribute("timeUnit", "seconds"), new XAttribute("id", Id()));
        foreach (var group in clips.GroupBy(x => x.Track, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var trackId = Id();
            structure.Add(new XElement("Track",
                new XAttribute("contentType", "audio"),
                new XAttribute("loaded", "true"),
                new XAttribute("id", trackId),
                new XAttribute("name", group.Key),
                new XElement("Channel",
                    new XAttribute("audioChannels", group.Max(x => x.Channels)),
                    new XAttribute("destination", masterChannel),
                    new XAttribute("role", "regular"),
                    new XAttribute("solo", "false"), new XAttribute("id", Id()))));
            var sequence = new XElement("Clips", new XAttribute("id", Id()));
            foreach (var clip in group.OrderBy(x => x.Start).ThenBy(x => x.EventId, StringComparer.Ordinal))
                sequence.Add(new XElement("Clip",
                    new XAttribute("name", System.IO.Path.GetFileNameWithoutExtension(clip.Path)),
                    new XAttribute("time", Format(clip.Start)),
                    new XAttribute("duration", Format(clip.Duration)),
                    new XAttribute("playStart", "0"),
                    new XAttribute("contentTimeUnit", "seconds"),
                    new XElement("Audio",
                        new XAttribute("duration", Format(clip.Duration)),
                        new XAttribute("channels", clip.Channels),
                        new XAttribute("sampleRate", clip.SampleRate),
                        new XAttribute("id", Id()),
                        new XElement("File", new XAttribute("path", media[clip.Hash])))));
            lanes.Add(new XElement("Lanes", new XAttribute("track", trackId),
                new XAttribute("id", Id()), sequence));
        }
        structure.Add(new XElement("Track",
            new XAttribute("contentType", "audio notes"),
            new XAttribute("loaded", "true"), new XAttribute("id", masterId),
            new XAttribute("name", "Master"),
            new XElement("Channel", new XAttribute("audioChannels", 2),
                new XAttribute("role", "master"), new XAttribute("solo", "false"),
                new XAttribute("id", masterChannel))));
        return new XDocument(new XElement("Project", new XAttribute("version", "1.0"),
            new XElement("Application", new XAttribute("name", "Steinberg Foley MCP"),
                new XAttribute("version", "0.1")),
            new XElement("Transport", new XElement("Tempo",
                    new XAttribute("unit", "bpm"), new XAttribute("value", Format(bpm)),
                    new XAttribute("id", Id())),
                new XElement("TimeSignature", new XAttribute("numerator", 4),
                    new XAttribute("denominator", 4), new XAttribute("id", Id()))),
            structure, new XElement("Arrangement", new XAttribute("id", Id()), lanes),
            new XElement("Scenes")));
    }

    private static void WriteXml(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(false), Indent = true, CloseOutput = false
        });
        document.Save(writer);
    }

    private static string OutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new ArgumentException("Absolute output path required.");
        var full = System.IO.Path.GetFullPath(path);
        var parent = new DirectoryInfo(System.IO.Path.GetDirectoryName(full)!);
        if (!full.EndsWith(".dawproject", StringComparison.OrdinalIgnoreCase) ||
            !parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            System.IO.Path.Exists(full))
            throw new ArgumentException("Provide a NEW .dawproject in an existing non-linked folder.");
        return full;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Format(double n) => n.ToString("0.###############", CultureInfo.InvariantCulture);
    private static JsonObject Error(string code, string message) => new()
    { ["status"] = "Error", ["code"] = code, ["message"] = message, ["host_modified"] = false };
}
