#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.LittleFs;

namespace Compression.Tests.LittleFs;

/// <summary>
/// Regression for the registry-wide self-round-trip: a multi-block (~11 KB) text
/// file plus small/random siblings written through the descriptor must come back
/// byte-exact under their original names.
/// </summary>
[TestFixture]
public class LittleFsE2eRegressionTests {

  private static byte[] RepetitiveText() {
    using var ms = new MemoryStream();
    for (var i = 0; i < 200; ++i)
      ms.Write(Encoding.UTF8.GetBytes($"Line {i}: The quick brown fox jumps over the lazy dog.\n"));
    return ms.ToArray();
  }

  private static byte[] RandomData() {
    var data = new byte[4096];
    var rng = new Random(1234);
    rng.NextBytes(data);
    return data;
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_CreateThenExtract_RoundTripsMultiBlockFiles() {
    var files = new Dictionary<string, byte[]> {
      ["repeat.txt"] = RepetitiveText(),
      ["small.txt"] = "Hello, World! This is a small test file.\n"u8.ToArray(),
      ["random.dat"] = RandomData(),
    };

    var descriptor = new LittleFsFormatDescriptor();
    using var image = new MemoryStream();
    descriptor.Create(image,
      files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions());

    image.Position = 0;
    var entries = descriptor.List(image, null);
    foreach (var name in files.Keys)
      Assert.That(entries.Any(e => e.Name.TrimStart('/') == name), Is.True,
        $"'{name}' missing from listing; got: {string.Join(", ", entries.Select(e => e.Name))}");

    var tmp = Path.Combine(Path.GetTempPath(), "lfs_e2e_" + Guid.NewGuid().ToString("N"));
    try {
      image.Position = 0;
      descriptor.Extract(image, tmp, null, null);
      foreach (var (name, expected) in files) {
        var found = Directory.EnumerateFiles(tmp, name, SearchOption.AllDirectories).FirstOrDefault();
        Assert.That(found, Is.Not.Null, $"'{name}' not extracted");
        Assert.That(File.ReadAllBytes(found!), Is.EqualTo(expected), $"data mismatch for '{name}'");
      }
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
