using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

/// <summary>
/// End-to-end tests for the Listing↔RecordIndex↔Blob extraction path.
/// </summary>
/// <remarks>
/// <para>
/// The per-file FileMeta records (102/1/2/5) are surfaced as opaque diagnostic blobs by the
/// reader because their bodies remain undocumented in every public source. Extraction itself
/// pairs Listing entries with RecordIndex records by archive order (the
/// sequential-pairing assumption) and validates per-handle MD5s before writing.
/// </para>
/// <para>
/// These tests build fully synthetic .tib slices with a Listing + per-file FileMeta chain
/// (102/1/2/5) + RecordIndex(108) + Blob(109) sequence and verify that <c>Extract</c> reproduces
/// the original byte content.
/// </para>
/// </remarks>
[TestFixture]
public class AcronisExtractionTests {

  // ----- Test builders -----

  private sealed record TestFile(string Path, string Name, byte[] Content);

  /// <summary>
  /// Builds a .tib slice with one Listing record + per-file (102 + 1 + 2 + 5 + 108 + 109)
  /// chain, surrounded by the standard volume header, file-system trailer, and mirror footer.
  /// </summary>
  /// <remarks>
  /// File data is stored as a single Blob per file (one handle per RecordIndex) for simplicity.
  /// Larger fragmented files are exercised by the dedicated <c>FragmentedFile</c> test.
  /// </remarks>
  private static byte[] BuildTibWithExtractableFiles(IReadOnlyList<TestFile> testFiles, bool fragmentLargeFiles = false) {
    using var ms = new MemoryStream();

    // 1) Volume header — Windows format.
    Span<byte> hdr = stackalloc byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], 0x20);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], 0x11111111);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], 0x22222222);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], 0x33333333);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);

    var metaStart = (long)ms.Position;
    var headerLength = 0x20;

    // 2) Listing record (type 103) — opens the metadata stream.
    var listingPayload = BuildListingPayload(testFiles);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listingPayload);

    // 3) For each file: 102 + 1 + 2 + 5 (opaque) + 108 (RecordIndex) + 109 (Blob).
    foreach (var f in testFiles) {
      // Opaque per-file metadata chain (real bodies are unknown — we write tiny markers).
      WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord, Encoding.ASCII.GetBytes($"meta102:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes($"meta1:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes($"meta2:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes($"meta5:{f.Name}"));

      // Decide blob layout: 1 blob (default) or split-into-halves for the fragmented test.
      var fragments = fragmentLargeFiles && f.Content.Length >= 4
        ? SplitInHalves(f.Content)
        : [(StartOffset: 0L, Data: f.Content)];

      // Pre-compute the RecordIndex payload — handles need each Blob's recordOffset, which is
      // (Blob.absolutePosition - headerLength). We don't yet know the Blob positions, so we
      // emit the RecordIndex BEFORE the blobs and patch the recordOffsets after.
      // Simpler approach: emit blobs first, then RecordIndex.
      var blobInfo = new List<(long startOffset, long recordOffset, byte[] md5)>(fragments.Length);
      foreach (var frag in fragments) {
        var blobAbsPos = ms.Position;
        WriteZlibRecord(ms, AcronisRecordType.Blob, frag.Data);
        blobInfo.Add((frag.StartOffset, blobAbsPos - headerLength, MD5.HashData(frag.Data)));
      }

      // RecordIndex — Acronis payload format per dennisss RE.
      var idxPayload = BuildRecordIndexPayload(f.Content.LongLength, blobInfo);
      WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idxPayload);
    }

    // 4) EndTrailer
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    // 5) File-system trailer: uint64 LE metaOffset + 4-byte fs-magic.
    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    // 6) 48-byte mirror footer.
    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);

    return ms.ToArray();
  }

  private static (long StartOffset, byte[] Data)[] SplitInHalves(byte[] data) {
    var half = data.Length / 2;
    var a = data[..half];
    var b = data[half..];
    return [(0L, a), (half, b)];
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
    // 2-byte zlib header. CMF = 0x78 (deflate, 32k window). FLG chosen so (CMF<<8 | FLG) % 31 == 0.
    // 0x78 9C is the canonical default-compression header (FCHECK works out).
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    // 4-byte big-endian Adler-32 trailer (reader doesn't validate it, but we write a real one).
    var adler = ComputeAdler32(payload);
    Span<byte> trailer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
    ms.Write(trailer);
  }

  private static uint ComputeAdler32(byte[] data) {
    const uint MOD = 65521;
    uint a = 1, b = 0;
    foreach (var x in data) {
      a = (a + x) % MOD;
      b = (b + a) % MOD;
    }
    return (b << 16) | a;
  }

  private static byte[] BuildListingPayload(IReadOnlyList<TestFile> files) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)files.Count);
    foreach (var f in files) {
      WriteCountedUtf16(w, f.Path);
      w.Write(0u);
      WriteCountedUtf16(w, f.Name);
      WriteCountedUtf16(w, "");
      WriteUInt48(w, 0); w.Write((ushort)0);
      w.Write(0u);
      WriteUInt48(w, (ulong)f.Content.LongLength); w.Write((ushort)0);
      WriteUInt48(w, (ulong)f.Content.LongLength); w.Write((ushort)0);
      WriteUInt48(w, 0); w.Write((ushort)0);
      w.Write(new byte[38]);
    }
    w.Flush();
    return ms.ToArray();
  }

  private static byte[] BuildRecordIndexPayload(long totalSize, IReadOnlyList<(long startOffset, long recordOffset, byte[] md5)> handles) {
    using var ms = new MemoryStream();
    // 8-byte magic
    ms.Write([0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00]);
    // uint48 totalSize + 2 padding
    WriteUInt48Bytes(ms, (ulong)totalSize); ms.WriteByte(0); ms.WriteByte(0);
    // uint32 numHandles
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)handles.Count);
    ms.Write(u32);
    // handles
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

  // ----- RecordIndex payload parser -----

  [Test, Category("HappyPath")]
  public void ParseRecordIndex_RoundTrip() {
    var md5 = new byte[16];
    for (var i = 0; i < 16; i++) md5[i] = (byte)i;
    var payload = BuildRecordIndexPayload(
      totalSize: 1024,
      handles: [(0L, 0x100L, md5), (512L, 0x200L, md5)]
    );
    var info = AcronisRecordReader.ParseRecordIndex(payload);
    Assert.That(info, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(info!.TotalSize, Is.EqualTo(1024));
      Assert.That(info.Handles, Has.Count.EqualTo(2));
      Assert.That(info.Handles[0].StartOffset, Is.EqualTo(0));
      Assert.That(info.Handles[0].RecordOffset, Is.EqualTo(0x100));
      Assert.That(info.Handles[1].StartOffset, Is.EqualTo(512));
      Assert.That(info.Handles[1].RecordOffset, Is.EqualTo(0x200));
    });
  }

  [Test, Category("EdgeCase")]
  public void ParseRecordIndex_TooShort_ReturnsNull() {
    Assert.That(AcronisRecordReader.ParseRecordIndex(new byte[8]), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void ParseRecordIndex_TruncatedHandles_ReturnsWhatItHas() {
    // Build a valid header that claims 2 handles, but only deliver 1.
    var md5 = new byte[16];
    var full = BuildRecordIndexPayload(100, [(0L, 0x100L, md5), (50L, 0x200L, md5)]);
    var truncated = full[..(8 + 8 + 4 + 32)]; // header + 1 handle (8 + 8 + 16 = 32)
    var info = AcronisRecordReader.ParseRecordIndex(truncated);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Handles, Has.Count.EqualTo(1));
  }

  // ----- Reader exposure of metadata records -----

  [Test, Category("HappyPath")]
  public void Reader_SurfacesFileMetaAndRecordIndex() {
    var content = Encoding.ASCII.GetBytes("hello acronis");
    var tib = BuildTibWithExtractableFiles([new("d/", "hello.txt", content)]);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.Entries, Has.Count.EqualTo(1));
      Assert.That(r.RecordIndices, Has.Count.EqualTo(1));
      Assert.That(r.FileMetaRecords, Has.Count.EqualTo(4), "expected 102+1+2+5 = 4 opaque meta records");
      Assert.That(r.RecordIndices[0].Index, Is.Not.Null);
      Assert.That(r.RecordIndices[0].Index!.TotalSize, Is.EqualTo(content.Length));
      Assert.That(r.CanExtractByPairing(out _), Is.True);
    });
  }

  // ----- End-to-end extraction -----

  [Test, Category("HappyPath")]
  public void ExtractFile_SingleSmallFile_RoundTrips() {
    var content = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog");
    var tib = BuildTibWithExtractableFiles([new("d/", "fox.txt", content)]);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);
    var result = r.ExtractFile(0);
    Assert.Multiple(() => {
      Assert.That(result.IntegrityValid, Is.True);
      Assert.That(result.Data, Is.EqualTo(content));
    });
  }

  [Test, Category("HappyPath")]
  public void ExtractFile_MultipleFiles_AllRoundTrip() {
    var files = new TestFile[] {
      new("dir1/", "a.txt", Encoding.UTF8.GetBytes("first file content")),
      new("dir1/sub/", "b.bin", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]),
      new("", "root.dat", Encoding.UTF8.GetBytes("root level file with longer content here that exceeds 50 bytes for variety")),
    };
    var tib = BuildTibWithExtractableFiles(files);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.Multiple(() => {
      for (var i = 0; i < files.Length; i++) {
        var result = r.ExtractFile(i);
        Assert.That(result.IntegrityValid, Is.True, $"file[{i}] md5 mismatch");
        Assert.That(result.Data, Is.EqualTo(files[i].Content), $"file[{i}] content mismatch");
      }
    });
  }

  [Test, Category("HappyPath")]
  public void ExtractFile_FragmentedFile_ConcatenatesByStartOffset() {
    // 256-byte file split into two 128-byte Blobs — exercises StartOffset-based reassembly.
    var content = new byte[256];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 3 + 7);
    var tib = BuildTibWithExtractableFiles([new("", "frag.bin", content)], fragmentLargeFiles: true);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);
    var result = r.ExtractFile(0);
    Assert.Multiple(() => {
      Assert.That(result.IntegrityValid, Is.True);
      Assert.That(result.Data, Is.EqualTo(content));
      Assert.That(r.RecordIndices[0].Index!.Handles, Has.Count.EqualTo(2));
    });
  }

  [Test, Category("EdgeCase")]
  public void ExtractFile_SingleByteFile_RoundTrips() {
    // Boundary: smallest non-empty file. Empty files are a different code path — they would
    // legitimately have zero handles in the RecordIndex, which is out of scope for the current
    // sequential-pairing gate (it requires every entry to be paired with a non-empty index).
    var tib = BuildTibWithExtractableFiles([new("", "byte.bin", [0x42])]);
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);
    var result = r.ExtractFile(0);
    Assert.Multiple(() => {
      Assert.That(result.Data, Is.EqualTo(new byte[] { 0x42 }));
      Assert.That(result.IntegrityValid, Is.True);
    });
  }

  // ----- Descriptor-level Extract end-to-end -----

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesFilesToDisk() {
    var content1 = Encoding.UTF8.GetBytes("first file");
    var content2 = Encoding.UTF8.GetBytes("second file with more bytes");
    var tib = BuildTibWithExtractableFiles([
      new("C:\\dir\\", "a.txt", content1),
      new("C:\\dir\\sub\\", "b.txt", content2),
    ]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var tempDir = Path.Combine(Path.GetTempPath(), "acronis_extract_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      desc.Extract(ms, tempDir, null, null);

      // Path sanitization strips the "C:" prefix, so files land at "dir/a.txt" + "dir/sub/b.txt".
      var pathA = Path.Combine(tempDir, "dir", "a.txt");
      var pathB = Path.Combine(tempDir, "dir", "sub", "b.txt");
      Assert.Multiple(() => {
        Assert.That(File.Exists(pathA), Is.True, "a.txt should exist");
        Assert.That(File.Exists(pathB), Is.True, "b.txt should exist");
        Assert.That(File.ReadAllBytes(pathA), Is.EqualTo(content1));
        Assert.That(File.ReadAllBytes(pathB), Is.EqualTo(content2));
      });
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_HonoursFileFilter() {
    var content1 = Encoding.UTF8.GetBytes("alpha");
    var content2 = Encoding.UTF8.GetBytes("beta");
    var tib = BuildTibWithExtractableFiles([
      new("d/", "alpha.txt", content1),
      new("d/", "beta.txt", content2),
    ]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var tempDir = Path.Combine(Path.GetTempPath(), "acronis_filter_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      desc.Extract(ms, tempDir, null, ["beta.txt"]);
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(tempDir, "d", "alpha.txt")), Is.False);
        Assert.That(File.Exists(Path.Combine(tempDir, "d", "beta.txt")), Is.True);
      });
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  // ----- Honest-fallback gate: pairing mismatch refuses extraction -----

  [Test, Category("ErrorHandling")]
  public void Descriptor_Extract_ThrowsWhenIndexCountMissing() {
    // Use the original AcronisListingTests builder which emits ONLY a Listing — no RecordIndex.
    var tib = BuildListingOnly([("d/", "x.txt", 5L)]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var tempDir = Path.Combine(Path.GetTempPath(), "acronis_norec_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      var ex = Assert.Throws<NotSupportedException>(() => desc.Extract(ms, tempDir, null, null));
      Assert.That(ex!.Message, Does.Contain("pairing").Or.Contain("RecordIndex"));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void ExtractFile_SizeMismatch_RejectsBeforeWritingData() {
    // Construct a slice where Listing.FileSize says 10 but RecordIndex.TotalSize says 5.
    var tib = BuildTibWithSizeMismatch();
    using var ms = new MemoryStream(tib);
    var r = new AcronisReader(ms);
    Assert.That(r.CanExtractByPairing(out var reason), Is.False);
    Assert.That(reason, Does.Contain("size").Or.Contain("Size"));
    Assert.Throws<InvalidOperationException>(() => r.ExtractFile(0));
  }

  // ----- Descriptor capability flip -----

  [Test, Category("HappyPath")]
  public void Descriptor_NowAdvertisesCanExtract() {
    var desc = new AcronisFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
  }

  // ----- Helpers for negative tests -----

  private static byte[] BuildListingOnly(IReadOnlyList<(string Path, string Name, long FileSize)> entries) {
    using var ms = new MemoryStream();
    Span<byte> hdr = stackalloc byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], 0x20);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);
    var metaOffset = (long)ms.Position;

    var payload = BuildListingPayload(entries.Select(e => new TestFile(e.Path, e.Name, new byte[e.FileSize])).ToList());
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, payload);

    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaOffset);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);
    return ms.ToArray();
  }

  private static byte[] BuildTibWithSizeMismatch() {
    // Listing says size = 10. RecordIndex says total = 5. CanExtractByPairing must reject.
    using var ms = new MemoryStream();
    Span<byte> hdr = stackalloc byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], 0x20);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);
    var metaOffset = (long)ms.Position;

    var listing = BuildListingPayload([new("", "mismatch.bin", new byte[10])]);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listing);

    // Blob with only 5 bytes
    var blobAbs = ms.Position;
    var blobData = new byte[] { 1, 2, 3, 4, 5 };
    WriteZlibRecord(ms, AcronisRecordType.Blob, blobData);

    // RecordIndex claiming totalSize = 5 (NOT 10).
    var idx = BuildRecordIndexPayload(5, [(0L, blobAbs - 0x20, MD5.HashData(blobData))]);
    WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idx);

    ms.WriteByte((byte)AcronisRecordType.EndTrailer);

    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaOffset);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);

    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);
    return ms.ToArray();
  }
}
