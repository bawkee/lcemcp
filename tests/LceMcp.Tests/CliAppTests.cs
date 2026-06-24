using System.Text.Json.Nodes;

namespace LceMcp.Tests;

public sealed class CliAppTests
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    [Fact]
    public async Task McpConfigPrintsCodexToml()
    {
        var command = FakeCommandPath("LceMcp.exe");

        var output = await RunCliAsync(
            "mcp-config",
            "--client",
            "codex",
            "--name",
            "mail-cache",
            "--command",
            command);

        Assert.Contains("Codex MCP configuration", output);
        Assert.Contains("Add or update this block in %USERPROFILE%\\.codex\\config.toml:", output);
        Assert.Contains("[mcp_servers.mail-cache]", output);
        Assert.Contains($"command = \"{command.Replace("\\", "\\\\")}\"", output);
        Assert.Contains("args = [\"serve\"]", output);
        Assert.Contains("startup_timeout_sec = 30", output);
    }

    [Theory]
    [InlineData("claude-code", "mcpServers", "stdio")]
    [InlineData("opencode", "mcp", "local")]
    [InlineData("github-copilot", "mcpServers", "local")]
    [InlineData("vscode", "servers", "stdio")]
    public async Task McpConfigPrintsJsonForPopularClients(string client, string rootKey, string type)
    {
        var command = FakeCommandPath("LceMcp.exe");

        var output = await RunCliAsync(
            "mcp-config",
            "--client",
            client,
            "--name",
            "mail-cache",
            "--command",
            command);
        var json = ExtractJson(output);
        var server = json[rootKey]["mail-cache"].AsObject();

        Assert.Equal(type, server["type"].GetValue<string>());

        if (client == "opencode")
        {
            var commandLine = server["command"].AsArray().Select(value => value.GetValue<string>()).ToArray();
            Assert.Equal([command, "serve"], commandLine);
            Assert.True(server["enabled"].GetValue<bool>());
            return;
        }

        Assert.Equal(command, server["command"].GetValue<string>());
        Assert.Equal(["serve"], server["args"].AsArray().Select(value => value.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task McpConfigUsesDotnetHostForDllCommand()
    {
        var dll = FakeCommandPath("LceMcp.dll");

        var output = await RunCliAsync(
            "mcp-config",
            "--client",
            "claude",
            "--name",
            "mail-cache",
            "--command",
            dll);
        var json = ExtractJson(output);
        var server = json["mcpServers"]["mail-cache"].AsObject();

        Assert.Equal("dotnet", server["command"].GetValue<string>());
        Assert.Equal([dll, "serve"], server["args"].AsArray().Select(value => value.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task McpConfigAcceptsCopilotVsCodeAlias()
    {
        var command = FakeCommandPath("LceMcp.exe");

        var output = await RunCliAsync(
            "mcp-config",
            "--client",
            "copilot-vscode",
            "--command",
            command);
        var json = ExtractJson(output);

        Assert.NotNull(json["servers"]["lcemcp"]);
    }

    private static async Task<string> RunCliAsync(params string[] args)
    {
        await ConsoleGate.WaitAsync();

        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousIn = Console.In;
        var previousConfigDir = Environment.GetEnvironmentVariable("LCEMCP_CONFIG_DIR");

        try
        {
            using var temp = TempWorkspace.Create();
            using var output = new StringWriter();
            using var error = new StringWriter();

            Environment.SetEnvironmentVariable("LCEMCP_CONFIG_DIR", temp.Directory);
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(new StringReader(""));

            var exitCode = await CliApp.RunAsync(args, CancellationToken.None);

            Assert.Equal(0, exitCode);
            return output.ToString();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Console.SetIn(previousIn);
            Environment.SetEnvironmentVariable("LCEMCP_CONFIG_DIR", previousConfigDir);
            ConsoleGate.Release();
        }
    }

    private static JsonObject ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');

        Assert.True(start >= 0, "Output did not contain a JSON object.");
        Assert.True(end > start, "Output did not contain a complete JSON object.");

        return JsonNode.Parse(output[start..(end + 1)]).AsObject();
    }

    private static string FakeCommandPath(string fileName) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lcemcp mcp config", fileName));
}
