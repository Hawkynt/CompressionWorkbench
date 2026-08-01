#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.SmartFs;

namespace Compression.Tests.SmartFs;

/// <summary>
/// SmartFS volumes this repository lays out, read back through its own reader.
/// </summary>
/// <remarks>
/// The writer emits what <c>mksmartfs</c> leaves behind plus the files: logical
/// sector N in physical sector N, sequence numbers at zero, free sectors erased.
/// Wear-level rotation is what a running NuttX target adds afterwards, and the
/// reader follows the logical chain rather than assuming adjacency, so a volume
/// that has been rotated reads the same way.
/// </remarks>
[TestFixture]
public class SmartFsWriterTests {

  [Test, Category("HappyPath")]
  public void Build_RoundTripsEveryFile() {
    var writer = new SmartFsWriter();
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < 5; ++i) {
      var payload = new byte[i == 0 ? 0 : 1500 + i * 3000];
      for (var b = 0; b < payload.Length; ++b) payload[b] = (byte)(b * 31 + i);
      expected[$"FILE{i}.BIN"] = payload;
      writer.AddFile($"FILE{i}.BIN", payload);
    }

    using var image = new MemoryStream(writer.Build());
    using var reader = new SmartFsReader(image);

    foreach (var (name, payload) in expected) {
      var entry = reader.Entries.SingleOrDefault(e => e.Name == name);
      Assert.That(entry, Is.Not.Null, $"'{name}' is missing from the volume.");
      Assert.That(Digest(reader.Extract(entry!)), Is.EqualTo(Digest(payload)),
        $"'{name}' did not read back byte for byte.");
    }
  }

  /// <summary>
  /// A file longer than one sector is a chain, and the chain is what the reader
  /// has to follow — a reader that took the first sector alone would still pass
  /// on small files.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Build_ChainsAFileAcrossManySectors() {
    var payload = new byte[64 * 1024];
    for (var b = 0; b < payload.Length; ++b) payload[b] = (byte)(b % 251);

    var writer = new SmartFsWriter { SectorSize = 512 };
    writer.AddFile("BIG.BIN", payload);

    using var image = new MemoryStream(writer.Build());
    using var reader = new SmartFsReader(image);
    var entry = reader.Entries.Single(e => e.Name == "BIG.BIN");

    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  /// <summary>The signature, sector size and root-sector count a driver reads first.</summary>
  [Test, Category("HappyPath")]
  public void Build_EmitsAFormatSectorTheReaderAccepts() {
    var writer = new SmartFsWriter { SectorSize = 2048 };
    writer.AddFile("A.TXT", "hello"u8.ToArray());

    using var image = new MemoryStream(writer.Build());
    using var reader = new SmartFsReader(image);

    Assert.Multiple(() => {
      Assert.That(reader.ValidFormatSector, Is.True);
      Assert.That(reader.SectorSize, Is.EqualTo(2048u));
      Assert.That(reader.RootSectorCount, Is.EqualTo((ushort)1));
    });
  }

  /// <summary>A name the entry's fixed field cannot hold is refused, not truncated.</summary>
  [Test, Category("ErrorHandling")]
  public void Build_RefusesANameLongerThanTheEntryField() {
    var writer = new SmartFsWriter();
    writer.AddFile(new string('x', 32), [1, 2, 3]);

    var ex = Assert.Throws<InvalidOperationException>(() => writer.Build());
    Assert.That(ex!.Message, Does.Contain("16"));
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
