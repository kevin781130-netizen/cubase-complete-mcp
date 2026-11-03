using System.Reflection;
using System.Text;
using CubaseCompleteMcp.Server;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server.Tests;

public sealed class SteinbergModesContractTests
{
    [Fact]
    public void MODES01_ReportsFoleyAsNotImplementedAndNoHostValidationClaim()
    {
        var result = new SteinbergModesTools().Status();
        Assert.Equal("Ok", result["status"]!.GetValue<string>());
        Assert.Equal("NotImplemented", result["foley_mode"]!["status"]!.GetValue<string>());
        Assert.Contains("not validated", result["host_support"]!.GetValue<string>());
    }

    [Fact]
    public void MODES02_ExposesTheExpectedMcpToolSchemas()
    {
        var type = typeof(SteinbergModesTools);
        var status = McpServerTool.Create(type.GetMethod(nameof(SteinbergModesTools.Status))!, type);
        var tempo = McpServerTool.Create(type.GetMethod(nameof(SteinbergModesTools.ArrangeTempo))!, type);
        var midi = McpServerTool.Create(type.GetMethod(nameof(SteinbergModesTools.ArrangeMidi))!, type);

        Assert.Equal("steinberg_modes_status", status.ProtocolTool.Name);
        Assert.Equal("steinberg_arrange_tempo", tempo.ProtocolTool.Name);
        Assert.Equal("steinberg_arrange_midi", midi.ProtocolTool.Name);

        var tempoProperties = tempo.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(tempoProperties.TryGetProperty("drums_wav", out _));
        Assert.True(tempoProperties.TryGetProperty("output_prefix", out _));
        Assert.True(tempoProperties.TryGetProperty("checkpoint_path", out _));

        var midiProperties = midi.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(midiProperties.TryGetProperty("prompt", out _));
        Assert.True(midiProperties.TryGetProperty("output_midi", out _));
    }
    [Fact]
    public void MODES03_MidiHeaderValidationRejectsTruncatedAndAcceptsMinimalSMF()
    {
        var method = typeof(SteinbergModesTools).GetMethod("ValidMidi",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var path = Path.Combine(Path.GetTempPath(), "steinberg_smf_" + Guid.NewGuid().ToString("N") + ".mid");
        try
        {
            File.WriteAllBytes(path, "MThd"u8.ToArray());
            Assert.False((bool)method.Invoke(null, [path])!);

            // Standard MIDI header, type 0, one track, PPQ 480; end-of-track event.
            File.WriteAllBytes(path, [
                0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xe0,
                0x4d, 0x54, 0x72, 0x6b, 0, 0, 0, 4, 0, 0xff, 0x2f, 0
            ]);
            Assert.True((bool)method.Invoke(null, [path])!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MODES04_TempoMetadataRejectsSimulatedBeatSource()
    {
        var method = typeof(SteinbergModesTools).GetMethod("ValidTempoMetadata",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var path = Path.Combine(Path.GetTempPath(), "steinberg_timing_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stub = "{\"schema\":\"groovemap-next-timing-v2\",\"beat_source\":\"EXPLICIT-STUB-not-real-beat-this\",\"beats_sec\":[0.5],\"tempo_points\":[{\"bpm\":120}]}";
            File.WriteAllText(path, stub, Encoding.UTF8);
            Assert.False((bool)method.Invoke(null, [path])!);
            File.WriteAllText(path, stub.Replace("EXPLICIT-STUB-not-real-beat-this", "CPJKU/beat_this:minimal"), Encoding.UTF8);
            Assert.True((bool)method.Invoke(null, [path])!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

}
