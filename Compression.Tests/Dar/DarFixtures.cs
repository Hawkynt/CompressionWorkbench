using System.Reflection;
using System.Text;

namespace Compression.Tests.Dar;

/// <summary>The dar-written archives under <c>ReferenceVectors</c> and the tree most of them hold.</summary>
internal static class DarFixtures {

  public static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DarVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded DAR vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>Writes the named vectors into <paramref name="directory"/> and returns their paths.</summary>
  public static string[] Materialise(string directory, params string[] names) {
    Directory.CreateDirectory(directory);
    return names.Select(n => {
      var path = Path.Combine(directory, n);
      File.WriteAllBytes(path, Vector(n));
      return path;
    }).ToArray();
  }

  /// <summary>The regular files of the 2.7.13 fixture tree (see ReferenceVectors/README.md), by path.</summary>
  public static IReadOnlyDictionary<string, byte[]> TreeFiles { get; } = BuildTree();

  /// <summary>The fixture tree's hello.txt modification time.</summary>
  public static readonly DateTime HelloModified = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234567);

  private static Dictionary<string, byte[]> BuildTree() {
    var hello = "hello dar\n"u8.ToArray();
    var escape = new List<byte>();
    escape.AddRange(Enumerable.Repeat((byte)'A', 10));
    escape.AddRange([0xAD, 0xFD, 0xEA, 0x77, 0x21, 0x46]);
    escape.AddRange(Enumerable.Repeat((byte)'B', 10));
    for (var i = 0; i < 3; ++i)
      escape.AddRange([0xAE, 0xFD, 0xEA, 0x77, 0x21, 0x58]);
    escape.AddRange(Enumerable.Repeat((byte)'C', 200));
    var sparse = new byte[4 + 70000 + 4];
    "head"u8.CopyTo(sparse);
    "tail"u8.CopyTo(sparse.AsSpan(sparse.Length - 4));
    var noise = new byte[600];
    var x = 2463534242u;
    for (var i = 0; i < noise.Length; ++i) {
      x ^= x << 13;
      x ^= x >> 17;
      x ^= x << 5;
      noise[i] = (byte)x;
    }
    return new(StringComparer.Ordinal) {
      ["hello.txt"] = hello,
      ["hard.txt"] = hello,
      ["empty.bin"] = [],
      ["text.txt"] = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 50))),
      ["escape.bin"] = [.. escape],
      ["sparse.bin"] = sparse,
      ["noise.bin"] = noise,
      [new string('n', 200)] = "long"u8.ToArray(),
      ["dir/deeper/file.txt"] = "deep\n"u8.ToArray(),
    };
  }

  public static string TempDirectory(string tag) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_dar_{tag}_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
  }
}
