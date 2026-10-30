using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>One file an archive must yield: its name, size and SHA-256.</summary>
public sealed record RarExpectedEntry(string Name, long Size, string Sha256);

/// <summary>An archive of a conformance set with what it must extract to.</summary>
public sealed record RarExpectedArchive(string Set, string Archive, string? Password, IReadOnlyList<RarExpectedEntry> Entries) {
  public override string ToString() => $"{this.Set}/{this.Archive}";
}

/// <summary>
/// The RAR conformance sets of <c>Rar/Conformance</c> (embedded): written to a temp directory
/// once per run, so volume sets find their other volumes the way they do on disk.
/// </summary>
internal static class RarConformanceSet {
  public const string Modern = "RarConfModern";
  public const string Dos = "RarConfDos";
  public const string Libarchive = "RarConfLibarchive";

  private static readonly ConcurrentDictionary<string, string> Dirs = new();

  /// <summary>The directory holding every file of <paramref name="set"/>.</summary>
  public static string Directory(string set) => Dirs.GetOrAdd(set, static s => {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_rarconf_{s}_{Environment.ProcessId}");
    System.IO.Directory.CreateDirectory(dir);
    var asm = Assembly.GetExecutingAssembly();
    foreach (var name in asm.GetManifestResourceNames()) {
      if (!name.StartsWith(s + ".", StringComparison.Ordinal)) continue;
      using var src = asm.GetManifestResourceStream(name)!;
      using var dst = File.Create(Path.Combine(dir, name[(s.Length + 1)..]));
      src.CopyTo(dst);
    }
    return dir;
  });

  public static string PathOf(string set, string file) => Path.Combine(Directory(set), file);

  /// <summary>The archives a manifest lists, with their entries.</summary>
  public static IEnumerable<RarExpectedArchive> Manifest(string set, string manifest = "manifest.tsv") {
    var byArchive = new Dictionary<string, (string? Password, List<RarExpectedEntry> Entries)>(StringComparer.Ordinal);
    var order = new List<string>();
    foreach (var line in File.ReadAllLines(PathOf(set, manifest))) {
      if (line.Length == 0) continue;
      var f = line.Split('\t');
      if (!byArchive.TryGetValue(f[0], out var a)) {
        a = (f.Length > 5 && f[5].Length > 0 ? f[5] : null, []);
        byArchive[f[0]] = a;
        order.Add(f[0]);
      }
      if (f[2] == "f") a.Entries.Add(new RarExpectedEntry(f[1], long.Parse(f[3]), f[4]));
    }
    foreach (var name in order)
      yield return new RarExpectedArchive(set, name, byArchive[name].Password, byArchive[name].Entries);
  }

  /// <summary>Extracts every file entry into memory, keyed by name.</summary>
  public static Dictionary<string, byte[]> ExtractAll(RarReader reader) {
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var e = reader.Entries[i];
      if (e.IsDirectory || e.IsSymlink) continue;
      result[e.Name] = reader.Extract(i);
    }
    return result;
  }

  public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
