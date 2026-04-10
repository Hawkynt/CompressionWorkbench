using System.Buffers.Binary;

namespace Compression.Tests.Zfs;

[TestFixture]
public class ZfsTests {

  private static byte[] BuildMinimalZfs() {
    var data = new byte[512 * 1024]; // 512KB minimum for one label
    // Write uberblock at offset 128KB with magic
    var ubOff = 128 * 1024;
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(ubOff), 0x00BAB10C); // magic
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(ubOff + 8), 1); // version
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(ubOff + 16), 1); // txg
    return data;
  }

  [Test, Category("HappyPath")]
  public void Reader_ValidUberblock() {
    var data = BuildMinimalZfs();
    using var ms = new MemoryStream(data);
    var r = new FileFormat.Zfs.ZfsReader(ms);
    // No entries (can't fully traverse ZFS), but shouldn't throw
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Zfs.ZfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Zfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".zfs"));
    Assert.That(desc.Extensions, Does.Contain(".zpool"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Zfs.ZfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NoUberblock_Throws() {
    var data = new byte[512 * 1024];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Zfs.ZfsReader(ms));
  }
}
