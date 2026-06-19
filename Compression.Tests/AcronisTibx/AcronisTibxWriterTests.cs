using System.Buffers.Binary;
using System.Text;
using FileFormat.AcronisTibx;

namespace Compression.Tests.AcronisTibx;

/// <summary>
///   Self-round-trip acceptance gate for <see cref="AcronisTibxWriter"/> — the whole-archive
///   writer for Acronis <c>.tibx</c> (libarchive3 LSM page-store) containers.
///
///   <para>
///     Given a set of files, when <see cref="AcronisTibxWriter.Build"/> emits a container, then
///     <see cref="AcronisTibxReader"/> must parse it back: page-zero ARCH header valid, the
///     expected page-type counts present, the LSM_LEAF bodies LZ4-decode cleanly, and the
///     ItemCommon scanner recovers every file name.
///   </para>
///
///   <para>
///     The writer is NOT advertised via <c>CanCreate</c> — the full LSM B+-tree (Golomb index,
///     dedup short index, commit-info segment chain, AES wrap, content-defined chunking) has no
///     published spec, so vendor-restore validation gates any capability flip.
///   </para>
/// </summary>
[TestFixture]
public class AcronisTibxWriterTests {

  private const int PageSize = AcronisTibxWriter.PageSize;

  // ─── header / page structure ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Build_OutputIsWholePages() {
    var bytes = AcronisTibxWriter.Build([new AcronisTibxWriter.FileSpec("a.txt", "x"u8.ToArray())]);
    Assert.That(bytes.Length % PageSize, Is.EqualTo(0), "Container must be a whole number of 4 KiB pages.");
  }

  [Test, Category("HappyPath")]
  public void Build_EmitsArchMagicAndValidHeader() {
    var bytes = AcronisTibxWriter.Build([new AcronisTibxWriter.FileSpec("a.txt", "x"u8.ToArray())]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);
    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("ARCH"));
      Assert.That(reader.Version, Is.EqualTo(AcronisTibxWriter.DefaultVersion));
    });
  }

  [Test, Category("HappyPath")]
  public void Build_EmitsUuid() {
    var uuid = new byte[16];
    for (var i = 0; i < 16; i++) uuid[i] = (byte)(i + 1);
    var bytes = AcronisTibxWriter.Build([new AcronisTibxWriter.FileSpec("a.txt", "x"u8.ToArray())], uuid);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);
    Assert.That(reader.ArchiveUuid, Is.EqualTo(uuid));
  }

  // ─── page-frame walk round-trip ───────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_PageTypeCountsMatchWrittenPages() {
    var files = new[] {
      new AcronisTibxWriter.FileSpec("a.txt", "alpha"u8.ToArray()),
      new AcronisTibxWriter.FileSpec("b.txt", "beta"u8.ToArray()),
    };
    var bytes = AcronisTibxWriter.Build(files);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);

    Assert.Multiple(() => {
      Assert.That(reader.PageTypeCounts.GetValueOrDefault(AcronisTibxPageType.Hdr), Is.EqualTo(1));
      Assert.That(reader.PageTypeCounts.GetValueOrDefault(AcronisTibxPageType.LsmLeaf), Is.EqualTo(2));
      Assert.That(reader.PageTypeCounts.GetValueOrDefault(AcronisTibxPageType.Data), Is.EqualTo(2));
      Assert.That(reader.PageTypeCounts.GetValueOrDefault(AcronisTibxPageType.Ci), Is.EqualTo(1));
    });
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LeafBodiesDecodeOk() {
    var files = new[] {
      new AcronisTibxWriter.FileSpec("a.txt", "alpha"u8.ToArray()),
      new AcronisTibxWriter.FileSpec("sub/b.txt", "beta"u8.ToArray()),
    };
    var bytes = AcronisTibxWriter.Build(files);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);
    Assert.That(reader.DecodedLeaves, Has.Count.EqualTo(2));
    Assert.That(reader.DecodedLeaves.All(d => d.Status == "ok"), Is.True,
      "Every LSM_LEAF LZ4 chained stream must decode cleanly.");
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_ScannerRecoversEveryLeafName() {
    // ItemCommon carries the leaf name (the format's per-item name attribute); the reader's
    // forensic scan rejects path separators, so leaf-name recovery is the honest gate.
    var files = new[] {
      new AcronisTibxWriter.FileSpec("readme.txt", "abc"u8.ToArray()),
      new AcronisTibxWriter.FileSpec("subdir/nested.txt", "nested"u8.ToArray()),
      new AcronisTibxWriter.FileSpec("subdir/deep/deeper.txt", "deeper"u8.ToArray()),
    };
    var bytes = AcronisTibxWriter.Build(files);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);

    var recovered = reader.ScannedItemNames.Select(n => n.Name).ToList();
    foreach (var leaf in new[] { "readme.txt", "nested.txt", "deeper.txt" })
      Assert.That(recovered, Does.Contain(leaf), $"ItemCommon scan must recover leaf '{leaf}'.");
  }

  // ─── page frame validity ──────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void RoundTrip_EveryTypedPageHasSentinelAndCrc() {
    var bytes = AcronisTibxWriter.Build([new AcronisTibxWriter.FileSpec("a.txt", "x"u8.ToArray())]);
    // Skip page 0 (HDR uses the ARCH path, no frame CRC). Verify each later page frame.
    for (var off = PageSize; off + PageSize <= bytes.Length; off += PageSize) {
      Assert.That(bytes[off], Is.EqualTo(0x41), $"page at 0x{off:X} must lead with the 'A' sentinel");
      var page = bytes.AsSpan(off, PageSize).ToArray();
      var stored = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(0x4, 4));
      // Recompute over the page with the CRC field zeroed.
      for (var i = 0; i < 4; i++) page[0x4 + i] = 0;
      var crc = new Compression.Core.Checksums.Crc32();
      crc.Update(page);
      Assert.That(stored, Is.EqualTo(crc.Value), $"page at 0x{off:X} stored CRC must match recomputed CRC");
    }
  }

  // ─── boundary cases ───────────────────────────────────────────────

  [Test, Category("BoundaryValue")]
  public void RoundTrip_EmptyContentFile() {
    var bytes = AcronisTibxWriter.Build([new AcronisTibxWriter.FileSpec("empty.dat", [])]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);
    Assert.That(reader.ValidHeader, Is.True);
    Assert.That(reader.ScannedItemNames.Select(n => n.Name), Does.Contain("empty.dat"));
  }

  [Test, Category("BoundaryValue")]
  public void RoundTrip_NoFiles_HeaderAndCommitOnly() {
    var bytes = AcronisTibxWriter.Build([]);
    using var ms = new MemoryStream(bytes);
    var reader = new AcronisTibxReader(ms);
    Assert.That(reader.ValidHeader, Is.True);
    Assert.That(reader.PageTypeCounts.GetValueOrDefault(AcronisTibxPageType.LsmLeaf), Is.EqualTo(0));
    Assert.That(reader.PageTypeCounts.GetValueOrDefault(AcronisTibxPageType.Ci), Is.EqualTo(1));
  }
}
