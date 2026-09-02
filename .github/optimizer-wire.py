from pathlib import Path

p = Path('Compression.Lib/ArchiveOperations.cs')
t = p.read_text()
old = '''    // ── Unsupported: fall back to copy ───────────────────────────────\n    // Use temp+rename so a crash mid-copy doesn't leave a truncated target.\n    AtomicFileWriter.WriteAtomic(outputPath, outFs => {\n      using var inFs = File.OpenRead(inputPath);\n      inFs.CopyTo(outFs);\n    });\n    return (originalSize, originalSize, 0);'''
new = '''    // ── Generic multi-entry optimizer ─────────────────────────────────\n    // Any creatable/listable container that publishes a finite tunable schema\n    // can participate. Every candidate is rebuilt and exact-name/data verified\n    // by ArchiveCompressionOptimizer/RebuildVerb before it can win.\n    FormatRegistration.EnsureInitialized();\n    var archiveOps = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());\n    if (archiveOps is Compression.Registry.IArchiveCreatable creator && archiveOps is Compression.Registry.IFormatOptionsSchema schema\n        && schema.OptionsSchema.Any(option =>\n          option.Kind == Compression.Registry.FormatOptionKind.Boolean || option.AllowedValues is { Count: > 1 })) {\n      var optimized = ArchiveCompressionOptimizer.Optimize(\n        inputPath, outputPath, archiveOps, creator, schema);\n      return (optimized.OriginalSize, optimized.OptimizedSize, optimized.EntriesOptimized);\n    }\n\n    // ── No honest optimization surface: preserve the input byte-for-byte ───\n    // Use temp+rename so a crash mid-copy doesn't leave a truncated target.\n    AtomicFileWriter.WriteAtomic(outputPath, outFs => {\n      using var inFs = File.OpenRead(inputPath);\n      inFs.CopyTo(outFs);\n    });\n    return (originalSize, originalSize, 0);'''
if old not in t:
    raise SystemExit('ArchiveOperations optimize fallback anchor not found')
p.write_text(t.replace(old, new, 1))

# The helper exists only to transform this branch; never retain it in the PR.
Path('.github/optimizer-wire.py').unlink()
