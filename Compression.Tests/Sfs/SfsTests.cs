using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Sfs;

[TestFixture]
public class SfsTests {

  /// <summary>Synthesize an Amiga SFS root block at offset 0 with all fields big-endian.</summary>
  private static byte[] BuildMinimal() {
    var image = new byte[2048];
    // "SFS\0" magic at offset 0.
    image[0] = 0x53; image[1] = 0x46; image[2] = 0x53; image[3] = 0x00;
    // checksum
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x04, 4), 0xCAFEBABEu);
    // ownblock
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x08, 4), 0x00000001u);
    // version 2
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x0C, 2), 2);
    // sequence number
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x0E, 2), 5);
    // datecreated
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x10, 4), 1234567890u);
    // The root block carries two reserved longwords after its flag byte, not
    // one. Counting a single one put these two fields a word early — here and
    // in the reader alike, so the fixture agreed with the parser and neither
    // agreed with a volume.
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x30, 4), 1760);   // totalblocks
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x34, 4), 512);    // blocksize
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Sfs.SfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Sfs"));
    Assert.That(d.DisplayName, Is.EqualTo("Amiga SFS"));
    Assert.That(d.Extensions, Does.Contain(".sfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.95).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsRootBlock() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Sfs.SfsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.sfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("root_block.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedRoot() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Sfs.SfsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "sfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.sfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "root_block.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("version=2"));
      Assert.That(meta, Does.Contain("sequence_number=5"));
      Assert.That(meta, Does.Contain("total_blocks=1760"));
      Assert.That(meta, Does.Contain("block_size=512"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[2048]);
    var d = new FileSystem.Sfs.SfsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.sfs"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[8]);
    var d = new FileSystem.Sfs.SfsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }

  /// <summary>
  /// SFS is written whole, and an existing volume is edited the same way: the
  /// files are read out and the volume laid out again through the writer.
  /// </summary>
  /// <remarks>
  /// Adding or removing a file in place would mean allocating against the
  /// bitmap, threading the object node table and splitting the extent tree,
  /// none of which this implements. The rebuild costs the whole volume instead,
  /// which is what the capability promises and what this checks: the edit is
  /// offered, and what it produces reads back.
  /// </remarks>
  [Test, Category("HappyPath")]
  public void Descriptor_EditsAVolumeByLayingItOutAgain() {
    var d = new FileSystem.Sfs.SfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(), "SFS writes whole volumes, so it says so.");
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(), "SFS edits a volume by rebuilding it, so it says so.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var first = new byte[3000];
    var second = new byte[700];
    for (var i = 0; i < first.Length; ++i) first[i] = (byte)(i * 7);
    for (var i = 0; i < second.Length; ++i) second[i] = (byte)(i * 13);

    using var image = new MemoryStream();
    d.Create(image, [new ArchiveInputInfo("", "first.bin", false, first)], new FormatCreateOptions());

    d.Add(image, [new ArchiveInputInfo("", "second.bin", false, second)]);
    image.Position = 0;
    Assert.That(d.List(image, null).Select(e => e.Name), Is.SupersetOf(new[] { "first.bin", "second.bin" }));

    d.Remove(image, ["first.bin"]);
    image.Position = 0;
    var names = d.List(image, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("first.bin"));
    Assert.That(names, Does.Contain("second.bin"));

    image.Position = 0;
    var volume = new FileSystem.Sfs.SfsVolume(image);
    Assert.That(volume.Valid, Is.True, volume.Status);
    Assert.That(volume.Read(volume.Files.Single(f => f.Name == "second.bin")), Is.EqualTo(second));
  }
}
