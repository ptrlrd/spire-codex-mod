using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireCodex.Autopilot;

// A child `sim-cli serve` process spoken to over stdin and stdout, one JSON line per request
// and one per response. Where the binary and the content live comes from
// %APPDATA%/SpireCodex/autopilot.json: {"sim_cli": "...", "data_root": "...", "build": "v0.107.1"}.
internal sealed class EngineBridge : IDisposable
{
    public sealed record Settings(string SimCli, string DataRoot, string Build, double SearchSeconds, int Depth);

    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly object _gate = new();
    private long _next = 1;

    public Settings Config { get; }

    private EngineBridge(Process process, Settings settings)
    {
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        Config = settings;
    }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireCodex", "autopilot.json");

    public static Settings? LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            if (node is null) return null;
            var simCli = node["sim_cli"]?.GetValue<string>();
            var dataRoot = node["data_root"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(simCli) || string.IsNullOrWhiteSpace(dataRoot)) return null;
            return new Settings(
                simCli,
                dataRoot,
                node["build"]?.GetValue<string>() ?? "v0.107.1",
                node["search_seconds"]?.GetValue<double>() ?? 0.5,
                node["depth"]?.GetValue<int>() ?? 3);
        }
        catch (Exception error)
        {
            MainFile.Logger.Warn($"autopilot settings unreadable: {error.Message}");
            return null;
        }
    }

    public static EngineBridge? Open()
    {
        var settings = LoadSettings();
        if (settings is null)
        {
            MainFile.Logger.Warn($"autopilot needs {SettingsPath} with sim_cli and data_root");
            return null;
        }
        try
        {
            var info = new ProcessStartInfo(settings.SimCli, "serve")
            {
                WorkingDirectory = settings.DataRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = Process.Start(info) ?? throw new InvalidOperationException("serve did not start");
            process.StandardInput.AutoFlush = true;
            return new EngineBridge(process, settings);
        }
        catch (Exception error)
        {
            MainFile.Logger.Warn($"autopilot engine unavailable: {error.Message}");
            return null;
        }
    }

    public JsonObject Request(JsonObject request)
    {
        lock (_gate)
        {
            var id = _next++;
            request["id"] = id;
            _input.WriteLine(request.ToJsonString());
            while (true)
            {
                var line = _output.ReadLine() ?? throw new InvalidOperationException("sim-cli serve closed");
                if (line.Length == 0) continue;
                var response = JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException("bad response");
                if (response["id"]?.GetValue<long>() == id) return response;
            }
        }
    }

    public static string? Error(JsonObject response) =>
        response["error"] is null ? null : $"{response["error"]}: {response["message"]}";

    public long RunLoad(string save)
    {
        var response = Request(new JsonObject
        {
            ["op"] = "run_load",
            ["save"] = save,
            ["build"] = Config.Build,
            ["data_root"] = Config.DataRoot,
        });
        return Error(response) is { } error ? throw new InvalidOperationException(error) : response["run"]!.GetValue<long>();
    }

    public JsonArray RunLegal(long run)
    {
        var response = Request(new JsonObject { ["op"] = "run_legal", ["run"] = run });
        return Error(response) is { } error ? throw new InvalidOperationException(error) : (JsonArray)response["actions"]!;
    }

    public JsonObject RunSearch(long run)
    {
        return Request(new JsonObject
        {
            ["op"] = "run_search",
            ["run"] = run,
            ["mode"] = "ismcts",
            ["clairvoyant"] = false,
            ["seconds"] = Config.SearchSeconds,
            ["worlds"] = 16,
            ["max_turns"] = 4,
        });
    }

    public JsonObject RunApply(long run, JsonNode action)
    {
        return Request(new JsonObject { ["op"] = "run_apply", ["run"] = run, ["action"] = action.DeepClone() });
    }

    public JsonObject RunState(long run) => Request(new JsonObject { ["op"] = "run_state", ["run"] = run });

    public void RunRelease(long run) => Request(new JsonObject { ["op"] = "run_release", ["run"] = run });

    public JsonObject Advise(string save)
    {
        return Request(new JsonObject
        {
            ["op"] = "advise",
            ["save"] = save,
            ["build"] = Config.Build,
            ["data_root"] = Config.DataRoot,
            ["mode"] = "ismcts",
            ["depth"] = Config.Depth,
            ["seconds"] = Config.SearchSeconds,
        });
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _input.Close();
                if (!_process.WaitForExit(2000)) _process.Kill();
            }
        }
        catch (Exception error)
        {
            MainFile.Logger.Warn($"autopilot engine shutdown: {error.Message}");
        }
        _process.Dispose();
    }
}
