namespace AutoCoder.Core.Retrieval;

public sealed class CodeChunk
{
    public required string Id { get; init; }
    public required string RelativePath { get; init; }
    public required string Symbol { get; init; }
    public required string Kind { get; init; }
    public required string Language { get; init; }
    public required string Imports { get; init; }
    public required string Body { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string TextForEmbed =>
        string.IsNullOrWhiteSpace(Imports)
            ? $"{RelativePath}\n{Symbol}\n{Body}"
            : $"{RelativePath}\n{Imports}\n{Symbol}\n{Body}";
}

/// <summary>
/// Chunk on function/class boundaries (item 11) — fixed-size chunking breaks code.
/// Imports stay with the first code unit in the file so call sites keep their context.
/// </summary>
public static class CodeChunker
{
    private static readonly HashSet<string> IndexableExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".py",
        ".java", ".kt",
        ".go", ".rs",
        ".html", ".htm", ".css", ".scss",
        ".json", ".yml", ".yaml", ".md", ".sql"
    };

    public static bool IsIndexable(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var ext = Path.GetExtension(relativePath);
        return IndexableExt.Contains(ext);
    }

    public static IReadOnlyList<CodeChunk> ChunkFile(string relativePath, string content, int maxChunkChars = 4_000)
    {
        relativePath = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var language = LanguageOf(relativePath);
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var imports = ExtractImports(lines, language);
        var boundaries = FindBoundaries(lines, language);

        if (boundaries.Count == 0)
            return SplitOversized(relativePath, language, imports, content, maxChunkChars);

        var chunks = new List<CodeChunk>();
        for (var i = 0; i < boundaries.Count; i++)
        {
            var (start, symbol, kind) = boundaries[i];
            var end = i + 1 < boundaries.Count ? boundaries[i + 1].Start - 1 : lines.Length - 1;
            if (end < start)
                end = start;
            var body = string.Join('\n', lines[start..(end + 1)]);
            var importBlock = i == 0 ? imports : "";
            foreach (var piece in SplitBody(body, maxChunkChars))
            {
                chunks.Add(MakeChunk(relativePath, language, importBlock, symbol, kind, piece, start + 1, end + 1));
            }
        }

        return chunks;
    }

    private static IReadOnlyList<CodeChunk> SplitOversized(
        string path, string language, string imports, string content, int maxChunkChars)
    {
        var pieces = SplitBody(content, maxChunkChars);
        var list = new List<CodeChunk>();
        for (var i = 0; i < pieces.Count; i++)
        {
            list.Add(MakeChunk(
                path, language, i == 0 ? imports : "",
                Path.GetFileName(path), "file", pieces[i], 1, content.Count(c => c == '\n') + 1));
        }
        return list;
    }

    private static CodeChunk MakeChunk(
        string path, string language, string imports, string symbol, string kind, string body, int start, int end)
    {
        var id = $"{path}#{symbol}#{start}-{end}".ToLowerInvariant();
        return new CodeChunk
        {
            Id = id,
            RelativePath = path,
            Symbol = symbol,
            Kind = kind,
            Language = language,
            Imports = imports,
            Body = body,
            StartLine = start,
            EndLine = end
        };
    }

    private static IReadOnlyList<string> SplitBody(string body, int maxChunkChars)
    {
        if (body.Length <= maxChunkChars)
            return [body];
        var parts = new List<string>();
        for (var i = 0; i < body.Length; i += maxChunkChars)
            parts.Add(body[i..Math.Min(i + maxChunkChars, body.Length)]);
        return parts;
    }

    private static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".fs" or ".vb" => "csharp",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".py" => "python",
        ".java" or ".kt" => "jvm",
        ".go" => "go",
        ".rs" => "rust",
        ".html" or ".htm" => "html",
        ".css" or ".scss" => "css",
        _ => "text"
    };

    private static string ExtractImports(string[] lines, string language)
    {
        var taken = new List<string>();
        foreach (var line in lines.Take(80))
        {
            var t = line.TrimStart();
            var isImport = language switch
            {
                "csharp" => t.StartsWith("using ", StringComparison.Ordinal) || t.StartsWith("namespace ", StringComparison.Ordinal),
                "javascript" or "typescript" => t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("export ", StringComparison.Ordinal) && t.Contains(" from ", StringComparison.Ordinal),
                "python" => t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("from ", StringComparison.Ordinal),
                "go" => t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("package ", StringComparison.Ordinal),
                "jvm" => t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("package ", StringComparison.Ordinal),
                "rust" => t.StartsWith("use ", StringComparison.Ordinal) || t.StartsWith("mod ", StringComparison.Ordinal),
                _ => false
            };
            if (isImport)
                taken.Add(line);
            else if (taken.Count > 0 && string.IsNullOrWhiteSpace(t))
                continue;
            else if (taken.Count > 0)
                break;
        }
        return string.Join('\n', taken);
    }

    private static List<(int Start, string Symbol, string Kind)> FindBoundaries(string[] lines, string language)
    {
        var list = new List<(int, string, string)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            if (string.IsNullOrWhiteSpace(t) || t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (TryMatch(language, t, out var symbol, out var kind))
                list.Add((i, symbol, kind));
        }
        return list;
    }

    private static bool TryMatch(string language, string t, out string symbol, out string kind)
    {
        symbol = "";
        kind = "";
        switch (language)
        {
            case "csharp":
                if (ContainsDecl(t, "class ", out symbol)) { kind = "class"; return true; }
                if (ContainsDecl(t, "interface ", out symbol)) { kind = "interface"; return true; }
                if (ContainsDecl(t, "record ", out symbol)) { kind = "record"; return true; }
                if (ContainsDecl(t, "enum ", out symbol)) { kind = "enum"; return true; }
                if (ContainsDecl(t, "struct ", out symbol)) { kind = "struct"; return true; }
                if (LooksLikeCsharpMethod(t, out symbol)) { kind = "method"; return true; }
                return false;
            case "javascript" or "typescript":
                if (t.StartsWith("export ", StringComparison.Ordinal))
                    t = t["export ".Length..].TrimStart();
                if (t.StartsWith("async ", StringComparison.Ordinal))
                    t = t["async ".Length..].TrimStart();
                if (t.StartsWith("function ", StringComparison.Ordinal))
                {
                    symbol = WordAfter(t, "function ");
                    kind = "function";
                    return !string.IsNullOrWhiteSpace(symbol);
                }
                if (t.StartsWith("class ", StringComparison.Ordinal))
                {
                    symbol = WordAfter(t, "class ");
                    kind = "class";
                    return !string.IsNullOrWhiteSpace(symbol);
                }
                if (t.Contains("=>", StringComparison.Ordinal) && t.Contains('=', StringComparison.Ordinal))
                {
                    var name = ConstLetName(t);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        symbol = name;
                        kind = "function";
                        return true;
                    }
                }
                return false;
            case "python":
                if (t.StartsWith("def ", StringComparison.Ordinal))
                {
                    symbol = WordAfter(t, "def ").TrimEnd(':');
                    kind = "function";
                    return !string.IsNullOrWhiteSpace(symbol);
                }
                if (t.StartsWith("class ", StringComparison.Ordinal))
                {
                    symbol = WordAfter(t, "class ").Split('(', ':')[0].Trim();
                    kind = "class";
                    return !string.IsNullOrWhiteSpace(symbol);
                }
                return false;
            case "html":
                if (t.StartsWith("<section", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("<main", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("<article", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("<header", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("<footer", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("<nav", StringComparison.OrdinalIgnoreCase))
                {
                    symbol = t.Split([' ', '>'], StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('<');
                    kind = "section";
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool ContainsDecl(string t, string keyword, out string symbol)
    {
        symbol = "";
        var idx = t.IndexOf(keyword, StringComparison.Ordinal);
        if (idx < 0)
            return false;
        // Skip "partial class" etc. — still fine
        symbol = WordAfter(t[idx..], keyword);
        return !string.IsNullOrWhiteSpace(symbol);
    }

    private static bool LooksLikeCsharpMethod(string t, out string symbol)
    {
        symbol = "";
        if (!t.Contains('(', StringComparison.Ordinal) || t.Contains('=', StringComparison.Ordinal))
            return false;
        if (t.StartsWith("if ", StringComparison.Ordinal) || t.StartsWith("for ", StringComparison.Ordinal)
            || t.StartsWith("foreach ", StringComparison.Ordinal) || t.StartsWith("while ", StringComparison.Ordinal)
            || t.StartsWith("switch ", StringComparison.Ordinal) || t.StartsWith("catch ", StringComparison.Ordinal)
            || t.StartsWith("using (", StringComparison.Ordinal) || t.StartsWith("lock (", StringComparison.Ordinal))
            return false;
        var open = t.IndexOf('(');
        if (open <= 0)
            return false;
        var before = t[..open].Trim();
        var parts = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;
        symbol = parts[^1].TrimStart('@');
        return symbol.Length > 0 && char.IsLetter(symbol[0]);
    }

    private static string WordAfter(string text, string keyword)
    {
        var rest = text[keyword.Length..].TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] is '_' or '<' or '>' or '.'))
            end++;
        return rest[..end].Trim();
    }

    private static string ConstLetName(string t)
    {
        foreach (var kw in new[] { "const ", "let ", "var " })
        {
            if (!t.StartsWith(kw, StringComparison.Ordinal))
                continue;
            var rest = t[kw.Length..].TrimStart();
            var eq = rest.IndexOf('=');
            if (eq <= 0)
                return "";
            return rest[..eq].Trim();
        }
        return "";
    }
}
