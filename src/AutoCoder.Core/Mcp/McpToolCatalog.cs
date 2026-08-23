using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Mcp;

public sealed class McpToolDescriptor
{
    public required string Server { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string Description { get; init; }
    public bool ReadOnly { get; init; }
}

/// <summary>
/// Minimal MCP client over stdio JSON-RPC 2.0 (tools/list + tools/call).
/// Scaffold for Pro: servers are allowlisted in config; tools merge into the coding agent as mcp_* .
/// </summary>
public sealed class McpToolCatalog : IAsyncDisposable
{
    private readonly List<McpSession> _sessions = [];
    private readonly List<McpToolDescriptor> _tools = [];

    public IReadOnlyList<McpToolDescriptor> Tools => _tools;

    public static async Task<McpToolCatalog> ConnectAsync(McpOptions options, CancellationToken cancellationToken = default)
    {
        var catalog = new McpToolCatalog();
        if (!options.Enabled || options.Servers.Count == 0)
            return catalog;

        foreach (var server in options.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Command))
                continue;
            try
            {
                var session = await McpSession.StartAsync(server, cancellationToken);
                catalog._sessions.Add(session);
                foreach (var tool in await session.ListToolsAsync(cancellationToken))
                    catalog._tools.Add(tool);
                Console.WriteLine($"[mcp] Connected '{server.Name}' — {catalog._tools.Count(t => t.Server == server.Name)} tool(s).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[mcp] Failed to start '{server.Name}': {ex.Message}");
            }
        }

        return catalog;
    }

    public async Task<string> CallAsync(string qualifiedName, string argsJson, CancellationToken cancellationToken = default)
    {
        var tool = _tools.FirstOrDefault(t => t.QualifiedName.Equals(qualifiedName, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            return $"Unknown MCP tool {qualifiedName}";

        var session = _sessions.FirstOrDefault(s => s.ServerName.Equals(tool.Server, StringComparison.OrdinalIgnoreCase));
        if (session is null)
            return $"MCP server '{tool.Server}' is not connected.";

        return await session.CallAsync(tool.Name, argsJson, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions)
            await s.DisposeAsync();
        _sessions.Clear();
        _tools.Clear();
    }
}

internal sealed class McpSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId = 1;

    public string ServerName { get; }
    public bool ReadOnly { get; }

    private McpSession(Process process, StreamWriter stdin, StreamReader stdout, McpServerOptions options)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        ServerName = options.Name;
        ReadOnly = options.ReadOnly;
    }

    public static async Task<McpSession> StartAsync(McpServerOptions options, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in options.Args)
            psi.ArgumentList.Add(a);

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Could not start MCP server '{options.Name}'.");
        var stdin = process.StandardInput;
        var stdout = process.StandardOutput;
        var session = new McpSession(process, stdin, stdout, options);

        await session.RequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "autocoderpro", version = "0.1.0" }
        }, cancellationToken);
        await session.NotifyAsync("notifications/initialized", new { }, cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        using var doc = await RequestAsync("tools/list", new { }, cancellationToken);
        var list = new List<McpToolDescriptor>();
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("tools", out var tools))
            return list;

        foreach (var t in tools.EnumerateArray())
        {
            var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? name : name;
            var qualified = $"mcp_{Sanitize(ServerName)}_{Sanitize(name)}";
            list.Add(new McpToolDescriptor
            {
                Server = ServerName,
                Name = name,
                QualifiedName = qualified,
                Description = $"[MCP:{ServerName}] {desc}",
                ReadOnly = ReadOnly
            });
        }
        return list;
    }

    public async Task<string> CallAsync(string toolName, string argsJson, CancellationToken cancellationToken)
    {
        object args;
        try
        {
            args = string.IsNullOrWhiteSpace(argsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? new();
        }
        catch
        {
            args = new Dictionary<string, object?>();
        }

        using var doc = await RequestAsync("tools/call", new { name = toolName, arguments = args }, cancellationToken);
        if (doc.RootElement.TryGetProperty("result", out var result))
            return result.ToString();
        if (doc.RootElement.TryGetProperty("error", out var error))
            return $"MCP error: {error}";
        return doc.RootElement.ToString();
    }

    private async Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
            await WriteMessageAsync(payload, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var id = _nextId++;
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
            await WriteMessageAsync(payload, cancellationToken);
            var raw = await ReadMessageAsync(cancellationToken);
            return JsonDocument.Parse(raw);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteMessageAsync(string json, CancellationToken cancellationToken)
    {
        // MCP stdio framing: Content-Length header + body (same as LSP).
        var body = Encoding.UTF8.GetBytes(json);
        await _stdin.WriteAsync($"Content-Length: {body.Length}\r\n\r\n".AsMemory(), cancellationToken);
        await _stdin.BaseStream.WriteAsync(body, cancellationToken);
        await _stdin.FlushAsync(cancellationToken);
    }

    private async Task<string> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var contentLength = -1;
        while (true)
        {
            var line = await _stdout.ReadLineAsync(cancellationToken)
                       ?? throw new InvalidOperationException($"MCP server '{ServerName}' closed stdout.");
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
            if (string.IsNullOrEmpty(line))
                break;
        }

        if (contentLength < 0)
            throw new InvalidOperationException($"MCP server '{ServerName}' sent a message without Content-Length.");

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await _stdout.BaseStream.ReadAsync(buffer.AsMemory(read, contentLength - read), cancellationToken);
            if (n == 0)
                throw new InvalidOperationException($"MCP server '{ServerName}' ended mid-message.");
            read += n;
        }
        return Encoding.UTF8.GetString(buffer);
    }

    private static string Sanitize(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // best effort
        }
        _stdin.Dispose();
        _stdout.Dispose();
        _process.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Ambient MCP catalog for the coding agent loop.</summary>
public static class McpAmbient
{
    private static readonly AsyncLocal<McpToolCatalog?> Current = new();
    public static void Set(McpToolCatalog? catalog) => Current.Value = catalog;
    public static McpToolCatalog? Catalog => Current.Value;
}
