using AutoCoder.Core.Mcp;

namespace AutoCoder.Core.Agent;

/// <summary>Shared tool schemas for OpenAI-compatible and Gemini coding loops (Pro adds search_code + MCP).</summary>
internal static class AgentToolSchema
{
    public static object[] OpenAiToolDefs()
    {
        var list = new List<object>
        {
            OpenAiTool("list_files", "List files and folders relative to the repo root.",
                ("path", "Relative directory. Use empty or '.' for root.", false)),
            OpenAiTool("search_code",
                "Semantic search over the indexed repo (function/class chunks). Prefer this in large repos before blind grep.",
                ("query", "What you are looking for — symbols, behavior, or ticket phrasing", true)),
            OpenAiTool("read_file", "Read a text file relative to the repo root.",
                ("path", "Relative file path", true)),
            OpenAiTool("write_file", "Create or overwrite a text file. Use this to implement the fix or feature.",
                ("path", "Relative file path", true),
                ("content", "Full file contents", true)),
            OpenAiTool("grep", "Search file contents for a string (case-insensitive).",
                ("pattern", "Text to find", true),
                ("path", "Relative file or directory to search", false)),
            OpenAiTool("finish", "Call when the code change is complete. Do not call until files are written.",
                ("summary", "What you changed and why", true))
        };

        foreach (var mcp in McpAmbient.Catalog?.Tools ?? [])
        {
            list.Add(OpenAiTool(mcp.QualifiedName, mcp.Description,
                ("arguments_json", "JSON object of tool arguments as a string", false)));
        }

        return list.ToArray();
    }

    public static object[] GeminiToolDefs()
    {
        var list = new List<object>
        {
            GeminiFn("list_files", "List files and folders relative to the repo root.",
                GeminiProp("path", "Relative directory. Use empty or '.' for root.")),
            GeminiFn("search_code",
                "Semantic search over the indexed repo (function/class chunks). Prefer this in large repos before blind grep.",
                GeminiProp("query", "What you are looking for — symbols, behavior, or ticket phrasing", required: true)),
            GeminiFn("read_file", "Read a text file relative to the repo root.",
                GeminiProp("path", "Relative file path", required: true)),
            GeminiFn("write_file", "Create or overwrite a text file. Use this to implement the fix or feature.",
                GeminiProp("path", "Relative file path", required: true),
                GeminiProp("content", "Full file contents", required: true)),
            GeminiFn("grep", "Search file contents for a string (case-insensitive).",
                GeminiProp("pattern", "Text to find", required: true),
                GeminiProp("path", "Relative file or directory to search")),
            GeminiFn("finish", "Call when the code change is complete. Do not call until files are written.",
                GeminiProp("summary", "What you changed and why", required: true))
        };

        foreach (var mcp in McpAmbient.Catalog?.Tools ?? [])
        {
            list.Add(GeminiFn(mcp.QualifiedName, mcp.Description,
                GeminiProp("arguments_json", "JSON object of tool arguments as a string")));
        }

        return list.ToArray();
    }

    private static object OpenAiTool(string name, string description, params (string Name, string Description, bool Required)[] props)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in props)
        {
            properties[p.Name] = new { type = "string", description = p.Description };
            if (p.Required)
                required.Add(p.Name);
        }

        return new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters = new { type = "object", properties, required }
            }
        };
    }

    private static object GeminiFn(string name, string description, params object[] properties)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in properties)
        {
            var d = (Dictionary<string, object>)p;
            var n = (string)d["name"];
            props[n] = new { type = "string", description = d["description"] };
            if (d.TryGetValue("required", out var r) && r is true)
                required.Add(n);
        }

        return new
        {
            name,
            description,
            parameters = new
            {
                type = "object",
                properties = props,
                required
            }
        };
    }

    private static Dictionary<string, object> GeminiProp(string name, string description, bool required = false) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["required"] = required
        };
}
