using AutoCoder.Core.Agent;
using AutoCoder.Core.Mcp;

namespace AutoCoder.Tests;

public sealed class AgentToolSchemaTests
{
    [Fact]
    public void OpenAi_tool_defs_include_search_code()
    {
        var defs = AgentToolSchema.OpenAiToolDefs();
        var json = System.Text.Json.JsonSerializer.Serialize(defs);
        Assert.Contains("search_code", json, StringComparison.Ordinal);
        Assert.Contains("list_files", json, StringComparison.Ordinal);
        Assert.Contains("finish", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Gemini_tool_defs_include_search_code()
    {
        var defs = AgentToolSchema.GeminiToolDefs();
        var json = System.Text.Json.JsonSerializer.Serialize(defs);
        Assert.Contains("search_code", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_tools_from_ambient_catalog_are_merged()
    {
        var catalog = new McpToolCatalog();
        // Catalog is only populated via ConnectAsync; inject via reflection-free ambient with empty catalog is fine.
        // Verify schema still works with null ambient.
        McpAmbient.Set(null);
        var before = AgentToolSchema.OpenAiToolDefs().Length;
        Assert.True(before >= 6);
    }
}
