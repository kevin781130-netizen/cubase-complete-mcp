using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using CubaseCompleteMcp.Server;

namespace CubaseCompleteMcp.Server.Tests;

/// <summary>
/// Validates actual exported ZIP XML against official Bitwig DAWproject v1.0 XSD files,
/// pinned from upstream commit ee4dcdde75940f30e14e55401a26955a58b8322b.
/// No download or GitHub Actions is required.
/// </summary>
public sealed class SteinbergDawprojectSchemaTests
{
    [Fact]
    public void DAW04_ExportedXmlValidatesAgainstOfficialProjectAndMetadataSchemas()
    {
        var root = Path.Combine(Path.GetTempPath(), "steinberg-xsd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wav = Path.Combine(root, "kick_120bpm.wav");
            File.WriteAllBytes(wav, TinyPcmWav());
            var plan = Path.Combine(root, "placement.json");
            var result = new SteinbergSampleTools().Plan(root, wav, plan, project_bpm: 120, bar: 2);
            Assert.Equal("OkDraft", result["status"]!.GetValue<string>());

            var file = Path.Combine(root, "export.dawproject");
            var exported = new SteinbergDawprojectTools().Export(root, [plan], file, approved: true);
            Assert.Equal("OkFileOnly", exported["status"]!.GetValue<string>());

            using var zip = ZipFile.OpenRead(file);
            ValidateEntry(zip, "project.xml", "Project.xsd");
            ValidateEntry(zip, "metadata.xml", "MetaData.xsd");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DAW05_OfficialProjectSchemaRejectsUnknownXml()
    {
        var settings = NewSettings("Project.xsd");
        Assert.Throws<XmlSchemaValidationException>(() =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<Project version=\"1.0\"><Incorrect/></Project>"));
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read()) { }
        });
    }

    private static void ValidateEntry(ZipArchive zip, string entryName, string schemaName)
    {
        var entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var reader = XmlReader.Create(stream, NewSettings(schemaName));
        while (reader.Read()) { }
    }

    private static XmlReaderSettings NewSettings(string schema)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DAWprojectSchema", schema);
        Assert.True(File.Exists(path), "Pinned DAWproject schema missing: " + path);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema
        };
        settings.Schemas.Add(null, path);
        return settings;
    }

    private static byte[] TinyPcmWav()
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write((uint)68);
        writer.Write("WAVEfmt "u8);
        writer.Write((uint)16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)48000);
        writer.Write((uint)96000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)32);
        for (int i = 0; i < 16; i++) writer.Write((short)0);
        return memory.ToArray();
    }
}
