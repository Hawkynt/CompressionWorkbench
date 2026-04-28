using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Refs;

[TestFixture]
public class RefsTests {

  /// <summary>Synthesizes a minimal ReFS boot sector — OEM-id at offset 3, FSRS at 0x46.</summary>
  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    // Jump-instruction prefix (NTFS / ReFS share 0xEB 0x76 0x90).
    image[0] = 0xEB; image[1] = 0x76; image[2] = 0x90;
    // OEM-id "ReFS\0\0\0\0" at offset 3.
    Encoding.ASCII.GetBytes("ReFS").CopyTo(image.AsSpan(3));
    // bytes_per_sector = 4096 at 0x0B.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x0B, 2), 4096);
    // FSRS marker at 0x46.
    Encoding.ASCII.GetBytes("FSRS").CopyTo(image.AsSpan(0x46));
    // length, checksum, reserved, total_sectors, bytes_per_sector, bytes_per_cluster, major, minor.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x46 + 4, 4), 32);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x46 + 8, 2), 0xCAFE);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x46 + 12, 8), 1024UL);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x46 + 20, 4), 4096);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x46 + 24, 4), 65536);
    image[0x46 + 28] = 3;
    image[0x46 + 29] = 14;
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Refs"));
    Assert.That(d.DisplayName, Is.EqualTo("ReFS"));
    Assert.That(d.Extensions, Does.Contain(".refs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(3));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.refs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("volume_header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "refs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.refs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "volume_header.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("oem_id=ReFS"));
      Assert.That(meta, Does.Contain("bytes_per_cluster=65536"));
      Assert.That(meta, Does.Contain("total_sectors=1024"));
      Assert.That(meta, Does.Contain("version_major=3"));
      Assert.That(meta, Does.Contain("version_minor=14"));
      Assert.That(meta, Does.Contain("fsrs_found=True"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[1024]);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.refs"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[16]);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
