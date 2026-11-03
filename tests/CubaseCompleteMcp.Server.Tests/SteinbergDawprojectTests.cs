using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergDawprojectTests
{
    [Fact]
    public void DAW01_ExporterIsDiscoverableAsAnMcpTool()
    {
        var type = typeof(SteinbergDawprojectTools);
        var mcp = McpServerTool.Create(type.GetMethod(nameof(SteinbergDawprojectTools.Export))!, type);
        Assert.Equal("steinberg_export_dawproject", mcp.ProtocolTool.Name);
        var properties = mcp.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var field in new[] { "library_root", "manifests", "output_dawproject", "approved" })
            Assert.True(properties.TryGetProperty(field, out _), field);
    }

    [Fact]
    public void DAW02_ThreePlacementsBecomeTwoAudioTracksWithBundledMediaAndCorrectSeconds()
    {
        var root = MakeRoot();
        try
        {
            var sampleTool = new SteinbergSampleTools();
            var folder = Directory.CreateDirectory(Path.Combine(root, "Samples"));
            var kick = Path.Combine(folder.FullName, "kick_120bpm.wav");
            var snare = Path.Combine(folder.FullName, "snare_120bpm.wav");
            File.WriteAllBytes(kick, Wav());
            File.WriteAllBytes(snare, Wav(amplitude: 30));
            var plan1 = Path.Combine(root, "kick1.json");
            var plan2 = Path.Combine(root, "snare1.json");
            var plan3 = Path.Combine(root, "kick2.json");
            Assert.Equal("OkDraft", sampleTool.Plan(root, kick, plan1, 120, bar: 1)["status"]!.GetValue<string>());
            Assert.Equal("OkDraft", sampleTool.Plan(root, snare, plan2, 120, bar: 2)["status"]!.GetValue<string>());
            Assert.Equal("OkDraft", sampleTool.Plan(root, kick, plan3, 120, bar: 3)["status"]!.GetValue<string>());
            var output = Path.Combine(root, "song.dawproject");
            var exporter = new SteinbergDawprojectTools();
            Assert.Equal("Error", exporter.Export(root, [plan1, plan2, plan3], output)["status"]!.GetValue<string>());
            Assert.False(File.Exists(output));

            var result = exporter.Export(root, [plan1, plan2, plan3], output, approved: true);
            Assert.Equal("OkFileOnly", result["status"]!.GetValue<string>());
            Assert.Equal(3, result["clips"]!.GetValue<int>());
            Assert.Equal(2, result["tracks"]!.GetValue<int>());
            Assert.Equal(2, result["audio_files_embedded"]!.GetValue<int>());
            Assert.False(result["host_modified"]!.GetValue<bool>());

            using var zip = ZipFile.OpenRead(output);
            Assert.NotNull(zip.GetEntry("metadata.xml"));
            var projectEntry = zip.GetEntry("project.xml");
            Assert.NotNull(projectEntry);
            using var reader = new StreamReader(projectEntry!.Open());
            var xml = XDocument.Parse(reader.ReadToEnd());
            var tracks = xml.Descendants("Track").ToList();
            Assert.Equal(3, tracks.Count); // KICK, SNARE and Master
            Assert.Equal(3, xml.Descendants("Clip").Count());
            var times = xml.Descendants("Clip")
                .Select(clip => double.Parse(clip.Attribute("time")!.Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(v => v).ToArray();
            Assert.Equal(new[] { 0.0, 2.0, 4.0 }, times);
            Assert.Equal(2, zip.Entries.Count(x => x.FullName.StartsWith("audio/", StringComparison.Ordinal)));
            Assert.All(xml.Descendants("File"), file =>
                Assert.NotNull(zip.GetEntry(file.Attribute("path")!.Value)));
            Assert.Equal("Error", exporter.Export(root, [plan1], output, true)["status"]!.GetValue<string>());
            Assert.Equal(Wav(), File.ReadAllBytes(kick));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DAW03_SourceHashTamperAndTempoMismatchRefusePackage()
    {
        var root = MakeRoot();
        try
        {
            var sampleTool = new SteinbergSampleTools();
            var sample = Path.Combine(root, "kick.wav");
            File.WriteAllBytes(sample, Wav());
            var first = Path.Combine(root, "first.json");
            var second = Path.Combine(root, "second.json");
            sampleTool.Plan(root, sample, first, 120);
            sampleTool.Plan(root, sample, second, 123);
            var export = new SteinbergDawprojectTools();
            var outPath = Path.Combine(root, "wrong-tempo.dawproject");
            Assert.Equal("TEMPO_MISMATCH",
                export.Export(root, [first, second], outPath, true)["code"]!.GetValue<string>());
            Assert.False(File.Exists(outPath));
            File.WriteAllBytes(sample, Wav(amplitude: 70));
            Assert.Equal("EXPORT_REJECTED",
                export.Export(root, [first], outPath, true)["code"]!.GetValue<string>());
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DAW06_MalformedRiffLengthAndBlockAlignmentRefusePackaging()
    {
        var root = MakeRoot();
        try
        {
            var source = Path.Combine(root, "kick.wav");
            var original = Wav();
            File.WriteAllBytes(source, original);
            var draft = Path.Combine(root, "draft.json");
            Assert.Equal("OkDraft", new SteinbergSampleTools().Plan(root, source, draft, 120)["status"]!.GetValue<string>());
            var output = Path.Combine(root, "rejected.dawproject");
            var exporter = new SteinbergDawprojectTools();

            var corruptLength = (byte[])original.Clone();
            corruptLength[4] = 0; // RIFF length must be 68, not 0.
            File.WriteAllBytes(source, corruptLength);
            Assert.Equal("EXPORT_REJECTED", exporter.Export(root, [draft], output, true)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));

            var corruptAlignment = (byte[])original.Clone();
            corruptAlignment[32] = 4; // 16-bit mono expects 2-byte block alignment.
            File.WriteAllBytes(source, corruptAlignment);
            Assert.Equal("EXPORT_REJECTED", exporter.Export(root, [draft], output, true)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));

            var corruptByteRate = (byte[])original.Clone();
            corruptByteRate[28] = 0; // 48k x 2 bytes must be 96000 bytes/sec.
            File.WriteAllBytes(source, corruptByteRate);
            Assert.Equal("EXPORT_REJECTED", exporter.Export(root, [draft], output, true)["code"]!.GetValue<string>());
            Assert.False(File.Exists(output));

            // Ensure all failures left the approved source path and the output untouched.
            File.WriteAllBytes(source, original);
            Assert.Equal("OkFileOnly", exporter.Export(root, [draft], output, true)["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string MakeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steinberg-dawproject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] Wav(short amplitude = 0)
    {
        // Real 16-bit mono PCM RIFF/WAVE, 48000Hz, 16 audio frames.
        var bytes = new byte[76];
        using var output = new MemoryStream(bytes);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(bytes.Length - 8));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write((uint)16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)48000);
        writer.Write((uint)96000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)32);
        for (var i = 0; i < 16; i++) writer.Write(amplitude);
        return bytes;
    }
}
