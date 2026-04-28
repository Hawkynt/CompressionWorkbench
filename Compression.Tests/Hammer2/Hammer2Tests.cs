using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Hammer2;

[TestFixture]
public class Hammer2Tests {

  /// <summary>Synthesises a minimal HAMMER2 volume-data sector at offset 0.</summary>
  private static byte[] BuildMinimal(long voluSize = 64L * 1024 * 1024) {
    var image = new byte[64 * 1024]; // one HAMMER2 VOLUME_BYTES sector.

    // magic uint64 LE = HAMMER2_VOLUME_ID_HBO = 0x48414d3205172011.
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0, 8), 0x48414d3205172011UL);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(8, 8), 0x0000_0000_0000_2000L);  // boot_beg
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(16, 8), 0x0000_0000_0010_0000L); // boot_end
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(24, 8), 0x0000_0000_0010_0000L); // aux_beg
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(32, 8), 0x0000_0000_0020_0000L); // aux_end
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(40, 8), voluSize);                // volu_size
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(48, 4), 1u);                     // version
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(52, 4), 0x00000005u);            // flags
    image[56] = 1;  // copyid
    image[57] = 1;  // freemap_version
    image[58] = 0;  // peer_type
    image[59] = 0;  // volu_id
    image[60] = 1;  // nvolumes

    // fsid + fstype UUIDs at +64 / +80 — recognisable patterns.
    for (var i = 0; i < 16; i++) image[64 + i] = (byte)(0xA0 + i);
    var hammer2FsType = new byte[] {
      0x5C, 0xBB, 0x9A, 0xD1, 0x86, 0x2D, 0x11, 0xDC,
      0xA9, 0x4D, 0x01, 0x30, 0x1B, 0xB8, 0xA9, 0xF5
    };
    hammer2FsType.CopyTo(image.AsSpan(80, 16));
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Hammer2"));
    Assert.That(d.DisplayName, Is.EqualTo("HAMMER2 (DragonFly BSD)"));
    Assert.That(d.Extensions, Does.Contain(".hammer2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
    Assert.That(d.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { 0x11, 0x20, 0x17, 0x05, 0x32, 0x4D, 0x41, 0x48 }));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(3));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.hammer2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("volume_header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal(voluSize: 12345678L);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "hammer2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.hammer2")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "volume_header.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("magic=0x48414D3205172011"));
      Assert.That(meta, Does.Contain("byte_swapped=False"));
      Assert.That(meta, Does.Contain("version=1"));
      Assert.That(meta, Does.Contain("volu_size=12345678"));
      Assert.That(meta, Does.Contain("nvolumes=1"));
      Assert.That(meta, Does.Contain("copyid=1"));
      // HAMMER2 fstype UUID 5cbb9ad1-862d-11dc-a94d-01301bb8a9f5 in hex.
      Assert.That(meta, Does.Contain("5CBB9AD1862D11DCA94D01301BB8A9F5"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void List_AlternateByteOrderMagic_RecognisedAsValid() {
    var img = BuildMinimal();
    // Overwrite magic with HAMMER2_VOLUME_ID_ABO.
    BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(0, 8), 0x11201705324d4148UL);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("volume_header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.hammer2"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var rng = new Random(0xBEEF);
    var buf = new byte[4096];
    rng.NextBytes(buf);
    // Stomp magic so random bytes don't accidentally match either HBO or ABO.
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x00; buf[3] = 0x00;
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.hammer2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("volume_header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[256];
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Hammer2.Hammer2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "hammer2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.hammer2")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void VolumeData_Magic_Bytes_Match() {
    Assert.That(FileSystem.Hammer2.Hammer2VolumeData.VolumeIdHbo,
                Is.EqualTo(0x48414d3205172011UL));
    Assert.That(FileSystem.Hammer2.Hammer2VolumeData.VolumeIdAbo,
                Is.EqualTo(0x11201705324d4148UL));
    Assert.That(FileSystem.Hammer2.Hammer2VolumeData.MagicBytesHboLE,
                Is.EqualTo(new byte[] { 0x11, 0x20, 0x17, 0x05, 0x32, 0x4D, 0x41, 0x48 }));
    Assert.That(FileSystem.Hammer2.Hammer2VolumeData.VolumeBytes, Is.EqualTo(65536));
  }
}
