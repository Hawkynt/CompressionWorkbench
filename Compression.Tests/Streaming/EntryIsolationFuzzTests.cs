using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Streaming;

/// <summary>
/// Fuzz / invariant tests that prove the per-entry bounded-stream contract
/// holds across every source format: <see cref="IArchiveFormatOperations.OpenEntry"/>
/// returns a stream that cannot leak slack bytes, adjacent entries,
/// padding, or metadata regions into the consumer.
///
/// <para>The technique: build a real archive whose ENTRY DATA is distinctive
/// (sequential or pattern bytes) but whose slack / padding / metadata are
/// stamped with a forbidden marker byte (<c>0xDE</c>). After opening each
/// entry via the descriptor's bounded stream and copying it to a sink, the
/// sink MUST contain only the entry's own bytes — never the marker.</para>
/// </summary>
[TestFixture]
public class EntryIsolationFuzzTests {

  private const byte ForbiddenMarker = 0xDE;

  private static byte[] StampedEntry(int seed, int size) {
    // Entry bytes must NOT contain the forbidden marker, so the post-extraction
    // check can search for marker leakage without false positives.
    var rnd = new Random(seed);
    var buf = new byte[size];
    for (var i = 0; i < buf.Length; i++) {
      byte b;
      do { b = (byte)rnd.Next(256); } while (b == ForbiddenMarker);
      buf[i] = b;
    }
    return buf;
  }

  private static void AssertNoMarkerLeak(byte[] payload, byte[] result, string ctx) {
    Assert.That(result.Length, Is.EqualTo(payload.Length),
      $"{ctx}: bounded stream produced wrong byte count");
    for (var i = 0; i < result.Length; i++) {
      Assert.That(result[i], Is.Not.EqualTo(ForbiddenMarker),
        $"{ctx}: forbidden marker leaked at offset {i}");
    }
    Assert.That(result, Is.EqualTo(payload).AsCollection,
      $"{ctx}: bounded stream produced wrong bytes");
  }

  // ── FAT source: cluster-tail slack is the canonical leak target ──────────

  [Test, Category("Spec")]
  public void Fat_OpenEntry_NeverLeaksClusterTailSlack() {
    // 1500-byte entry on a 4 KB cluster image leaves 2596 bytes of tail
    // slack — perfect target for leakage.
    var payload = StampedEntry(seed: 1234, size: 1500);
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("DATA.BIN", payload);
    var image = w.BuildAutoSized(requestedClusterSize: 4096);

    // Stamp the cluster-tail slack with the forbidden marker. The
    // cluster containing DATA.BIN starts in the data area; we don't
    // need to find the exact offset — stamping every byte past the
    // entry within its cluster span is sufficient. Easiest: scan the
    // image, find the payload's first 16 bytes, then overwrite the
    // 2596 bytes after the payload with the marker.
    StampSlackForPayload(image, payload, slackLen: 2596);

    var ops = new FileSystem.Fat.FatFormatDescriptor();
    using var src = new MemoryStream(image);
    using var bounded = ops.OpenEntry(src, "DATA.BIN", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>(),
      "FAT OpenEntry returns a BoundedEntryStream");

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    AssertNoMarkerLeak(payload, sink.ToArray(), "FAT cluster slack");
  }

  // ── ZIP source: next entry's bytes immediately follow in the archive ─────

  [Test, Category("Spec")]
  public void Zip_OpenEntry_DoesNotBleedIntoNextEntry() {
    var first  = StampedEntry(seed: 11, size: 800);
    var second = StampedEntry(seed: 22, size: 800);
    byte[] image;
    using (var ms = new MemoryStream()) {
      var zw = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true);
      zw.AddEntry("first.bin", first, FileFormat.Zip.ZipCompressionMethod.Store);
      // Sandwich a stamped marker block so any "read past the first entry"
      // would visibly leak. ZIP stores second's local-file header here,
      // but we also write a stamped second entry — its payload is
      // distinct, so checking the first entry's read against either
      // catches a leak.
      zw.AddEntry("second.bin", second, FileFormat.Zip.ZipCompressionMethod.Store);
      zw.Finish();
      image = ms.ToArray();
    }
    // Stamp every 0xDE-byte-sized region in archive's slack/header areas
    // with the forbidden marker — but the entry payloads themselves
    // contain no markers by construction, so stamping outside the
    // central directory is impossible without knowing offsets. We
    // instead trust the bounded-size contract: requesting first.bin
    // must produce exactly 800 bytes from `first`, never bleeding into
    // anything else.

    var ops = new FileFormat.Zip.ZipFormatDescriptor();
    using var src = new MemoryStream(image);
    using var bounded = ops.OpenEntry(src, "first.bin", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(bounded.Length, Is.EqualTo(first.Length));

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    Assert.That(sink.ToArray(), Is.EqualTo(first).AsCollection,
      "ZIP entry read must not bleed into second entry");
  }

  // ── TAR source: each entry padded to 512-byte block ──────────────────────

  [Test, Category("Spec")]
  public void Tar_OpenEntry_DoesNotReturnBlockPadding() {
    // 500-byte payload pads to a 512-byte block ⇒ 12 bytes of padding.
    var payload = StampedEntry(seed: 33, size: 500);

    byte[] image;
    using (var ms = new MemoryStream()) {
      var tw = new FileFormat.Tar.TarWriter(ms, leaveOpen: true);
      tw.AddEntry(new FileFormat.Tar.TarEntry { Name = "data.bin", Size = payload.Length }, payload);
      tw.Finish();
      image = ms.ToArray();
    }
    // Locate the entry data block and stamp the trailing 12 padding bytes
    // with the forbidden marker.
    StampSlackForPayload(image, payload, slackLen: 12);

    var ops = new FileFormat.Tar.TarFormatDescriptor();
    using var src = new MemoryStream(image);
    using var bounded = ops.OpenEntry(src, "data.bin", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>());

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    AssertNoMarkerLeak(payload, sink.ToArray(), "TAR block padding");
  }

  // ── 7z source: solid block isolation per entry ───────────────────────────

  [Test, Category("Spec")]
  public void SevenZip_OpenEntry_ReturnsBoundedStream() {
    var first  = StampedEntry(seed: 55, size: 600);
    var second = StampedEntry(seed: 66, size: 400);
    byte[] image;
    using (var ms = new MemoryStream()) {
      var sw = new FileFormat.SevenZip.SevenZipWriter(ms, FileFormat.SevenZip.SevenZipCodec.Lzma2);
      sw.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "a.bin", Size = first.Length }, first);
      sw.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "b.bin", Size = second.Length }, second);
      sw.Finish();
      image = ms.ToArray();
    }

    var ops = new FileFormat.SevenZip.SevenZipFormatDescriptor();
    using var src = new MemoryStream(image);
    using var bounded = ops.OpenEntry(src, "a.bin", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(bounded.Length, Is.EqualTo(first.Length));

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    Assert.That(sink.ToArray(), Is.EqualTo(first).AsCollection,
      "7z entry read must not bleed into sibling entry within the same solid block");
  }

  // ── ZIP-derived containers (APK + JAR) ────────────────────────────────

  [Test, Category("Spec")]
  public void Apk_OpenEntry_ReturnsBoundedStream() {
    var first  = StampedEntry(seed: 77, size: 700);
    var second = StampedEntry(seed: 88, size: 500);
    byte[] image;
    using (var ms = new MemoryStream()) {
      var zw = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true);
      zw.AddEntry("classes.dex", first, FileFormat.Zip.ZipCompressionMethod.Store);
      zw.AddEntry("AndroidManifest.xml", second, FileFormat.Zip.ZipCompressionMethod.Store);
      zw.Finish();
      image = ms.ToArray();
    }

    var ops = new FileFormat.Apk.ApkFormatDescriptor();
    using var src = new MemoryStream(image);
    using var bounded = ops.OpenEntry(src, "classes.dex", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>(),
      "APK OpenEntry returns a BoundedEntryStream");
    Assert.That(bounded.Length, Is.EqualTo(first.Length));

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    Assert.That(sink.ToArray(), Is.EqualTo(first).AsCollection,
      "APK entry read must produce exactly the source bytes");
  }

  [Test, Category("Spec")]
  public void Jar_OpenEntry_ReturnsBoundedStream() {
    var payload = StampedEntry(seed: 99, size: 1200);
    byte[] image;
    using (var ms = new MemoryStream()) {
      var zw = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true);
      zw.AddEntry("META-INF/MANIFEST.MF", payload, FileFormat.Zip.ZipCompressionMethod.Store);
      zw.Finish();
      image = ms.ToArray();
    }

    var ops = new FileFormat.Jar.JarFormatDescriptor();
    using var src = new MemoryStream(image);
    using var bounded = ops.OpenEntry(src, "META-INF/MANIFEST.MF", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>());

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    Assert.That(sink.ToArray(), Is.EqualTo(payload).AsCollection);
  }

  // ── Compound TAR delegation (gzip-wrapped TAR) ────────────────────────

  [Test, Category("Spec")]
  public void TarGz_OpenEntry_DelegatesToInnerTarBounded() {
    var payload = StampedEntry(seed: 111, size: 900);
    byte[] image;
    // Build a TAR archive, then gzip-wrap it. CompoundTarDescriptor's
    // native OpenEntry must decompress, delegate to TAR, and return a
    // BoundedEntryStream that produces exactly the payload — never the
    // 124 bytes of TAR block padding past the 900-byte payload.
    using (var tarMs = new MemoryStream()) {
      var tw = new FileFormat.Tar.TarWriter(tarMs, leaveOpen: true);
      tw.AddEntry(new FileFormat.Tar.TarEntry { Name = "data.bin", Size = payload.Length }, payload);
      tw.Finish();
      var tarBytes = tarMs.ToArray();
      using var outMs = new MemoryStream();
      using (var gz = new FileFormat.Gzip.GzipStream(outMs,
          Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true)) {
        gz.Write(tarBytes, 0, tarBytes.Length);
      }
      image = outMs.ToArray();
    }

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var tarGzOps = Compression.Registry.FormatRegistry.GetArchiveOps("TarGz");
    Assert.That(tarGzOps, Is.Not.Null, "tar.gz descriptor must be registered");
    using var src = new MemoryStream(image);
    using var bounded = tarGzOps!.OpenEntry(src, "data.bin", null);
    Assert.That(bounded, Is.InstanceOf<BoundedEntryStream>());

    using var sink = new MemoryStream();
    bounded.CopyTo(sink);
    Assert.That(sink.ToArray(), Is.EqualTo(payload).AsCollection,
      "tar.gz entry read must produce exactly the inner TAR entry bytes — no block padding leak");
  }

  /// <summary>
  /// Locates <paramref name="payload"/> in <paramref name="image"/> (verbatim
  /// — works for stored/uncompressed archives) and stamps the
  /// <paramref name="slackLen"/> bytes immediately after it with
  /// <see cref="ForbiddenMarker"/>. No-op if the payload isn't found
  /// (the test still passes through the bounded-size length check).
  /// </summary>
  private static void StampSlackForPayload(byte[] image, byte[] payload, int slackLen) {
    if (payload.Length == 0 || slackLen <= 0) return;
    // Linear scan for first occurrence.
    for (var i = 0; i + payload.Length + slackLen <= image.Length; i++) {
      var match = true;
      for (var j = 0; j < Math.Min(payload.Length, 64); j++) {
        if (image[i + j] != payload[j]) { match = false; break; }
      }
      if (!match) continue;
      // Confirm full payload match.
      var fullMatch = true;
      for (var j = 0; j < payload.Length; j++) {
        if (image[i + j] != payload[j]) { fullMatch = false; break; }
      }
      if (!fullMatch) continue;
      // Stamp the trailing slackLen bytes.
      for (var k = 0; k < slackLen; k++)
        image[i + payload.Length + k] = ForbiddenMarker;
      return;
    }
  }
}
