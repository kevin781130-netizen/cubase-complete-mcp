using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Optional file-only adapters for the owner's existing music engines.
/// They never modify an open Cubase/Nuendo project, start a shell, or change the original projects.
/// An explicit locally installed executable is required for each provider.
/// </summary>
[McpServerToolType]
public sealed class SteinbergModesTools
{
    private const string GrooveEnv = "STEINBERG_GROOVEMAP_EXE";
    private const string MidiEnv = "STEINBERG_MIDIVOLT_EXE";

    [McpServerTool(Name = "steinberg_modes_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Show the two production modes and whether the optional, local GrooveMap Next and MIDIVOLT CLI executables are configured. No external process is started.")]
    public JsonNode Status() => new JsonObject
    {
        ["status"] = "Ok",
        ["arrangement_mode"] = new JsonObject
        {
            ["groovemap_next"] = ProviderState(GrooveEnv, "groovemap-next.exe"),
            ["midivolt"] = ProviderState(MidiEnv, "midivolt-prompt.exe"),
            ["output"] = "File-only tempo/drum MIDI and arrangement MIDI; import to DAW remains manual"
        },
        ["foley_mode"] = new JsonObject
        {
            ["status"] = "NotImplemented",
            ["next_boundary"] = "video spotting and timestamped sound-effect event manifest"
        },
        ["host_support"] = "Upstream broker was live-validated on Cubase 15; Cubase 12-14 and Nuendo 12-15 are not validated"
    };

    [McpServerTool(Name = "steinberg_arrange_tempo", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Run an explicitly configured local GrooveMap Next CLI on an existing WAV drum stem. Writes new tempo/drum MIDI and timing files under a unique absolute output prefix; refuses existing outputs. Does not insert anything into Cubase/Nuendo. Genuine Beat This! inference may need a licensed local checkpoint.")]
    public async Task<JsonNode> ArrangeTempo(string drums_wav, string output_prefix, CancellationToken cancellationToken = default)
    {
        try
        {
            var executable = ConfiguredExecutable(GrooveEnv, "groovemap-next.exe");
            if (executable is null) return Error("PROVIDER_NOT_CONFIGURED", "Set STEINBERG_GROOVEMAP_EXE to the full path of the installed groovemap-next.exe.");
            var input = ExistingAbsoluteFile(drums_wav, ".wav");
            var prefix = AbsoluteOutputPrefix(output_prefix);
            var outputs = new[] { "_tempo.mid", "_drums.mid", "_timing.json", "_drums_humanized.mid", "_groove.json" }
                .Select(suffix => prefix + suffix).ToArray();
            if (outputs.Any(Path.Exists)) return Error("OUTPUT_EXISTS", "An output under the requested prefix already exists. Choose a new prefix; existing work is never overwritten.");
            return await RunProviderAsync(executable,
                ["suno", "--drums", input, "--out", prefix, "--device", "cpu"],
                [prefix + "_tempo.mid", prefix + "_drums.mid"], cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException e) { return Error("INVALID_INPUT", e.Message); }
    }

    [McpServerTool(Name = "steinberg_arrange_midi", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Run an explicitly configured local MIDIVOLT prompt arranger to generate a new editable multitrack MIDI file. Requires an absolute .mid output path that does not exist. No modification of Cubase/Nuendo or an existing MIDI project.")]
    public async Task<JsonNode> ArrangeMidi(string prompt, string output_midi, CancellationToken cancellationToken = default)
    {
        try
        {
            var executable = ConfiguredExecutable(MidiEnv, "midivolt-prompt.exe");
            if (executable is null) return Error("PROVIDER_NOT_CONFIGURED", "Set STEINBERG_MIDIVOLT_EXE to the full path of the installed midivolt-prompt.exe.");
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1200)
                return Error("INVALID_PROMPT", "Provide a nonempty musical prompt of at most 1200 characters.");
            var output = AbsoluteOutputFile(output_midi, ".mid");
            if (Path.Exists(output)) return Error("OUTPUT_EXISTS", "The output file already exists. Choose a new file; existing work is never overwritten.");
            return await RunProviderAsync(executable,
                ["--prompt", prompt, "--out", output], [output], cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException e) { return Error("INVALID_INPUT", e.Message); }
    }

    private static JsonObject ProviderState(string variable, string leafName)
    {
        var configured = ConfiguredExecutable(variable, leafName);
        return new JsonObject
        {
            ["environment_variable"] = variable,
            ["configured"] = configured is not null,
            ["executable_found"] = configured is not null && File.Exists(configured)
        };
    }

    private static string? ConfiguredExecutable(string variable, string leafName)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        try
        {
            var full = Path.GetFullPath(value);
            return Path.GetFileName(full).Equals(leafName, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string ExistingAbsoluteFile(string path, string extension)
    {
        var full = AbsolutePath(path);
        if (!Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("An existing absolute " + extension + " source file is required.");
        return full;
    }

    private static string AbsoluteOutputPrefix(string path)
    {
        var full = AbsolutePath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(full)) || !Directory.Exists(Path.GetDirectoryName(full)))
            throw new ArgumentException("The output prefix must have a filename in an existing directory.");
        return full;
    }

    private static string AbsoluteOutputFile(string path, string extension)
    {
        var full = AbsolutePath(path);
        if (!Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(full)))
            throw new ArgumentException("The output must be a " + extension + " file in an existing directory.");
        return full;
    }

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute local path is required.");
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Invalid local path.", e); }
    }

    private static async Task<JsonNode> RunProviderAsync(
        string executable, string[] args, string[] requiredOutputs, CancellationToken cancellationToken)
    {
        // Guard against simultaneous callers using the same destination. A crash leaves
        // the lock for manual inspection, rather than risking an automatic duplicate run.
        var lockPath = requiredOutputs[0] + ".mcp-inflight";
        FileStream? reservation = null;
        try
        {
            reservation = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (requiredOutputs.Any(Path.Exists))
                return Error("OUTPUT_EXISTS", "One of the requested outputs already exists.");
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start()) return Error("START_FAILED", "The local provider did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(8));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var log = await stdout.ConfigureAwait(false);
                var errors = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    return Error("PROVIDER_FAILED", "External CLI exited with code " + process.ExitCode +
                        ": " + Clip(errors, 1000) + " " + Clip(log, 300));
                if (requiredOutputs.Any(path => !ValidMidi(path)))
                    return Error("OUTPUT_NOT_VERIFIED", "The CLI finished but one or more expected MIDI files were missing or not valid MIDI headers. Check the provider output before any retry.");

                var files = new JsonArray();
                foreach (var path in requiredOutputs) files.Add(path);
                return new JsonObject
                {
                    ["status"] = "OkFilesOnly",
                    ["execution_state"] = "applied",
                    ["files"] = files,
                    ["validation"] = "MIDI headers checked; timing, musical accuracy and DAW import NOT verified",
                    ["stdout_tail"] = Clip(log, 600)
                };
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return Error("OUTCOME_UNKNOWN", "Process canceled or exceeded eight minutes. Partial outputs may exist. Inspect them before deciding whether to retry.");
            }
        }
        catch (IOException e) { return Error("OUTPUT_BUSY", "Output lock could not be created: " + e.Message); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return Error("PROVIDER_START_ERROR", e.Message); }
        finally
        {
            if (reservation is not null)
            {
                reservation.Dispose();
                try { File.Delete(lockPath); } catch (IOException) { /* stale lock requires operator inspection */ }
            }
        }
    }

    private static bool ValidMidi(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return stream.Read(header) == 4 && header.SequenceEqual("MThd"u8);
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[^max..];

    private static JsonObject Error(string code, string message) => new()
    {
        ["status"] = "Error",
        ["execution_state"] = code == "OUTCOME_UNKNOWN" ? "unknown" : "not_verified",
        ["code"] = code,
        ["message"] = message
    };
}
