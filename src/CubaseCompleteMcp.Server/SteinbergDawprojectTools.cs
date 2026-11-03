using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Pure file exporter for the approved sample-placement drafts.
/// Does not communicate with the Cubase broker, launch programs, or edit an open project.
/// DAWproject support is a separate host-import capability, not a MIDI Remote API 1.0 claim.
/// </summary>
[McpServerToolType]
public sealed class SteinbergDawprojectTools
{
    private const int MaxPlacements = 64;
    private const long MaxSourceBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;

    [McpServerTool(Name = "steinberg_export_dawproject", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("After explicit approval, package 1–64 previously created sample-placement JSON drafts and their verified WAV sources into a NEW multitrack .dawproject ZIP. Cubase 14/15 import is the initial target; Nuendo and older versions require separate validation. Never alters a DAW project.")]
    public JsonNode Export(string library_root, string[] manifests, string output_dawproject, bool approved = false)
    {
        if (!approved) return Error("APPROVAL_REQUIRED", "Set approved=true only after reviewing the sample, timing and output paths.");
        if (manifests is not { Length: >= 1 and <= MaxPlacements })
            return Error("INVALID_INPUT", "Provide 1 to 64 manifest paths.");
        string? staging = null;
        try
        {
            var root = RequireLibraryRoot(library_root);
            var destination = RequireOutput(output_dawproject);
            var clips = new List<Placement>();
            foreach (var path in manifests)
                clips.Add(ParsePlacement(path, root));
            if (clips.GroupBy(x => x.ProjectBpm).Count() != 1 ||
                clips.GroupBy(x => x.BeatsPerBar).Count() != 1)
                return Error("TEMPO_MISMATCH", "All plans must agree on constant project BPM and numerator.");

            var uniqueAudio = clips.GroupBy(x => x.Sha256, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
            if (uniqueAudio.Sum(x => x.SourceBytes) > MaxTotalBytes)
                return Error("PACKAGE_TOO_LARGE", "Total unique WAV payload exceeds 512 MiB.");
            foreach (var group in clips.GroupBy(x => x.Sha256, StringComparer.OrdinalIgnoreCase))
                if (group.Any(x => x.SourceBytes != group.First().SourceBytes))
                    return Error("SOURCE_MISMATCH", "A shared hash is associated with conflicting sample sizes.");

            var zipPaths = uniqueAudio.ToDictionary(x => x.Sha256,
                x => "audio/" + x.Sha256.ToLowerInvariant() + ".wav",
                StringComparer.OrdinalIgnoreCase);

            var projectXml = BuildProjectXml(clips, zipPaths);
            var metadataXml = new XDocument(new XElement("MetaData",
                new XElement("Title", "Steinberg Sample Arrangement"),
                new XElement("Comment", "Review timing and sample licenses before using this portable project.")));
            staging = Path.Combine(Path.GetDirectoryName(destination)!,
                "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".partial");

            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteXml(zip, "project.xml", projectXml);
                WriteXml(zip, "metadata.xml", metadataXml);
                foreach (var clip in uniqueAudio)
                {
                    var entry = zip.CreateEntry(zipPaths[clip.Sha256], CompressionLevel.NoCompression);
                    using var entryStream = entry.Open();
                    using var source = new FileStream(clip.SourcePath, FileMode.Open,
                        FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    long copied = 0;
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copied += count;
                        if (copied > MaxSourceBytes) throw new InvalidDataException("Source grew above size limit.");
                        digest.AppendData(buffer, 0, count);
                        entryStream.Write(buffer, 0, count);
                    }
                    if (copied != clip.SourceBytes ||
                        !Convert.ToHexString(digest.GetHashAndReset()).Equals(clip.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Source WAV differs from its approved manifest hash.");
                }
            }

            // Never overwrite an existing export. Failure deletes only our newly created staging file.
            File.Move(staging, destination, overwrite: false);
            staging = null;
            return new JsonObject
            {
                ["status"] = "OkFileOnly",
                ["output"] = destination,
                ["tracks"] = clips.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["clips"] = clips.Count,
                ["audio_files_embedded"] = uniqueAudio.Count,
                ["host_modified"] = false,
                ["validation"] = "Source SHA-256 matched during ZIP creation. XML schema and Cubase/Nuendo import NOT_RUN.",
                ["host_support"] = "Cubase 14/15 DAWproject import target; Nuendo, Cubase 12/13 not validated"
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException
            or OverflowException)
        { return Error("EXPORT_REJECTED", e.Message); }
        finally
        {
            if (staging is not null)
            {
                try { File.Delete(staging); } catch (IOException) { /* preserve evidence if locked */ }
            }
        }
    }

    private static Placement ParsePlacement(string filename, string root)
    {
        if (!Path.IsPathFullyQualified(filename)) throw new ArgumentException("Manifest path must be absolute.");
        var path = Path.GetFullPath(filename);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)) throw new ArgumentException("Missing .json manifest.");
        if (new FileInfo(path).Length > 64 * 1024)
            throw new ArgumentException("Manifest exceeds 64 KiB.");

        var doc = JsonNode.Parse(File.ReadAllText(path)) ??
            throw new InvalidDataException("Empty manifest.");
        if (Text(doc, "schema") != "steinberg-sample-placement-plan-v1"
            || Text(doc, "status") != "draft_requires_approval"
            || Text(doc, "host_action") != "none"
            || Text(doc, "time_stretch") != "not_applied_requires_review")
            throw new InvalidDataException("Not an approved-format placement draft.");
        var source = doc["source"] ?? throw new InvalidDataException("Missing source.");
        var project = doc["project"] ?? throw new InvalidDataException("Missing project.");
        var point = doc["placement"] ?? throw new InvalidDataException("Missing placement.");
        var sourcePath = RequireContainedWav(root, Text(source, "path"));
        var sha = Text(source, "sha256");
        if (sha.Length != 64 || !sha.All(c => Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid source SHA-256.");
        var size = Long(source, "size_bytes");
        if (size < 44 || size > MaxSourceBytes || new FileInfo(sourcePath).Length != size)
            throw new InvalidDataException("Source is missing, changed, or too large.");
        var bpm = Double(project, "bpm");
        var numerator = Integer(project, "beats_per_bar");
        var ppq = Integer(project, "ppq");
        var beatUnit = Integer(project, "beat_unit");
        if (!double.IsFinite(bpm) || bpm < 30 || bpm > 300 ||
            numerator < 1 || numerator > 16 || ppq != 960 || beatUnit != 4)
            throw new InvalidDataException("Unsupported tempo, meter, or PPQ.");

        var bar = Integer(point, "bar");
        var beat = Integer(point, "beat");
        var sixteenth = Integer(point, "sixteenth");
        if (bar < 1 || bar > 100000 || beat < 1 || beat > numerator || sixteenth < 0 || sixteenth > 3)
            throw new InvalidDataException("Invalid placement grid.");
        var ticks = ((long)(bar - 1) * numerator + beat - 1) * 960 + sixteenth * 240;
        if (Long(point, "tick") != ticks)
            throw new InvalidDataException("Timeline tick mismatch.");
        var seconds = ticks / 960.0 * 60.0 / bpm;
        if (Math.Abs(Double(point, "seconds_from_project_start") - Math.Round(seconds, 6)) > 0.000002)
            throw new InvalidDataException("Time/tempo mapping mismatch.");
        var category = Text(source, "category_guess");
        if (!new[] { "kick", "snare", "clap", "hihat", "tom", "cymbal", "percussion", "bass", "loop", "fx", "other" }
            .Contains(category, StringComparer.OrdinalIgnoreCase))
            category = "other";
        var wave = ReadWaveInfo(sourcePath);
        return new Placement(sourcePath, sha, size, category.ToLowerInvariant(),
            bpm, numerator, seconds, wave.Duration, wave.Channels, wave.SampleRate);
    }

    private static XDocument BuildProjectXml(List<Placement> clips, Dictionary<string, string> zipPaths)
    {
        var idCounter = 0;
        string Id() => "id" + idCounter++.ToString(CultureInfo.InvariantCulture);
        var masterTrack = Id();
        var masterChannel = Id();
        var trackGroups = clips.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase).ToList();
        var structure = new XElement("Structure");
        var rootLanes = new XElement("Lanes", new XAttribute("timeUnit", "seconds"), new XAttribute("id", Id()));
        foreach (var group in trackGroups)
        {
            var trackId = Id();
            var track = new XElement("Track",
                new XAttribute("contentType", "audio"),
                new XAttribute("loaded", "true"),
                new XAttribute("id", trackId),
                new XAttribute("name", group.Key.ToUpperInvariant()),
                new XElement("Channel",
                    new XAttribute("audioChannels", Math.Max(1, group.Max(x => x.Channels))),
                    new XAttribute("destination", masterChannel),
                    new XAttribute("role", "regular"),
                    new XAttribute("solo", "false"),
                    new XAttribute("id", Id())));
            structure.Add(track);
            var clipSequence = new XElement("Clips", new XAttribute("id", Id()));
            foreach (var clip in group.OrderBy(x => x.StartSeconds))
            {
                clipSequence.Add(new XElement("Clip",
                    new XAttribute("name", Path.GetFileNameWithoutExtension(clip.SourcePath)),
                    new XAttribute("time", Format(clip.StartSeconds)),
                    new XAttribute("duration", Format(clip.Duration)),
                    new XAttribute("playStart", "0"),
                    new XAttribute("contentTimeUnit", "seconds"),
                    new XElement("Audio",
                        new XAttribute("duration", Format(clip.Duration)),
                        new XAttribute("channels", clip.Channels),
                        new XAttribute("sampleRate", clip.SampleRate),
                        new XAttribute("id", Id()),
                        new XElement("File", new XAttribute("path", zipPaths[clip.Sha256])))));
            }
            rootLanes.Add(new XElement("Lanes", new XAttribute("track", trackId),
                new XAttribute("id", Id()), clipSequence));
        }
        structure.Add(new XElement("Track", new XAttribute("contentType", "audio notes"),
            new XAttribute("loaded", "true"), new XAttribute("id", masterTrack),
            new XAttribute("name", "Master"),
            new XElement("Channel", new XAttribute("audioChannels", 2),
                new XAttribute("role", "master"), new XAttribute("solo", "false"),
                new XAttribute("id", masterChannel))));
        return new XDocument(new XElement("Project", new XAttribute("version", "1.0"),
            new XElement("Application", new XAttribute("name", "Steinberg Foley MCP"),
                new XAttribute("version", "0.1")),
            new XElement("Transport",
                new XElement("Tempo", new XAttribute("unit", "bpm"),
                    new XAttribute("value", Format(clips[0].ProjectBpm)), new XAttribute("id", Id())),
                new XElement("TimeSignature", new XAttribute("numerator", clips[0].BeatsPerBar),
                    new XAttribute("denominator", 4), new XAttribute("id", Id()))),
            structure,
            new XElement("Arrangement", new XAttribute("id", Id()), rootLanes),
            new XElement("Scenes")));
    }

    private static void WriteXml(ZipArchive zip, string path, XDocument doc)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(false), Indent = true, CloseOutput = false
        });
        doc.Save(writer);
    }

    private static (int Channels, int SampleRate, double Duration) ReadWaveInfo(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (file.Length < 44 || new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("Only standard RIFF WAV files are supported.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Invalid WAV signature.");
        int channels = 0, rate = 0, blockAlign = 0;
        long dataBytes = -1;
        while (file.Position + 8 <= file.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var end = checked(file.Position + size);
            if (end > file.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (chunk == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Invalid WAV fmt.");
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                blockAlign = reader.ReadUInt16();
                reader.ReadUInt16();
                if (format is not (1 or 3))
                    throw new InvalidDataException("Only PCM / IEEE-float RIFF WAV is supported.");
            }
            else if (chunk == "data") dataBytes = size;
            file.Position = end + (size % 2);
        }
        if (channels is < 1 or > 2 || rate is < 8000 or > 384000 || blockAlign < channels ||
            dataBytes < 1 || dataBytes % blockAlign != 0)
            throw new InvalidDataException("Missing or unsupported WAV audio format.");
        return (channels, rate, dataBytes / (double)(blockAlign * rate));
    }

    private static string RequireLibraryRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Library root must be absolute.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var dir = new DirectoryInfo(root);
        if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException("Library root unavailable or linked.");
        return root;
    }

    private static string RequireContainedWav(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Source must be absolute.");
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
            !Path.GetExtension(full).Equals(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Source must be an existing WAV inside the approved library.");
        for (FileSystemInfo? item = new FileInfo(full); item is not null; item = item switch
        {
            FileInfo f => f.Directory,
            DirectoryInfo d => d.Parent,
            _ => null
        })
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Linked source paths are not accepted.");
            if (item.FullName.Equals(root, comparison)) break;
        }
        return full;
    }

    private static string RequireOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Output must be absolute.");
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".dawproject", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.GetDirectoryName(full)) || File.Exists(full))
            throw new ArgumentException("Choose a NEW .dawproject file in an existing directory.");
        return full;
    }

    private static string Text(JsonNode node, string key) =>
        node[key]?.GetValue<string>() ?? throw new InvalidDataException("Missing " + key);
    private static int Integer(JsonNode node, string key) =>
        node[key]?.GetValue<int>() ?? throw new InvalidDataException("Missing " + key);
    private static long Long(JsonNode node, string key) =>
        node[key]?.GetValue<long>() ?? throw new InvalidDataException("Missing " + key);
    private static double Double(JsonNode node, string key) =>
        node[key]?.GetValue<double>() ?? throw new InvalidDataException("Missing " + key);
    private static string Format(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    private sealed record Placement(string SourcePath, string Sha256, long SourceBytes,
        string Category, double ProjectBpm, int BeatsPerBar, double StartSeconds,
        double Duration, int Channels, int SampleRate);

    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error", ["code"] = code, ["message"] = message, ["host_modified"] = false
    };
}
