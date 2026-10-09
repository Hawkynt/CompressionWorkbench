#pragma warning disable CS1591

using System.Runtime.InteropServices;
using System.Text;

namespace Compression.Benchmarks;

/// <summary>
/// The inputs the DEFLATE benchmarks run on, one per kind of data a general-purpose codec
/// meets. Every kind is built from files every machine running the benchmark has — this
/// repository's own sources and the installed .NET runtime — so no corpus is checked in.
/// </summary>
/// <remarks>
/// The bytes therefore differ between checkouts and runtime versions: compare figures taken
/// in the same run (ours against the platform's zlib on the same input), not across machines.
/// </remarks>
public static class DeflateCorpus {

  /// <summary>Size of every corpus in bytes.</summary>
  public const int Size = 8 << 20;

  /// <summary>The corpus kinds.</summary>
  public static readonly string[] Kinds = ["Code", "Prose", "Binary", "Compressed", "Repetitive"];

  private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

  /// <summary>Gets the corpus named <paramref name="kind"/>, built on first use.</summary>
  /// <remarks>
  /// With the environment variable <c>DEFLATE_CORPUS_DIR</c> set, each corpus is read from that
  /// directory and only built (and saved there) when missing, so a before/after comparison
  /// across two checkouts runs on the same bytes even though the sources differ.
  /// </remarks>
  public static byte[] Get(string kind) {
    lock (Cache) {
      if (Cache.TryGetValue(kind, out var data))
        return data;

      var saved = Environment.GetEnvironmentVariable("DEFLATE_CORPUS_DIR") is { Length: > 0 } directory
        ? Path.Combine(directory, kind + ".bin")
        : null;
      if (saved != null && File.Exists(saved))
        return Cache[kind] = File.ReadAllBytes(saved);

      Cache[kind] = data = kind switch {
        "Code" => SourceCode(),
        "Prose" => Prose(),
        "Binary" => Binary(),
        "Compressed" => Compressed(),
        "Repetitive" => Repetitive(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
      };
      if (saved != null) {
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        File.WriteAllBytes(saved, data);
      }

      return data;
    }
  }

  /// <summary>C# sources of this repository, in ordinal path order.</summary>
  private static byte[] SourceCode() {
    var root = RepositoryRoot();
    string[] skipped = ["obj", "bin", ".claude"];
    var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
      .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(skipped.Contains));
    return Concatenate(files);
  }

  /// <summary>English API documentation: the XML doc files of the .NET reference pack, falling back to this repository's Markdown.</summary>
  private static byte[] Prose() {
    var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
    var version = Path.GetFileName(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar));
    var refDir = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref", version, "ref", $"net{Environment.Version.Major}.0");
    if (Directory.Exists(refDir))
      return Concatenate(Directory.EnumerateFiles(refDir, "*.xml"));

    return Concatenate(Directory.EnumerateFiles(RepositoryRoot(), "*.md", SearchOption.AllDirectories));
  }

  /// <summary>Executable code and metadata: the managed assemblies of the running runtime.</summary>
  private static byte[] Binary() => Concatenate(Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

  /// <summary>Data that is already compressed: the source corpus run through DEFLATE once.</summary>
  private static byte[] Compressed() {
    var once = Core.Deflate.DeflateCompressor.Compress(Get("Code"), Core.Deflate.DeflateCompressionLevel.Best);
    var data = new byte[Size];
    for (var offset = 0; offset < Size; offset += once.Length)
      once.AsSpan(0, Math.Min(once.Length, Size - offset)).CopyTo(data.AsSpan(offset));
    return data;
  }

  /// <summary>Long runs and short repeating records, as in sparse images and logs.</summary>
  private static byte[] Repetitive() {
    var data = new byte[Size];
    var record = Encoding.ASCII.GetBytes("2026-10-09T12:00:00Z INFO request served status=200 bytes=");
    var rng = new Random(1);
    var position = 0;
    while (position < Size) {
      if (rng.Next(4) == 0) {
        // A run of one value, as in zero-filled sectors.
        var run = Math.Min(rng.Next(1, 16384), Size - position);
        data.AsSpan(position, run).Fill((byte)(rng.Next(2) == 0 ? 0 : 0xFF));
        position += run;
        continue;
      }

      var line = Encoding.ASCII.GetBytes($"{Encoding.ASCII.GetString(record)}{rng.Next(1000, 1100)}\n");
      var n = Math.Min(line.Length, Size - position);
      line.AsSpan(0, n).CopyTo(data.AsSpan(position));
      position += n;
    }

    return data;
  }

  private static byte[] Concatenate(IEnumerable<string> files) {
    var data = new byte[Size];
    var position = 0;
    var sorted = files.Order(StringComparer.Ordinal).ToArray();
    if (sorted.Length == 0)
      throw new InvalidOperationException("No files found to build the corpus from.");

    // Wrap around when the files are smaller than the corpus; the repeat is far beyond the window.
    while (position < Size) {
      var before = position;
      foreach (var file in sorted) {
        var bytes = File.ReadAllBytes(file);
        var n = Math.Min(bytes.Length, Size - position);
        bytes.AsSpan(0, n).CopyTo(data.AsSpan(position));
        position += n;
        if (position == Size)
          break;
      }

      if (position == before)
        throw new InvalidOperationException("The files to build the corpus from are all empty.");
    }

    return data;
  }

  private static string RepositoryRoot() {
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
      for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "CompressionWorkbench.slnx")))
          return directory.FullName;

    throw new InvalidOperationException("Run the benchmarks from inside the repository checkout.");
  }
}
