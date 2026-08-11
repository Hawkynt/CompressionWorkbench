using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.NortonGhost;

namespace Compression.Tests.NortonGhost;

/// <summary>
/// Stage-1 R/O acceptance gate for <see cref="NortonGhostFormatDescriptor"/>.
/// The tests below build synthetic Ghost <c>.gho</c> images at the byte
/// level (the only path that doesn't require a Symantec corpus) and then
/// exercise the descriptor's List/Extract surface plus the lower-level
/// reader/decompressor primitives. Each test pins one of:
///   - the descriptor metadata contract (id, magic, capabilities),
///   - the FE EF + 0x012F18D8 record framing,
///   - Z0 uncompressed block round-trip,
///   - Z1 Fast LZ literal-only round-trip (matches not covered — the
///     encoder side is deferred),
///   - Z1 raw-marker (0x01-first-byte) round-trip,
///   - Z3 zlib (DEFLATE) round-trip,
///   - MBR partition table parsing on Track 0,
///   - metadata.ini surface (description bytes 255..335, references).
/// </summary>
[TestFixture]
public class NortonGhostReaderTests {

  // ----------------------------------------------------------------------
  // Builders — produce synthetic FE EF images for the test surface.
  // ----------------------------------------------------------------------

  /// <summary>Builds a 512-byte FE EF file header.</summary>
  private static byte[] BuildFileHeader(NortonGhostReader.FileType type, byte compression, uint imageId, string description) {
    var hdr = new byte[NortonGhostReader.HeaderSize];
    hdr[0] = 0xFE;
    hdr[1] = 0xEF;
    hdr[2] = (byte)type;
    hdr[3] = compression;
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), imageId);
    var descBytes = Encoding.ASCII.GetBytes(description);
    Array.Copy(descBytes, 0, hdr, 255, Math.Min(descBytes.Length, 80));
    return hdr;
  }

  /// <summary>Builds a 512-byte FE EF partition header carrying a compression code.</summary>
  private static byte[] BuildPartitionHeader(byte compression, uint id) {
    var hdr = new byte[NortonGhostReader.HeaderSize];
    hdr[0] = 0xFE;
    hdr[1] = 0xEF;
    hdr[3] = compression;
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), id);
    return hdr;
  }

  /// <summary>Builds a 10-byte record header with the 0x012F18D8 magic.</summary>
  private static byte[] BuildRecordHeader(ushort type, ushort bodyLen) {
    var rec = new byte[NortonGhostReader.RecordHeaderSize];
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0, 2), type);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4, 4), NortonGhostReader.RecordMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8, 2), bodyLen);
    return rec;
  }

  /// <summary>Builds a single block with the <c>0x01</c> uncompressed marker: <c>[2B len][01][3B reserved][payload]</c>.</summary>
  private static byte[] BuildUncompressedBlock(ReadOnlySpan<byte> payload) {
    var storedLen = 2 + 4 + payload.Length; // 2 = length prefix; 4 = block-data preamble.
    var block = new byte[storedLen];
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), (ushort)storedLen);
    block[2] = 0x01; // uncompressed marker
    payload.CopyTo(block.AsSpan(6));
    return block;
  }

  /// <summary>
  /// Builds a Fast-LZ block that encodes <paramref name="literals"/> as a
  /// pure-literal control-word run (no match references). The decoder uses
  /// the same hash-table state machine but only the literal path is
  /// exercised, so this is safe to build from a synthetic encoder.
  /// </summary>
  private static byte[] BuildFastLzLiteralBlock(ReadOnlySpan<byte> literals) {
    // Layout: [4B block-data preamble][2B control = 0x0000][16 literal bytes] repeating.
    // Each full control word covers 16 literal bytes; if we have N literals,
    // we need ceil(N/16) control words, each followed by up to 16 literal bytes.
    var bodySize = 4; // preamble
    var n = literals.Length;
    var fullWords = n / 16;
    var remainder = n % 16;
    bodySize += fullWords * (2 + 16);
    if (remainder > 0) bodySize += 2 + remainder;

    var block = new byte[2 + bodySize];
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), (ushort)block.Length);
    // First byte != 0x01 → Fast LZ; remaining 3 preamble bytes are ignored.
    block[2] = 0x02;
    block[3] = 0x00;
    block[4] = 0x00;
    block[5] = 0x00;

    var pos = 6;
    var litPos = 0;
    for (var w = 0; w < fullWords; w++) {
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(pos, 2), 0x0000); // 16 zero bits = 16 literals
      pos += 2;
      literals.Slice(litPos, 16).CopyTo(block.AsSpan(pos, 16));
      pos += 16;
      litPos += 16;
    }
    if (remainder > 0) {
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(pos, 2), 0x0000);
      pos += 2;
      literals.Slice(litPos, remainder).CopyTo(block.AsSpan(pos, remainder));
    }
    return block;
  }

  /// <summary>Builds a Z3 zlib block: <c>[2B len][marker non-01][zlib stream]</c>.</summary>
  private static byte[] BuildZlibBlock(ReadOnlySpan<byte> payload) {
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      z.Write(payload);
    var zlibBytes = ms.ToArray();
    var storedLen = 2 + 1 + zlibBytes.Length; // marker + stream
    var block = new byte[storedLen];
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), (ushort)storedLen);
    block[2] = 0x02; // any non-0x01 marker
    zlibBytes.CopyTo(block, 3);
    return block;
  }

  /// <summary>Concatenates a list of byte arrays into one buffer.</summary>
  private static byte[] Concat(params byte[][] parts) {
    var total = parts.Sum(p => p.Length);
    var result = new byte[total];
    var offset = 0;
    foreach (var p in parts) {
      Buffer.BlockCopy(p, 0, result, offset, p.Length);
      offset += p.Length;
    }
    return result;
  }

  /// <summary>Builds a synthetic 512-byte MBR sector with the given partition entries.</summary>
  private static byte[] BuildMbrSector(params (byte type, uint lbaStart, uint lbaSize)[] partitions) {
    var sector = new byte[512];
    for (var i = 0; i < partitions.Length && i < 4; i++) {
      var off = 446 + i * 16;
      sector[off + 4] = partitions[i].type;
      BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(off + 8, 4), partitions[i].lbaStart);
      BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(off + 12, 4), partitions[i].lbaSize);
    }
    sector[510] = 0x55;
    sector[511] = 0xAA;
    return sector;
  }

  /// <summary>
  /// Builds a complete minimal <c>.gho</c> image: file header + Track 0 record
  /// + Partition record + FEEF partition header + payload blocks + End record.
  /// </summary>
  private static byte[] BuildImage(
      byte compression,
      string description,
      byte[] mbrSector,
      byte[] partitionDescriptorBody,
      byte[][] blocks,
      NortonGhostReader.FileType fileType = NortonGhostReader.FileType.Single) {
    var fileHdr = BuildFileHeader(fileType, compression, 0x12345678, description);
    // Track 0: 6-byte mini-header + MBR sector.
    var track0Body = new byte[6 + mbrSector.Length];
    track0Body[1] = (byte)(mbrSector.Length / 512); // sectors
    mbrSector.CopyTo(track0Body, 6);
    var track0Rec = BuildRecordHeader(NortonGhostReader.RecordTypeTrack0, (ushort)track0Body.Length);
    var partRec = BuildRecordHeader(NortonGhostReader.RecordTypePartition, (ushort)partitionDescriptorBody.Length);
    var partHdr = BuildPartitionHeader(compression, id: 0xCAFEBABE);
    var endRec = BuildRecordHeader(NortonGhostReader.RecordTypeEnd, bodyLen: 0);

    var parts = new List<byte[]> { fileHdr, track0Rec, track0Body, partRec, partitionDescriptorBody, partHdr };
    parts.AddRange(blocks);
    parts.Add(endRec);
    return Concat([.. parts]);
  }

  // ----------------------------------------------------------------------
  // Descriptor metadata tests
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Descriptor_PinsIdentityAndMagic() {
    var d = new NortonGhostFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("NortonGhost"));
    Assert.That(d.Extensions, Does.Contain(".gho"));
    Assert.That(d.Extensions, Does.Contain(".ghs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xFE, 0xEF, 0x01 }));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0xFE, 0xEF, 0x09 }));
  }

  [Test, Category("Stub")]
  public void Descriptor_IsReadOnly_WriteDeferred() {
    var d = new NortonGhostFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    // Honest contract: Create deferred (no Z1 encoder parity without real corpus).
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Description.ToLowerInvariant(), Does.Contain("ghost explorer"),
      "Description must point users at Symantec Ghost Explorer for write needs.");
  }

  // ----------------------------------------------------------------------
  // Header parsing tests
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Header_ParsesMagicTypeVersionAndId() {
    var image = BuildImage(
      compression: NortonGhostReader.CompressionNone,
      description: "Ghost 2003 backup, C: drive, 2003-01-01 12:00",
      mbrSector: BuildMbrSector((0x07, 63u, 1000u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock([1, 2, 3, 4])]);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    Assert.That(r.Image.Header.Type, Is.EqualTo(NortonGhostReader.FileType.Single));
    Assert.That(r.Image.Header.VersionByte, Is.EqualTo(NortonGhostReader.CompressionNone));
    Assert.That(r.Image.Header.ImageId, Is.EqualTo(0x12345678u));
    Assert.That(r.Image.Header.Description, Does.Contain("Ghost 2003 backup"));
  }

  [Test, Category("HappyPath")]
  public void Header_RecognisesSpanFlag_OnGhs() {
    var image = BuildImage(
      compression: NortonGhostReader.CompressionNone,
      description: "spanned segment",
      mbrSector: BuildMbrSector((0x07, 63u, 100u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock([0xAB])],
      fileType: NortonGhostReader.FileType.Span);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    Assert.That(r.Image.Header.Type, Is.EqualTo(NortonGhostReader.FileType.Span));
  }

  [Test, Category("Sad")]
  public void Header_RejectsMissingMagic() {
    var img = new byte[NortonGhostReader.HeaderSize];
    img[0] = 0xDE;
    img[1] = 0xAD;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new NortonGhostReader(ms));
  }

  [Test, Category("Sad")]
  public void Header_RejectsTooSmall() {
    using var ms = new MemoryStream(new byte[16]);
    Assert.Throws<InvalidDataException>(() => _ = new NortonGhostReader(ms));
  }

  // ----------------------------------------------------------------------
  // Record framing tests
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void FindNextRecord_LocatesValidMagicAndType() {
    var noise = new byte[16];
    var rec = BuildRecordHeader(NortonGhostReader.RecordTypePartition, bodyLen: 4);
    var data = Concat(noise, rec);
    var offset = NortonGhostReader.FindNextRecord(data, 0);
    Assert.That(offset, Is.EqualTo(16));
  }

  [Test, Category("BoundaryCondition")]
  public void FindNextRecord_SkipsMagicWithUnknownType() {
    // Plant the 0x012F18D8 magic but with an unknown record-type code (0xFFFF).
    var trap = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(trap.AsSpan(0, 2), 0xFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(trap.AsSpan(4, 4), NortonGhostReader.RecordMagic);
    var realRec = BuildRecordHeader(NortonGhostReader.RecordTypeEnd, bodyLen: 0);
    var data = Concat(trap, realRec);
    var offset = NortonGhostReader.FindNextRecord(data, 0);
    Assert.That(offset, Is.EqualTo(trap.Length), "Must skip false-positive magic with unknown record type.");
  }

  [Test, Category("Sad")]
  public void FindNextRecord_ReturnsNegativeWhenNoRecordPresent() {
    var data = new byte[256];
    var offset = NortonGhostReader.FindNextRecord(data, 0);
    Assert.That(offset, Is.EqualTo(-1));
  }

  // ----------------------------------------------------------------------
  // Z0 (uncompressed) round-trip
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Decompress_Z0_RoundTripsRawBytes() {
    var payload = Encoding.ASCII.GetBytes("Norton Ghost legacy v4-v7 round trip test payload!");
    var image = BuildImage(
      compression: NortonGhostReader.CompressionNone,
      description: "Z0 test",
      mbrSector: BuildMbrSector((0x06, 63u, 200u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock(payload)]);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    Assert.That(r.Image.Partitions, Has.Count.EqualTo(1));
    var decompressed = r.DecompressPartition(r.Image.Partitions[0]);
    Assert.That(decompressed, Is.EqualTo(payload));
  }

  // ----------------------------------------------------------------------
  // Z1 Fast LZ literal-path round-trip
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Decompress_Z1_LiteralPath_RoundTripsBytes() {
    // 32 bytes of distinct literals → 2 full control words, no matches.
    var payload = new byte[32];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(0x40 + i);
    var image = BuildImage(
      compression: NortonGhostReader.CompressionFast,
      description: "Z1 literal test",
      mbrSector: BuildMbrSector((0x07, 63u, 300u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildFastLzLiteralBlock(payload)]);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    Assert.That(r.Image.Partitions, Has.Count.EqualTo(1));
    var decompressed = r.DecompressPartition(r.Image.Partitions[0]);
    Assert.That(decompressed.Length, Is.EqualTo(payload.Length));
    Assert.That(decompressed, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Decompress_Z1_RawMarker_RoundTripsBytes() {
    // Z1 partition + 0x01-first-byte block (uncompressed escape) — must dispatch through the 0x01 marker.
    var payload = Encoding.ASCII.GetBytes("raw-marker inside Z1 stream");
    var image = BuildImage(
      compression: NortonGhostReader.CompressionFast,
      description: "Z1 raw-marker test",
      mbrSector: BuildMbrSector((0x07, 63u, 300u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock(payload)]);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    var decompressed = r.DecompressPartition(r.Image.Partitions[0]);
    Assert.That(decompressed, Is.EqualTo(payload));
  }

  // ----------------------------------------------------------------------
  // Z3 zlib round-trip
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Decompress_Z3_Zlib_RoundTripsBytes() {
    var payload = Encoding.ASCII.GetBytes(new string('A', 200) + new string('B', 200) + "Norton Ghost");
    var image = BuildImage(
      compression: 3,
      description: "Z3 zlib test",
      mbrSector: BuildMbrSector((0x07, 63u, 400u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildZlibBlock(payload)]);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    var decompressed = r.DecompressPartition(r.Image.Partitions[0]);
    Assert.That(decompressed, Is.EqualTo(payload));
  }

  // ----------------------------------------------------------------------
  // MBR + Track 0 surface
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Track0_MbrIsParsedAndSurfaced() {
    var image = BuildImage(
      compression: NortonGhostReader.CompressionNone,
      description: "MBR test",
      mbrSector: BuildMbrSector((0x07, 63u, 1000u), (0x83, 1063u, 2000u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock([1, 2, 3])]);
    using var ms = new MemoryStream(image);
    var r = new NortonGhostReader(ms);
    Assert.That(r.Image.Track0.Length, Is.GreaterThanOrEqualTo(512));
    var mbr = NortonGhostReader.ParseMbr(r.Image.Track0.AsSpan(0, 512));
    Assert.That(mbr, Has.Count.EqualTo(2));
    Assert.That(mbr[0].Type, Is.EqualTo(0x07));
    Assert.That(mbr[0].LbaStart, Is.EqualTo(63u));
    Assert.That(mbr[0].LbaSize, Is.EqualTo(1000u));
    Assert.That(mbr[1].Type, Is.EqualTo(0x83));
  }

  [Test, Category("Sad")]
  public void Track0_ParseMbr_RejectsMissingBootSignature() {
    var bad = new byte[512];
    bad[510] = 0x00; // missing 0x55 0xAA
    var mbr = NortonGhostReader.ParseMbr(bad);
    Assert.That(mbr, Is.Empty);
  }

  // ----------------------------------------------------------------------
  // Descriptor.List + Extract integration
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Descriptor_List_SurfacesMetadataAndPartitionEntries() {
    var payload = Encoding.ASCII.GetBytes("partition payload bytes");
    var image = BuildImage(
      compression: NortonGhostReader.CompressionNone,
      description: "list-test",
      mbrSector: BuildMbrSector((0x07, 63u, 1000u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock(payload)]);
    var d = new NortonGhostFormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("mbr.bin"));
    Assert.That(names, Does.Contain("partition_00.img"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_RoundTripsPartitionPayload() {
    var payload = Encoding.ASCII.GetBytes("extract-test payload bytes");
    var image = BuildImage(
      compression: NortonGhostReader.CompressionNone,
      description: "extract-test",
      mbrSector: BuildMbrSector((0x07, 63u, 1000u)),
      partitionDescriptorBody: new byte[20],
      blocks: [BuildUncompressedBlock(payload)]);

    var tempDir = Path.Combine(Path.GetTempPath(), "ghost_extract_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      var d = new NortonGhostFormatDescriptor();
      using var ms = new MemoryStream(image);
      d.Extract(ms, tempDir, password: null, files: null);
      var partImg = Path.Combine(tempDir, "partition_00.img");
      Assert.That(File.Exists(partImg), Is.True);
      var extracted = File.ReadAllBytes(partImg);
      Assert.That(extracted, Is.EqualTo(payload));
      var metaPath = Path.Combine(tempDir, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var meta = File.ReadAllText(metaPath);
      Assert.That(meta, Does.Contain("[norton-ghost]"));
      Assert.That(meta, Does.Contain("image_id = 0x12345678"));
      Assert.That(meta, Does.Contain("nyarime"), "metadata must cite the RE source.");
      Assert.That(meta, Does.Contain("ghost_explorer_2003"), "metadata must point users at Ghost Explorer for writes.");
    } finally {
      try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
    }
  }

  // ----------------------------------------------------------------------
  // FastLZ decompressor unit
  // ----------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void FastLz_HashIsDeterministicAndInRange() {
    var h1 = FastLzDecompressor.Hash(0x41, 0x42, 0x43);
    var h2 = FastLzDecompressor.Hash(0x41, 0x42, 0x43);
    Assert.That(h1, Is.EqualTo(h2));
    Assert.That(h1, Is.InRange(0, 0xFFF));
  }

  [Test, Category("HappyPath")]
  public void FastLz_RawMarker_CopiesBytesAfterFourByteHeader() {
    var payload = "0123456789abcdef"u8.ToArray();
    var block = new byte[4 + payload.Length];
    block[0] = 0x01;
    payload.CopyTo(block, 4);
    var dst = new byte[256];
    var n = FastLzDecompressor.Decompress(block, dst);
    Assert.That(n, Is.EqualTo(payload.Length));
    Assert.That(dst.AsSpan(0, n).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("Sad")]
  public void FastLz_EmptyBlock_ReturnsNegative() {
    var dst = new byte[16];
    var n = FastLzDecompressor.Decompress([], dst);
    Assert.That(n, Is.EqualTo(-1));
  }
}
