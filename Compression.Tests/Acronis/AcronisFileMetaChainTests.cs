using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

/// <summary>
/// End-to-end tests for the FileMeta chain walk extraction path.
/// </summary>
/// <remarks>
/// <para>
/// The chain walk is the spec-grounded alternative to the legacy sequential-pairing heuristic:
/// per upstream RE (https://github.com/dennisss/acronis-tib, src/win/record.ts ListingRecord
/// comment "Offset relative to after the header of the FirstFileMetaRecord for this entry"),
/// each Listing entry carries a 48-bit <c>MetaOffset</c> field that points (relative to the
/// volume header length) at its FirstFileMetaRecord(102) anchor. From that anchor the
/// per-file chain runs 102 → FileMetaA(1) → FileMetaB(2) → FileMetaC(5) → RecordIndex(108) →
/// Blob(109)+, so the first 108 record appearing after the anchored 102 is the authoritative
/// RecordIndex for that entry.
/// </para>
/// <para>
/// These tests build synthetic .tib slices with properly-populated MetaOffset fields and exercise:
/// (1) chain walk resolves every entry on a well-formed archive, (2) chain walk agrees with
/// sequential pairing on archive-order slices, (3) chain walk produces correct results on
/// slices where Listing entries and RecordIndices are emitted in DIFFERENT orders (so the
/// sequential-pairing heuristic would give wrong content), (4) the resolved RecordIndex feeds
/// the same MD5-gated extraction path as the legacy sequential pairing.
/// </para>
/// </remarks>
[TestFixture]
public class AcronisFileMetaChainTests {

  private sealed record TestFile(string Path, string Name, byte[] Content);

  // ----- Builders -----

  /// <summary>
  /// Builds a .tib slice with one Listing record + per-file (102 + 1 + 2 + 5 + 108 + 109) chain,
  /// matching the real Acronis on-disk archive order. Listing entries may be emitted in a
  /// different order from chain order via <paramref name="listingOrder"/> — that's how we
  /// exercise the chain walk's superiority over the legacy sequential-pairing heuristic.
  /// </summary>
  /// <param name="testFiles">Files in chain (= archive) order. Each gets a 102/1/2/5/108/109 chain.</param>
  /// <param name="listingOrder">
  /// Function mapping Listing-entry index → chain index. <c>i => i</c> (the default) emits the
  /// Listing in chain order — sequential pairing then matches the chain walk. <c>i => N - 1 - i</c>
  /// emits the Listing in reverse — sequential pairing would silently pair Listing[0] with the
  /// first 108 (which belongs to the chain-0 file = chain index 0 ≠ Listing[0]'s actual file),
  /// while the chain walk correctly resolves Listing[0].MetaOffset back to its real chain.
  /// </param>
  private static byte[] BuildTibWithMetaOffsetChain(
      IReadOnlyList<TestFile> testFiles,
      Func<int, int>? listingOrder = null) {
    using var ms = new MemoryStream();
    const int HeaderLength = 0x20;

    // Volume header.
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], 0x11111111);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], 0x22222222);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], 0x33333333);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);

    var metaStart = (long)ms.Position;

    // Phase 1: emit the per-file chains in archive order. Each chain: 102, 1, 2, 5, 108, 109.
    // Capture every 102's relative offset for the Listing emit in phase 2.
    var ffmOffsets = new long[testFiles.Count];
    for (var i = 0; i < testFiles.Count; i++) {
      var f = testFiles[i];
      ffmOffsets[i] = ms.Position - HeaderLength;
      WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord, Encoding.ASCII.GetBytes($"meta102:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes($"meta1:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes($"meta2:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes($"meta5:{f.Name}"));
      var blobAbs = ms.Position;
      WriteZlibRecord(ms, AcronisRecordType.Blob, f.Content);
      var idxPayload = BuildRecordIndexPayload(f.Content.LongLength,
        [(0L, blobAbs - HeaderLength, MD5.HashData(f.Content))]);
      WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idxPayload);
    }

    // Phase 2: emit the Listing, possibly in a different order than chain order.
    var resolveListing = listingOrder ?? (i => i);
    var orderedFiles = new TestFile[testFiles.Count];
    var orderedOffsets = new long[testFiles.Count];
    for (var i = 0; i < testFiles.Count; i++) {
      var chainIdx = resolveListing(i);
      orderedFiles[i] = testFiles[chainIdx];
      orderedOffsets[i] = ffmOffsets[chainIdx];
    }
    var listingPayload = BuildListingPayload(orderedFiles, orderedOffsets);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listingPayload);

    // EndTrailer.
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    // File-system trailer + footer.
    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);

    return ms.ToArray();
  }

  private static void WriteRawDeflateRecord(MemoryStream ms, AcronisRecordType type, byte[] payload) {
    ms.WriteByte((byte)type);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    Span<byte> sum = stackalloc byte[4];
    ms.Write(sum);
  }

  private static void WriteZlibRecord(MemoryStream ms, AcronisRecordType type, byte[] payload) {
    ms.WriteByte((byte)type);
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    var adler = ComputeAdler32(payload);
    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
    ms.Write(trailer);
  }

  private static uint ComputeAdler32(byte[] data) {
    const uint MOD = 65521;
    uint a = 1, b = 0;
    foreach (var x in data) { a = (a + x) % MOD; b = (b + a) % MOD; }
    return (b << 16) | a;
  }

  private static byte[] BuildListingPayload(IReadOnlyList<TestFile> files, IReadOnlyList<long> metaOffsets) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)files.Count);
    for (var i = 0; i < files.Count; i++) {
      var f = files[i];
      WriteCountedUtf16(w, f.Path);
      w.Write(0u);
      WriteCountedUtf16(w, f.Name);
      WriteCountedUtf16(w, "");
      WriteUInt48(w, 0); w.Write((ushort)0);
      w.Write(0u);
      WriteUInt48(w, (ulong)f.Content.LongLength); w.Write((ushort)0);
      WriteUInt48(w, (ulong)f.Content.LongLength); w.Write((ushort)0);
      WriteUInt48(w, (ulong)metaOffsets[i]); w.Write((ushort)0);
      w.Write(new byte[38]);
    }
    w.Flush();
    return ms.ToArray();
  }

  private static byte[] BuildRecordIndexPayload(long totalSize, IReadOnlyList<(long startOffset, long recordOffset, byte[] md5)> handles) {
    using var ms = new MemoryStream();
    ms.Write([0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00]);
    WriteUInt48Bytes(ms, (ulong)totalSize); ms.WriteByte(0); ms.WriteByte(0);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)handles.Count);
    ms.Write(u32);
    foreach (var h in handles) {
      WriteUInt48Bytes(ms, (ulong)h.startOffset); ms.WriteByte(0); ms.WriteByte(0);
      WriteUInt48Bytes(ms, (ulong)h.recordOffset); ms.WriteByte(0); ms.WriteByte(0);
      ms.Write(h.md5);
    }
    return ms.ToArray();
  }

  private static void WriteCountedUtf16(BinaryWriter w, string s) {
    w.Write((uint)s.Length);
    if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
  }

  private static void WriteUInt48(BinaryWriter w, ulong v) {
    for (var i = 0; i < 6; i++) w.Write((byte)((v >> (i * 8)) & 0xFF));
  }

  private static void WriteUInt48Bytes(MemoryStream s, ulong v) {
    for (var i = 0; i < 6; i++) s.WriteByte((byte)((v >> (i * 8)) & 0xFF));
  }

  // ===== Chain-walk resolution =====

  [Test, Category("HappyPath")]
  public void ChainWalk_ResolvesEveryEntry_OnArchiveOrderSlice() {
    var files = new TestFile[] {
      new("d1/", "alpha.txt", Encoding.UTF8.GetBytes("alpha content")),
      new("d2/", "beta.txt", Encoding.UTF8.GetBytes("beta content here, slightly longer")),
      new("d3/", "gamma.bin", [0x01, 0x02, 0x03, 0x04, 0x05]),
    };
    var tib = BuildTibWithMetaOffsetChain(files);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.Entries, Has.Count.EqualTo(3));
      Assert.That(r.RecordIndices, Has.Count.EqualTo(3));
      Assert.That(r.RecordIndicesByChainWalk, Has.Count.EqualTo(3));
      Assert.That(r.ChainWalkComplete, Is.True, "chain walk must resolve every entry on a well-formed slice");
      Assert.That(r.ChainWalkMatchesSequentialPairing, Is.True, "chain walk and sequential pairing must agree in archive order");
      for (var i = 0; i < 3; i++)
        Assert.That(r.RecordIndicesByChainWalk[i], Is.Not.Null, $"entry[{i}] must resolve via chain walk");
    });
  }

  [Test, Category("HappyPath")]
  public void ChainWalk_PreferredOverSequentialPairing_OnArchiveOrderSlice() {
    var content = Encoding.UTF8.GetBytes("payload for chain-walk extraction test");
    var tib = BuildTibWithMetaOffsetChain([new("d/", "x.txt", content)]);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    // ChainWalkComplete = true means ExtractFile uses the chain walk path.
    Assert.That(r.ChainWalkComplete, Is.True);
    var result = r.ExtractFile(0);
    Assert.Multiple(() => {
      Assert.That(result.IntegrityValid, Is.True);
      Assert.That(result.Data, Is.EqualTo(content));
    });
  }

  // ===== Chain walk vs. sequential pairing disagreement =====

  [Test, Category("HappyPath")]
  public void ChainWalk_BeatsSequentialPairing_WhenListingEmittedInReverseChainOrder() {
    // Three files with DIFFERENT sizes. Chains are emitted in archive order [c0, c1, c2],
    // but the Listing entries are emitted in REVERSE order [c2, c1, c0]. Sequential pairing
    // would then incorrectly attempt Listing[0] ↔ index[0], but Listing[0] = file c2 (56
    // bytes) while index[0].TotalSize = file c0 (5 bytes) — the size cross-check fails.
    // The chain walk anchors Listing[0].MetaOffset → 102_c2 → next 108 = 108_c2 = RIGHT index.
    var c0 = Encoding.UTF8.GetBytes("first");
    var c1 = Encoding.UTF8.GetBytes("second is longer");
    var c2 = Encoding.UTF8.GetBytes("third is the longest of all three to make sizes distinct");
    var chains = new TestFile[] {
      new("d/", "c0.txt", c0),
      new("d/", "c1.txt", c1),
      new("d/", "c2.txt", c2),
    };
    // Listing order: Listing[i] = chains[N-1-i] (reverse).
    var tib = BuildTibWithMetaOffsetChain(chains, listingOrder: i => chains.Length - 1 - i);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ChainWalkComplete, Is.True, "chain walk must still resolve every entry");
      Assert.That(r.ChainWalkMatchesSequentialPairing, Is.False,
        "chain walk must report disagreement with sequential pairing on reversed-Listing slices");
      Assert.That(r.CanExtractByPairing(out _), Is.False,
        "sequential pairing must reject this slice (size cross-check fails: Listing[0]=c2, indices[0]=for c0)");

      // ExtractFile takes the chain-walk path and gets the right content.
      // Listing[0] = c2, Listing[1] = c1, Listing[2] = c0.
      var got0 = r.ExtractFile(0);
      var got1 = r.ExtractFile(1);
      var got2 = r.ExtractFile(2);
      Assert.That(got0.Data, Is.EqualTo(c2));
      Assert.That(got1.Data, Is.EqualTo(c1));
      Assert.That(got2.Data, Is.EqualTo(c0));
      Assert.That(got0.IntegrityValid, Is.True);
      Assert.That(got1.IntegrityValid, Is.True);
      Assert.That(got2.IntegrityValid, Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void ChainWalk_AssignsEachIndexExactlyOnce_OnSameSizeFiles() {
    // Same-size files would let sequential pairing silently emit the wrong content (no size
    // cross-check to catch it). The chain walk must assign each RecordIndex to exactly the entry
    // whose MetaOffset anchors its 102, even when listing order ≠ chain order.
    var c0 = new byte[16]; for (var i = 0; i < 16; i++) c0[i] = (byte)(0xA0 | i);
    var c1 = new byte[16]; for (var i = 0; i < 16; i++) c1[i] = (byte)(0xB0 | i);
    var chains = new TestFile[] {
      new("", "c0.bin", c0),
      new("", "c1.bin", c1),
    };
    // Swap Listing order: Listing[0] points at chain 1; Listing[1] points at chain 0.
    var tib = BuildTibWithMetaOffsetChain(chains, listingOrder: i => 1 - i);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ChainWalkComplete, Is.True);
      Assert.That(r.ChainWalkMatchesSequentialPairing, Is.False);
      // Both indices have TotalSize = 16, so CanExtractByPairing passes on size — sequential
      // pairing would silently produce chain-0 bytes for Listing[0], but Listing[0] is the
      // entry for chain 1. Chain walk avoids the silent corruption.
      Assert.That(r.CanExtractByPairing(out _), Is.True,
        "size cross-check passes when all files are same-size — that's exactly why chain walk matters");
      var got0 = r.ExtractFile(0); // Listing[0] = chain 1 (=c1)
      var got1 = r.ExtractFile(1); // Listing[1] = chain 0 (=c0)
      Assert.That(got0.Data, Is.EqualTo(c1), "Listing[0] must reconstruct c1 via chain walk, not the swap-paired c0");
      Assert.That(got1.Data, Is.EqualTo(c0), "Listing[1] must reconstruct c0 via chain walk, not the swap-paired c1");
    });
  }

  // ===== Negative cases =====

  [Test, Category("EdgeCase")]
  public void ChainWalk_IncompleteWhenMetaOffsetIsZero() {
    // The legacy AcronisExtractionTests builder writes MetaOffset=0 for every entry. Chain walk
    // must report Incomplete for that case (which lets ExtractFile fall back to sequential pairing).
    var content = Encoding.UTF8.GetBytes("hi");
    var tib = BuildTibWithZeroMetaOffset([new("d/", "x.txt", content)]);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ChainWalkComplete, Is.False, "MetaOffset=0 must NOT count as resolved");
      Assert.That(r.RecordIndicesByChainWalk[0], Is.Null);
      // Sequential pairing still works for this slice.
      Assert.That(r.CanExtractByPairing(out _), Is.True);
      var result = r.ExtractFile(0);
      Assert.That(result.Data, Is.EqualTo(content));
    });
  }

  [Test, Category("ErrorHandling")]
  public void ChainWalk_DoesNotResolve_WhenMetaOffsetPointsNowhere() {
    // MetaOffset points at a position that doesn't host a FirstFileMetaRecord(102). The chain
    // walk must report unresolved and fall back to sequential pairing.
    var content = Encoding.UTF8.GetBytes("payload");
    var tib = BuildTibWithBogusMetaOffset([new("", "x.txt", content)], bogusOffset: 0x7FFFFFFF);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);

    Assert.Multiple(() => {
      Assert.That(r.ChainWalkComplete, Is.False);
      Assert.That(r.RecordIndicesByChainWalk[0], Is.Null);
      // Sequential pairing is still happy — Listing.FileSize matches RecordIndex.TotalSize.
      Assert.That(r.CanExtractByPairing(out _), Is.True);
      var result = r.ExtractFile(0);
      Assert.That(result.Data, Is.EqualTo(content));
    });
  }

  // ----- Helpers for negative tests -----

  private static byte[] BuildTibWithZeroMetaOffset(IReadOnlyList<TestFile> testFiles) {
    // Same as BuildTibWithMetaOffsetChain but writes metaOffset=0 for every Listing entry.
    var metaOffsets = new long[testFiles.Count];
    for (var i = 0; i < testFiles.Count; i++) metaOffsets[i] = 0;
    return BuildWithCustomOffsets(testFiles, metaOffsets);
  }

  private static byte[] BuildTibWithBogusMetaOffset(IReadOnlyList<TestFile> testFiles, long bogusOffset) {
    var metaOffsets = new long[testFiles.Count];
    for (var i = 0; i < testFiles.Count; i++) metaOffsets[i] = bogusOffset;
    return BuildWithCustomOffsets(testFiles, metaOffsets);
  }

  private static byte[] BuildWithCustomOffsets(IReadOnlyList<TestFile> testFiles, IReadOnlyList<long> metaOffsetsToWrite) {
    using var ms = new MemoryStream();
    const int HeaderLength = 0x20;
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);
    var metaStart = (long)ms.Position;

    // Listing first (with the supplied metaOffsets — possibly bogus).
    var listingPayload = BuildListingPayload(testFiles, metaOffsetsToWrite);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listingPayload);

    // Per-file chain.
    foreach (var f in testFiles) {
      WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord, Encoding.ASCII.GetBytes($"meta102:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes($"meta1:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes($"meta2:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes($"meta5:{f.Name}"));
      var blobAbs = ms.Position;
      WriteZlibRecord(ms, AcronisRecordType.Blob, f.Content);
      var idxPayload = BuildRecordIndexPayload(f.Content.LongLength,
        [(0L, blobAbs - HeaderLength, MD5.HashData(f.Content))]);
      WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idxPayload);
    }

    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);
    return ms.ToArray();
  }
}
