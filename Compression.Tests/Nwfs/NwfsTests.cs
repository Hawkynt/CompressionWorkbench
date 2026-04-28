using System.Text;
using Compression.Registry;

namespace Compression.Tests.Nwfs;

[TestFixture]
public class NwfsTests {

  /// <summary>
  /// Synthesises a minimal NWFS386 partition image: HOTFIX00 at offset 0x4000,
  /// MIRROR00 at the next 512 B sector, "NetWare Volumes" at a later sector
  /// to mimic the real volume area location. Per the zhmu/nwfs RE doc.
  /// </summary>
  private static byte[] BuildMinimal() {
    var image = new byte[64 * 1024];
    // HOTFIX00 at 0x4000.
    Encoding.ASCII.GetBytes("HOTFIX00").CopyTo(image.AsSpan(0x4000, 8));
    // MIRROR00 at 0x4000 + 512 (next sector).
    Encoding.ASCII.GetBytes("MIRROR00").CopyTo(image.AsSpan(0x4000 + 512, 8));
    // "NetWare Volumes\0" further down, at a sector-aligned offset within the
    // bounded read window.
    Encoding.ASCII.GetBytes("NetWare Volumes\0").CopyTo(image.AsSpan(0xC000, 16));
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Nwfs.NwfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Nwfs"));
    Assert.That(d.DisplayName, Does.StartWith("NWFS"));
    Assert.That(d.Extensions, Does.Contain(".nwfs"));
    Assert.That(d.Extensions, Does.Contain(".nwvol"));
    Assert.That(d.Extensions, Does.Contain(".netware"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0x4000));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("HOTFIX00"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Nwfs.NwfsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(3));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nwfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("volume_header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_RecordsAllFoundSignatures() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Nwfs.NwfsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "nwfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.nwfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "volume_header.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      // NWFS always emits parse_status=partial — even with all magics found
      // we don't claim "ok" because the layout is RE'd.
      Assert.That(meta, Does.Contain("parse_status=partial"));
      Assert.That(meta, Does.Contain("detection_basis=reverse_engineered"));
      Assert.That(meta, Does.Contain("hotfix_found=True"));
      Assert.That(meta, Does.Contain("hotfix_offset=16384"));
      Assert.That(meta, Does.Contain("mirror_found=True"));
      Assert.That(meta, Does.Contain("volumes_found=True"));
      Assert.That(meta, Does.Contain("detected_magic=HOTFIX00+MIRROR00+NetWare Volumes"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Nwfs.NwfsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.nwfs"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var rng = new Random(0xDEAD);
    var buf = new byte[64 * 1024];
    rng.NextBytes(buf);
    // Stomp the canonical HOTFIX offset and any chance of the magic string
    // landing on a 512 B sector boundary by zeroing first 16 bytes of every
    // sector in the bounded read window.
    for (var i = 0; i + 16 <= buf.Length; i += 512)
      Array.Fill<byte>(buf, 0, i, 16);
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Nwfs.NwfsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nwfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("volume_header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[1024];
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Nwfs.NwfsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "nwfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.nwfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
      Assert.That(meta, Does.Contain("hotfix_found=False"));
      Assert.That(meta, Does.Contain("mirror_found=False"));
      Assert.That(meta, Does.Contain("volumes_found=False"));
      Assert.That(meta, Does.Contain("detected_magic=none"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Headers_Constants_Match_RE_Spec() {
    Assert.That(Encoding.ASCII.GetString(FileSystem.Nwfs.NwfsHeaders.HotfixMagic),
                Is.EqualTo("HOTFIX00"));
    Assert.That(Encoding.ASCII.GetString(FileSystem.Nwfs.NwfsHeaders.MirrorMagic),
                Is.EqualTo("MIRROR00"));
    // VolumesMagic includes a trailing NUL — strip it for the readable check.
    var volStr = Encoding.ASCII.GetString(FileSystem.Nwfs.NwfsHeaders.VolumesMagic).TrimEnd('\0');
    Assert.That(volStr, Is.EqualTo("NetWare Volumes"));
    Assert.That(FileSystem.Nwfs.NwfsHeaders.HotfixOffset, Is.EqualTo(0x4000L));
    Assert.That(FileSystem.Nwfs.NwfsHeaders.HotfixSector, Is.EqualTo(32));
  }
}
