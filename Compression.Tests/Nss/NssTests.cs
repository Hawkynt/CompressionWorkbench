using System.Text;
using Compression.Registry;

namespace Compression.Tests.Nss;

[TestFixture]
public class NssTests {

  /// <summary>
  /// Synthesises a minimal NSS-shaped image: 64 KB of zero blocks with our
  /// three primary anchors planted at sector-aligned offsets, plus the two
  /// corroborating brand strings.
  /// </summary>
  private static byte[] BuildMinimal(
      long poolOff = 0,
      long sbOff = 4096,
      long volOff = 8192,
      string volumeName = "SYS") {
    var image = new byte[64 * 1024];
    Encoding.ASCII.GetBytes("NSS Pool").CopyTo(image.AsSpan((int)poolOff, 8));
    Encoding.ASCII.GetBytes("SuperBlk").CopyTo(image.AsSpan((int)sbOff, 8));
    Encoding.ASCII.GetBytes("NSSVolume").CopyTo(image.AsSpan((int)volOff, 9));
    // Volume name follows the magic — printable ASCII run.
    Encoding.ASCII.GetBytes(volumeName).CopyTo(image.AsSpan((int)volOff + 9, volumeName.Length));
    // Corroborating brand strings near the end of the 1-MB window
    // (still within the 64 KB image bounds for the test).
    Encoding.ASCII.GetBytes("Novell").CopyTo(image.AsSpan(16384, 6));
    Encoding.ASCII.GetBytes("NetWare").CopyTo(image.AsSpan(20480, 7));
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Nss.NssFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Nss"));
    Assert.That(d.DisplayName, Is.EqualTo("NSS (Novell Storage Services)"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Extensions, Does.Contain(".nss"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes,
      Is.EqualTo(Encoding.ASCII.GetBytes("NSS Pool")));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.70).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Nss.NssFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nss"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("volume_header.bin"));
    // We expect synthetic anchor entries.
    Assert.That(entries.Any(e => e.Name.StartsWith("pool_anchor_", StringComparison.Ordinal)), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("superblock_anchor_", StringComparison.Ordinal)), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("volume_anchor_", StringComparison.Ordinal)), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesAnchorsAndMetadata() {
    var img = BuildMinimal(volumeName: "VOL1");
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Nss.NssFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "nss_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "volume_header.bin")), Is.True);

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
      Assert.That(meta, Does.Contain("detection_basis=reverse_engineered"));
      Assert.That(meta, Does.Contain("pool_found=True"));
      Assert.That(meta, Does.Contain("superblock_found=True"));
      Assert.That(meta, Does.Contain("volume_found=True"));
      Assert.That(meta, Does.Contain("novell_brand_found=True"));
      Assert.That(meta, Does.Contain("netware_brand_found=True"));
      Assert.That(meta, Does.Contain("volume_name=VOL1"));
      Assert.That(meta, Does.Contain("detected_magic=NSS Pool+SuperBlk+NSSVolume"));

      // At least one anchor blob exists with the right "NSS Pool" / "SuperBlk" / "NSSVolume" inside.
      var anchorFiles = Directory.EnumerateFiles(outDir, "*_anchor_*.bin").ToList();
      Assert.That(anchorFiles, Is.Not.Empty);
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Nss.NssFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nss"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("volume_header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartialNoAnchors() {
    var rng = new Random(0xBADA);
    var buf = new byte[4096];
    rng.NextBytes(buf);
    // Stomp any accidental ASCII run that might hit "NSS Pool".
    for (var i = 0; i + 8 <= buf.Length; i += 8) {
      buf[i] = 0;
      buf[i + 1] = 0;
    }
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Nss.NssFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nss"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("volume_header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Defragment_Throws_NotSupported() {
    var d = new FileSystem.Nss.NssFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    var ex = Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
    Assert.That(ex!.Message, Does.Contain("read-only"));
  }

  [Test, Category("ErrorHandling")]
  public void DefragmentWithOptions_Throws_NotSupported() {
    var d = new FileSystem.Nss.NssFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    var ex = Assert.Throws<NotSupportedException>(() => d.Defragment(ms, new DefragOptions()));
    Assert.That(ex!.Message, Does.Contain("read-only"));
  }

  [Test, Category("HappyPath")]
  public void Headers_TryParse_Detects_All_Three_Anchors() {
    var img = BuildMinimal();
    var h = FileSystem.Nss.NssHeaders.TryParse(img);
    Assert.That(h.AnyValid, Is.True);
    Assert.That(h.PoolFound, Is.True);
    Assert.That(h.SuperblockFound, Is.True);
    Assert.That(h.VolumeFound, Is.True);
    Assert.That(h.PoolFoundOffset, Is.EqualTo(0));
    Assert.That(h.SuperblockFoundOffset, Is.EqualTo(4096));
    Assert.That(h.VolumeFoundOffset, Is.EqualTo(8192));
    Assert.That(h.NovellFound, Is.True);
    Assert.That(h.NetWareFound, Is.True);
    Assert.That(h.HeaderRaw.Length, Is.EqualTo(4096));
  }

  [Test, Category("HappyPath")]
  public void Headers_OnlyPoolAnchor_Still_Valid() {
    var img = new byte[8192];
    Encoding.ASCII.GetBytes("NSS Pool").CopyTo(img.AsSpan(0, 8));
    var h = FileSystem.Nss.NssHeaders.TryParse(img);
    Assert.That(h.AnyValid, Is.True);
    Assert.That(h.PoolFound, Is.True);
    Assert.That(h.VolumeFound, Is.False);
    Assert.That(h.SuperblockFound, Is.False);
  }
}
