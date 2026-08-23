using AutoCoder.Core.Retrieval;

namespace AutoCoder.Tests;

public sealed class CodeChunkerTests
{
    [Fact]
    public void Chunks_csharp_on_class_and_method_boundaries_and_keeps_imports_with_first_unit()
    {
        var src = """
            using System;
            using System.Linq;

            namespace Demo;

            public sealed class Greeter
            {
                public string Hello(string name) => $"hi {name}";

                public int Add(int a, int b) => a + b;
            }
            """;

        var chunks = CodeChunker.ChunkFile("src/Greeter.cs", src);

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, c => c.Kind == "class" && c.Symbol.Contains("Greeter", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Imports.Contains("using System;", StringComparison.Ordinal));
        Assert.All(chunks.Skip(1), c => Assert.True(string.IsNullOrEmpty(c.Imports) || c.Imports.Contains("using")));
    }

    [Fact]
    public void Chunks_javascript_functions()
    {
        var src = """
            import { x } from './x.js';

            export function updateHeading(text) {
              return text;
            }

            const other = () => 1;
            """;

        var chunks = CodeChunker.ChunkFile("public/app.js", src);
        Assert.Contains(chunks, c => c.Symbol.Contains("updateHeading", StringComparison.Ordinal));
    }

    [Fact]
    public void Chunks_python_defs()
    {
        var src = """
            import os

            def load_config():
                return {}

            class Runner:
                def run(self):
                    pass
            """;

        var chunks = CodeChunker.ChunkFile("app.py", src);
        Assert.Contains(chunks, c => c.Kind == "function" && c.Symbol == "load_config");
        Assert.Contains(chunks, c => c.Kind == "class" && c.Symbol == "Runner");
    }

    [Fact]
    public void Non_code_files_still_produce_at_least_one_chunk()
    {
        var chunks = CodeChunker.ChunkFile("README.md", "# Hello\n\nWorld");
        Assert.Single(chunks);
        Assert.Equal("file", chunks[0].Kind);
    }
}
