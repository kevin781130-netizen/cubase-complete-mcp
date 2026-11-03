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

        var midiProperties = midi.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(midiProperties.TryGetProperty("prompt", out _));
        Assert.True(midiProperties.TryGetProperty("output_midi", out _));
    }
}
