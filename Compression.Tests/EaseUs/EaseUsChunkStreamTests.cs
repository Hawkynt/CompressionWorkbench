using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileFormat.EaseUs;

namespace Compression.Tests.EaseUs;

/// <summary>
/// R/O chunk-stream acceptance gate for the EaseUS PBD reader: pins the
/// per-zlib-substream trial-inflate scanner behavior, the chunk inventory
/// in metadata.ini, the per-chunk forensic entries, and the
/// FailedHeaderInvalid / InflatedOverCap fail-soft codes that keep
/// pathological inputs (encrypted body region, false-positive 0x78 hits,
/// oversized payloads) from breaking the surface.
///
/// <para>
/// EaseUS Todo Backup (.pbd) wraps every payload chunk in a proprietary
/// block-allocation table that gates sector reconstruction. The
/// chunk-stream treatment is the honest promotion ceiling: we surface
/// every confirmed zlib substream as a forensic entry, with offset and
/// length stamped into the entry name so chain-mate diffing works
/// without parsing metadata.ini, and we keep sector reconstruction
/// Stage-0 because no offline tooling can resolve offset-to-LBA without
/// the vendor's block-allocation table and (for encrypted backups) the
/// AES-256 key envelope.
/// </para>
/// </summary>
[TestFixture]
public class EaseUsChunkStreamTests {

  /// <summary>
  /// Returns a fresh zlib-compressed substream of <paramref name="payload"/>
  /// at the configured <see cref="CompressionLevel"/> — matches what
  /// EaseUS writes for every body chunk inside a real .pbd container.
  /// </summary>
  private static byte[] MakeZlib(byte[] payload, CompressionLevel level = CompressionLevel.Optimal) {
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, level, leaveOpen: true))
      z.Write(payload, 0, payload.Length);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a synthetic .pbd image with a real IMGF header and the given
  /// sequence of real zlib substreams glued together (with optional
  /// inter-stream padding so we exercise the byte-by-byte scan). Each
  /// stream is a genuine ZLibStream-emitted blob so the reader's
  /// trial-inflate will succeed on each one.
  /// </summary>
  private static byte[] BuildImageWithChunks(
    IReadOnlyList<byte[]> chunkPayloads,
    int padBetweenChunks = 4,
    string magic = "IMGF",
    uint headerWord = 0x0000052Cu,
    uint versionWord = 0x00020000u,
    string? sourcePath = "G:\\backup\\test.pbd",
    int trailingFfPadding = 8,
    CompressionLevel level = CompressionLevel.Optimal
  ) {
    var body = new List<byte>();
    body.AddRange(Encoding.ASCII.GetBytes(magic));
    Span<byte> tmp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, headerWord);
    body.AddRange(tmp.ToArray());
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, versionWord);
    body.AddRange(tmp.ToArray());

    // Embedded UTF-16LE source path.
    if (!string.IsNullOrEmpty(sourcePath)) {
      foreach (var ch in sourcePath) {
        body.Add((byte)ch);
        body.Add(0x00);
      }
      body.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03, 0x04 });
    }

    // Real zlib substreams glued together with inter-chunk padding.
    foreach (var payload in chunkPayloads) {
      var zlib = MakeZlib(payload, level);
      body.AddRange(zlib);
      for (var i = 0; i < padBetweenChunks; i++) body.Add(0xAB);
    }

    // Trailer + 0xFF padding so the trailer scan still passes.
    body.AddRange(Encoding.ASCII.GetBytes("IMGF"));
    body.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04 });
    for (var i = 0; i < trailingFfPadding; i++) body.Add(0xFF);

    return body.ToArray();
  }

  // ---------------------------------------------------------------------
  // Per-chunk trial inflate — happy path.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Scanner_Inflates_RealSingleZlibStream() {
    var payload = Encoding.ASCII.GetBytes("hello, EaseUS PBD reverse engineering — chunk #1");
    var img = BuildImageWithChunks(new[] { payload });
    using var ms = new MemoryStream(img);

    var r = new EaseUsReader(ms);
    Assert.That(r.ConfirmedZlibChunkCount, Is.GreaterThanOrEqualTo(1),
      "At least one real zlib substream must inflate end-to-end.");

    var confirmed = r.Chunks.Single(c => c.InflateStatus == EaseUsChunkInflateStatus.Inflated);
    Assert.That(confirmed.DecompressedLength, Is.EqualTo(payload.Length));
    Assert.That(confirmed.Payload, Is.EqualTo(payload));
    Assert.That(confirmed.CompressedLength, Is.GreaterThan(0));
    Assert.That(confirmed.FchByte, Is.AnyOf((byte)0x01, (byte)0x9C, (byte)0xDA));
  }

  [Test, Category("HappyPath")]
  public void Scanner_Inflates_MultipleRealZlibStreams() {
    var payloads = new[] {
      Encoding.ASCII.GetBytes("chunk-A header metadata bank — offset 0x98 surrogate"),
      Encoding.ASCII.GetBytes("chunk-B volume layout descriptor — offset 0x10F surrogate"),
      Encoding.ASCII.GetBytes("chunk-C first payload sector — offset 0xB28 surrogate"),
    };
    var img = BuildImageWithChunks(payloads);
    using var ms = new MemoryStream(img);

    var r = new EaseUsReader(ms);
    var inflated = r.Chunks
      .Where(c => c.InflateStatus == EaseUsChunkInflateStatus.Inflated)
      .ToList();
    Assert.That(inflated, Has.Count.EqualTo(payloads.Length));
    for (var i = 0; i < payloads.Length; ++i)
      Assert.That(inflated[i].Payload, Is.EqualTo(payloads[i]),
        $"Chunk #{i} payload must round-trip through the trial inflate.");

    Assert.That(r.TotalDecompressedChunkBytes,
      Is.EqualTo(payloads.Sum(p => p.Length)));
    Assert.That(r.TotalCompressedChunkBytes,
      Is.EqualTo(inflated.Sum(c => c.CompressedLength)));
  }

  [Test, Category("HappyPath")]
  public void Scanner_RecordsOffsets_StableAcrossPayloadShift() {
    // Mirrors the Rune-Server observation: metadata-bank chunks at fixed
    // offsets (0x98 / 0x10F surrogates here), payload chunks shifting by
    // the payload-delta byte count when the input grows.
    var headerBank = Encoding.ASCII.GetBytes("header-bank-metadata-stable");
    var v1 = new[] {
      headerBank,
      Encoding.ASCII.GetBytes("hello world"),
    };
    var v2 = new[] {
      headerBank,
      Encoding.ASCII.GetBytes("hello world123"),  // 3 bytes longer
    };

    using var msV1 = new MemoryStream(BuildImageWithChunks(v1));
    using var msV2 = new MemoryStream(BuildImageWithChunks(v2));
    var r1 = new EaseUsReader(msV1);
    var r2 = new EaseUsReader(msV2);

    var c1Inflated = r1.Chunks.Where(c => c.InflateStatus == EaseUsChunkInflateStatus.Inflated).ToList();
    var c2Inflated = r2.Chunks.Where(c => c.InflateStatus == EaseUsChunkInflateStatus.Inflated).ToList();
    Assert.That(c1Inflated, Has.Count.EqualTo(2));
    Assert.That(c2Inflated, Has.Count.EqualTo(2));

    // First (header-bank) chunk offset must be stable across the two versions.
    Assert.That(c2Inflated[0].Offset, Is.EqualTo(c1Inflated[0].Offset),
      "Header-bank chunk offset must be stable across versions — matches Rune-Server's stable 0x98 observation.");
    // Second chunk shifts because we changed the path / earlier content lengths only if upstream changed.
    // In our builder the path is identical so the second offset is also stable; the only delta is the
    // compressed-length difference reflected in CompressedLength of the second chunk itself.
    Assert.That(c2Inflated[1].DecompressedLength,
      Is.EqualTo(c1Inflated[1].DecompressedLength + 3),
      "Payload chunk decompressed length must grow by exactly the payload-delta byte count.");
  }

  // ---------------------------------------------------------------------
  // False-positive rejection.
  // ---------------------------------------------------------------------

  [Test, Category("Sad")]
  public void Scanner_RejectsCoincidental_0x78_9C_BytePattern() {
    // Build an image where the only "zlib markers" are raw 0x78 0x9C
    // followed by 5 bytes of filler — what the original detection test
    // injects. None of these inflate.
    var body = new List<byte>();
    body.AddRange(Encoding.ASCII.GetBytes("IMGF"));
    Span<byte> tmp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, 0x0000052Cu);
    body.AddRange(tmp.ToArray());
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, 0x00020000u);
    body.AddRange(tmp.ToArray());

    for (var i = 0; i < 3; ++i) {
      body.Add(0x78);
      body.Add(0x9C);
      body.AddRange(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE });
    }
    for (var i = 0; i < 64; ++i) body.Add((byte)(i & 0x7F));

    using var ms = new MemoryStream(body.ToArray());
    var r = new EaseUsReader(ms);

    Assert.That(r.ZlibStreamCount, Is.GreaterThanOrEqualTo(3),
      "Scanner must still LIST the coincidental 0x78 0x9C hits as candidates.");
    Assert.That(r.ConfirmedZlibChunkCount, Is.EqualTo(0),
      "Trial inflate must reject all of them — none of the 5-byte fillers form a valid DEFLATE stream.");
    Assert.That(
      r.Chunks.All(c => c.InflateStatus != EaseUsChunkInflateStatus.Inflated),
      Is.True);
  }

  [Test, Category("Sad")]
  public void Scanner_ReportsFailureCode_ForInvalidHeader() {
    // Single 0x78 0x9C at the body start followed by garbage — should
    // produce a FailedHeaderInvalid / FailedCorrupt result, NOT an
    // unhandled exception.
    var body = new List<byte>();
    body.AddRange(Encoding.ASCII.GetBytes("IMGF"));
    body.AddRange(new byte[8]);
    body.Add(0x78);
    body.Add(0x9C);
    body.AddRange(new byte[32]);  // not a valid DEFLATE bitstream

    using var ms = new MemoryStream(body.ToArray());
    var r = new EaseUsReader(ms);
    Assert.That(r.Chunks, Is.Not.Empty);
    Assert.That(
      r.Chunks.All(c => c.InflateStatus is
        EaseUsChunkInflateStatus.FailedHeaderInvalid or
        EaseUsChunkInflateStatus.FailedCorrupt or
        EaseUsChunkInflateStatus.FailedTruncated),
      Is.True,
      "Bare 0x78 0x9C followed by random bytes must fail through a documented failure code.");
  }

  // ---------------------------------------------------------------------
  // Payload retention cap.
  // ---------------------------------------------------------------------

  [Test, Category("Boundary")]
  public void Scanner_InflatedOverCap_WhenPayloadExceedsRetentionLimit() {
    // Force a payload larger than the per-chunk retention cap so the
    // scanner returns InflatedOverCap (counted, but payload not retained).
    var huge = new byte[EaseUsReader.MaxRetainedChunkPayloadBytes + 1024];
    new Random(42).NextBytes(huge);
    var zlib = MakeZlib(huge);

    var body = new List<byte>();
    body.AddRange(Encoding.ASCII.GetBytes("IMGF"));
    body.AddRange(new byte[8]);
    body.AddRange(zlib);

    using var ms = new MemoryStream(body.ToArray());
    var r = new EaseUsReader(ms);
    var c = r.Chunks.Single(x => x.InflateStatus == EaseUsChunkInflateStatus.InflatedOverCap);
    Assert.That(c.DecompressedLength, Is.EqualTo(huge.Length),
      "Even when payload is dropped on the floor, the decompressed length must be counted.");
    Assert.That(c.PayloadRetained, Is.False);
    Assert.That(c.Payload, Is.Empty);
    Assert.That(c.CompressedLength, Is.EqualTo(zlib.Length));
  }

  // ---------------------------------------------------------------------
  // Forensic entry surfacing.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_SurfacesPerChunkEntries_WithOffsetAndLengthInName() {
    var payload = Encoding.ASCII.GetBytes("forensic chunk surface test payload");
    var img = BuildImageWithChunks(new[] { payload });
    using var ms = new MemoryStream(img);
    var r = new EaseUsReader(ms);

    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Has.Some.StartsWith("chunks/chunk_0000_off"));
    Assert.That(
      names.Any(n => n.StartsWith("chunks/chunk_0000_off") && n.EndsWith(".zlib")),
      Is.True,
      "Raw compressed-stream entry must be surfaced as chunks/chunk_NNNN_off..._clen....zlib.");
    Assert.That(
      names.Any(n => n.StartsWith("chunks/chunk_0000_off") && n.EndsWith(".bin")),
      Is.True,
      "Inflated payload entry must be surfaced as chunks/chunk_NNNN_off..._dlen....bin when retained.");

    // Inflated entry must round-trip to the original payload.
    var inflatedEntry = r.Entries.Single(e =>
      e.Name.StartsWith("chunks/chunk_0000_off") && e.Name.EndsWith(".bin"));
    Assert.That(inflatedEntry.Data, Is.EqualTo(payload));
  }

  [Test, Category("Boundary")]
  public void Reader_RespectsChunkEntryCap() {
    // Build many small real zlib substreams to make sure the surfacing
    // cap kicks in. We only need to confirm: (1) total chunk count
    // in metadata.ini matches the actual scanned candidates, (2) the
    // number of "chunks/chunk_*.zlib" entries does NOT exceed
    // MaxChunkEntriesSurfaced.
    var payloads = Enumerable.Range(0, EaseUsReader.MaxChunkEntriesSurfaced + 5)
      .Select(i => Encoding.ASCII.GetBytes($"payload-{i:D3}-stable-content-for-real-deflation"))
      .ToList();
    var img = BuildImageWithChunks(payloads);
    using var ms = new MemoryStream(img);
    var r = new EaseUsReader(ms);

    var chunkRawEntries = r.Entries.Count(e =>
      e.Name.StartsWith("chunks/chunk_") && e.Name.EndsWith(".zlib"));
    Assert.That(chunkRawEntries, Is.LessThanOrEqualTo(EaseUsReader.MaxChunkEntriesSurfaced),
      "Surfaced chunk-entry count must not exceed the cap.");
    Assert.That(r.ConfirmedZlibChunkCount, Is.EqualTo(payloads.Count),
      "All real zlib substreams must inflate end-to-end regardless of the surfacing cap.");
  }

  // ---------------------------------------------------------------------
  // metadata.ini chunk inventory rows.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsChunkStream_ParseStatusPromotion() {
    var payloads = new[] {
      Encoding.ASCII.GetBytes("metadata-bank-payload-for-chunk-status-promotion"),
      Encoding.ASCII.GetBytes("second-chunk-payload-also-real-zlib"),
    };
    var img = BuildImageWithChunks(payloads);
    using var ms = new MemoryStream(img);
    var r = new EaseUsReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("parse_status=chunk-stream"),
      "parse_status must promote to chunk-stream once at least one chunk inflates.");
    Assert.That(text, Does.Contain("stage=ro-chunk-stream"));
    Assert.That(text, Does.Contain("zlib_confirmed_chunk_count="));
    Assert.That(text, Does.Contain("zlib_total_compressed_bytes="));
    Assert.That(text, Does.Contain("zlib_total_decompressed_bytes="));
    Assert.That(text, Does.Contain("zlib_chunk_retention_cap_bytes="));
    Assert.That(text, Does.Contain("zlib_chunk_entries_surfaced_cap="));
    Assert.That(text, Does.Contain("zlib_chunk_00_offset="));
    Assert.That(text, Does.Contain("status=Inflated"));
    Assert.That(text, Does.Contain("treatment=R/O chunk-stream"));
  }

  [Test, Category("Boundary")]
  public void Metadata_KeepsHeaderMetadata_WhenNoChunkInflates() {
    // Image with NO 0x78 candidate bytes at all — the scanner has
    // nothing to attempt-inflate, so parse_status MUST stay
    // header-metadata (not regress to chunk-stream) and downstream
    // forensic consumers see the honest "no chunks confirmed" signal.
    var body = new List<byte>();
    body.AddRange(Encoding.ASCII.GetBytes("IMGF"));
    Span<byte> tmp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, 0x0000052Cu);
    body.AddRange(tmp.ToArray());
    BinaryPrimitives.WriteUInt32LittleEndian(tmp, 0x00020000u);
    body.AddRange(tmp.ToArray());
    // Filler that purposely avoids the 0x78 byte to keep the candidate
    // count at zero — the scanner can't even try to inflate without a
    // 0x78 header byte.
    for (var i = 0; i < 64; ++i) body.Add(0x33);

    using var ms = new MemoryStream(body.ToArray());
    var r = new EaseUsReader(ms);
    var meta = r.Entries.Single(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("parse_status=header-metadata"));
    Assert.That(text, Does.Contain("stage=ro-metadata"));
    Assert.That(text, Does.Contain("zlib_confirmed_chunk_count=0"));
  }

  // ---------------------------------------------------------------------
  // Description contract (chunk-stream promotion).
  // ---------------------------------------------------------------------

  [Test, Category("Stub")]
  public void Description_NamesChunkStreamPromotion_ButPinsSectorReconstructionAsStage0() {
    var d = new EaseUsFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("chunk-stream"),
      "Description must name the new chunk-stream treatment explicitly.");
    Assert.That(desc, Does.Contain("trial inflate"),
      "Description must mention the trial-inflate technique so downstream readers know what to expect.");
    Assert.That(desc, Does.Contain("sector reconstruction"),
      "Description must pin sector reconstruction as the still-blocked promotion.");
    Assert.That(
      desc.Contains("vendor") || desc.Contains("easeus engine") || desc.Contains("engine") || desc.Contains("aes"),
      Is.True,
      "Description must still cite at least one upgrade-blocker family.");
  }

  // ---------------------------------------------------------------------
  // Direct scanner API surface — pinning the public contract.
  // ---------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Scanner_TryInflate_HandlesValidStandaloneZlib() {
    var payload = Encoding.ASCII.GetBytes("standalone trial inflate API contract test");
    var zlib = MakeZlib(payload);
    var c = EaseUsZlibScanner.TryInflate(zlib, 0);
    Assert.That(c.InflateStatus, Is.EqualTo(EaseUsChunkInflateStatus.Inflated));
    Assert.That(c.Payload, Is.EqualTo(payload));
    Assert.That(c.CompressedLength, Is.EqualTo(zlib.Length));
  }

  [Test, Category("Sad")]
  public void Scanner_TryInflate_RejectsOffsetPastEnd() {
    var c = EaseUsZlibScanner.TryInflate(new byte[4], 100);
    Assert.That(c.InflateStatus, Is.EqualTo(EaseUsChunkInflateStatus.FailedHeaderInvalid));
  }

  [Test, Category("Sad")]
  public void Scanner_Scan_ThrowsOnNullData() {
    Assert.Throws<ArgumentNullException>(() => EaseUsZlibScanner.Scan(null!));
  }

  [Test, Category("Boundary")]
  public void Scanner_Scan_BoundedByMaxCandidates() {
    // Force many short zlib streams; pass a tight maxCandidates cap and
    // confirm the scanner stops once it hits the ceiling.
    var streams = Enumerable.Range(0, 20)
      .Select(i => MakeZlib(Encoding.ASCII.GetBytes($"short-{i}")))
      .ToList();
    var glued = new List<byte>();
    foreach (var s in streams) glued.AddRange(s);

    var chunks = EaseUsZlibScanner.Scan(
      glued.ToArray(), startOffset: 0, maxCandidates: 5);
    Assert.That(chunks, Has.Count.EqualTo(5));
  }
}
