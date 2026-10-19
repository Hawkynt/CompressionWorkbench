using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// The pools OpenZFS 2.2.2 created (<c>ReferenceVectors/README.md</c>) and the manifests
/// taken from the mounted pools, as embedded resources.
/// </summary>
internal static class ZfsReferencePools {
  internal sealed record ManifestEntry(char Kind, string Path, string? Sha256, long Size, string? Target);

  private static readonly Dictionary<string, byte[]> Images = new(StringComparer.Ordinal);

  /// <summary>The device image <c>&lt;name&gt;.img</c>, decompressed (tests may use the BCL as oracle).</summary>
  public static byte[] ImageBytes(string name) {
    lock (Images) {
      if (Images.TryGetValue(name, out var cached)) return cached;
      using var resource = typeof(ZfsReferencePools).Assembly.GetManifestResourceStream($"ZfsVectors.{name}.img.gz")
        ?? throw new FileNotFoundException($"ZFS reference pool '{name}' is not embedded.");
      using var gz = new GZipStream(resource, CompressionMode.Decompress);
      using var ms = new MemoryStream();
      gz.CopyTo(ms);
      return Images[name] = ms.ToArray();
    }
  }

  /// <summary>A fresh read-only stream over a device image.</summary>
  public static MemoryStream Open(string name) => new(ImageBytes(name), writable: false);

  /// <summary>A writable copy, for corruption tests.</summary>
  public static MemoryStream Copy(string name) {
    var bytes = ImageBytes(name);
    var ms = new MemoryStream();
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }

  public static List<ManifestEntry> Manifest(string pool) {
    using var resource = typeof(ZfsReferencePools).Assembly.GetManifestResourceStream($"ZfsVectors.{pool}.manifest")
      ?? throw new FileNotFoundException($"ZFS manifest '{pool}' is not embedded.");
    using var reader = new StreamReader(resource);
    var entries = new List<ManifestEntry>();
    while (reader.ReadLine() is { } line) {
      if (line.Length == 0) continue;
      if (line.StartsWith("D ", StringComparison.Ordinal)) {
        entries.Add(new('D', line[2..], null, 0, null));
      } else if (line.StartsWith("L ", StringComparison.Ordinal)) {
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        entries.Add(new('L', line[2..arrow], null, 0, line[(arrow + 4)..]));
      } else {
        var parts = line.Split(' ', 3);
        entries.Add(new('F', parts[2], parts[0], long.Parse(parts[1]), null));
      }
    }
    return entries;
  }

  public static string Sha256Of(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  /// <summary>
  /// Every manifest entry is listed with the right kind, size, target and SHA-256, and
  /// every file the reader lists is in the manifest.
  /// </summary>
  public static void AssertMatchesManifest(ZfsReader reader, string pool) {
    var manifest = Manifest(pool);
    var byPath = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    var problems = new List<string>();
    foreach (var m in manifest) {
      if (!byPath.TryGetValue(m.Path, out var e)) {
        problems.Add($"missing {m.Kind} {m.Path}");
        continue;
      }
      switch (m.Kind) {
        case 'D' when !e.IsDirectory:
          problems.Add($"{m.Path}: expected a directory");
          break;
        case 'L' when e.LinkTarget != m.Target:
          problems.Add($"{m.Path}: link target '{e.LinkTarget}', expected '{m.Target}'");
          break;
        case 'F': {
          if (e.IsDirectory || e.IsSymlink) {
            problems.Add($"{m.Path}: expected a regular file");
            break;
          }
          if (e.Size != m.Size) problems.Add($"{m.Path}: size {e.Size}, expected {m.Size}");
          string sha;
          try {
            sha = Sha256Of(reader.Extract(e));
          } catch (Exception x) {
            problems.Add($"{m.Path}: {x.GetType().Name}: {x.Message}");
            break;
          }
          if (sha != m.Sha256) problems.Add($"{m.Path}: SHA-256 {sha}, expected {m.Sha256}");
          break;
        }
      }
    }

    var known = manifest.Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
    foreach (var e in reader.Entries.Where(e => !e.IsDirectory && !known.Contains(e.Name)))
      problems.Add($"unexpected {e.Name}");

    Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(60)));
  }
}
