using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Paragon;

namespace Compression.Tests.Paragon;

/// <summary>
/// Round-trip tests for <see cref="ParagonWriter"/> and the CWBP read path
/// of <see cref="ParagonReader"/>. Covers the WORM (write-once
/// round-trippable) scope: emit a fresh PBF, read it back, assert
/// byte-identical entries. Vendor-tool byte-compat is explicitly out of
/// scope and not tested here.
/// </summary>
[TestFixture]
public class ParagonRwTests {

  private static byte[] WriteAndReadBack(IReadOnlyList<byte[]> chunks, bool compressChunks = true) {
    using var ms = new MemoryStream();
    using (var w = new ParagonWriter(ms, compressChunks: compressChunks, leaveOpen: true)) {
      foreach (var c in chunks)
        w.WriteChunk(c);
      w.Finalise();
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsValidHeader_Magic_Major_FormatVersion() {
    var bytes = WriteAndReadBack([new byte[] { 1, 2, 3, 4 }]);
    Assert.That(bytes.Length, Is.GreaterThan(ParagonWriter.HeaderSize));
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("PImg"u8.ToArray()),
      "Writer must emit the vendor-documented 'PImg' magic at offset 0.");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)),
      Is.EqualTo(ParagonWriter.Major),
      "Writer must emit Major = 0x0002 at +4 (vendor literal from HDM-18 RVA 0x4a8dc4).");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)),
      Is.EqualTo(ParagonWriter.FormatVersion),
      "Writer must emit FormatVersion = 0x0003 at +6 (vendor literal).");
  }

  [Test, Category("HappyPath")]
  public void Writer_InstallsCwbpDiscriminator_At_0xF8() {
    var bytes = WriteAndReadBack([new byte[] { 1, 2, 3, 4 }]);
    var disc = bytes.AsSpan(0xF8, 8).ToArray();
    Assert.That(disc, Is.EqualTo(ParagonWriter.CwbpDiscriminator),
      "The 8-byte CWBP discriminator must sit at +0xF8 — past the vendor's last initialised offset 0xF1.");
  }

  [Test, Category("HappyPath")]
  public void Writer_LeavesVendorOffsetsZero() {
    // The vendor reverse-engineered offsets (+0xC, +0x26, +0x27, +0x30,
    // +0x34, +0xD8, +0xE8, +0xF1) must stay zero — forging them without a
    // real Paragon sample would produce an image the vendor rejects /
    // misreads silently.
    var bytes = WriteAndReadBack([new byte[] { 1, 2, 3, 4 }]);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0xC, 4)),
      Is.EqualTo(0u), "Vendor +0xC F12 discriminator must stay zero.");
    Assert.That(bytes[0x26], Is.EqualTo(0), "Vendor +0x26 FlagsA must stay zero.");
    Assert.That(bytes[0x27], Is.EqualTo(0), "Vendor +0x27 FlagsB must stay zero.");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x30, 4)),
      Is.EqualTo(0u), "Vendor +0x30 image-type / fork ID must stay zero.");
    Assert.That(bytes[0x34], Is.EqualTo(0), "Vendor +0x34 volume-name buffer first byte must stay zero.");
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0xD8, 8)),
      Is.EqualTo(0ul), "Vendor +0xD8 ParentId must stay zero.");
    Assert.That(bytes[0xE8], Is.EqualTo(0), "Vendor +0xE8 FlagsC must stay zero.");
    Assert.That(bytes[0xF1], Is.EqualTo(0), "Vendor +0xF1 derived byte must stay zero.");
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_SingleChunk_Compressed_IsByteIdentical() {
    var payload = Enumerable.Range(0, 1024).Select(i => (byte)(i & 0xFF)).ToArray();
    var bytes = WriteAndReadBack([payload], compressChunks: true);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.IsCwbpProduced, Is.True);
    Assert.That(r.ChunkCount, Is.EqualTo(1u));
    Assert.That(r.ChunkTable, Has.Count.EqualTo(1));
    Assert.That(r.ChunkTable[0].LogicalSize, Is.EqualTo((uint)payload.Length));

    var roundtripped = r.ChunkTable.Select(c => r.Entries.First(e => e.Name == $"chunk_{c.ChunkNumber:D6}.bin").Data).ToArray();
    Assert.That(roundtripped[0], Is.EqualTo(payload),
      "WORM-CWBP round-trip must recover the chunk bytes byte-identically.");
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_SingleChunk_Stored_IsByteIdentical() {
    var payload = Encoding.UTF8.GetBytes("Hello, Paragon WORM!");
    var bytes = WriteAndReadBack([payload], compressChunks: false);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.IsCwbpProduced, Is.True);
    Assert.That(r.ChunkTable, Has.Count.EqualTo(1));
    Assert.That(r.ChunkTable[0].IsCompressed, Is.False);
    Assert.That(r.ChunkTable[0].ChunkSize, Is.EqualTo((uint)payload.Length));

    var entry = r.Entries.First(e => e.Name == "chunk_000000.bin");
    Assert.That(entry.Data, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_MultipleChunks_AreByteIdentical() {
    var payloads = new[] {
      Encoding.UTF8.GetBytes("first chunk"),
      Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray(),
      new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
      new byte[8192], // all-zero chunk should compress very small
    };
    var bytes = WriteAndReadBack(payloads, compressChunks: true);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.IsCwbpProduced, Is.True);
    Assert.That(r.ChunkCount, Is.EqualTo((uint)payloads.Length));

    for (var i = 0; i < payloads.Length; i++) {
      var name = $"chunk_{i:D6}.bin";
      var entry = r.Entries.First(e => e.Name == name);
      Assert.That(entry.Data, Is.EqualTo(payloads[i]),
        $"Chunk {i} must round-trip byte-identically.");
    }
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_WritePayload_SplitsAtChunkSize_AndAssemblesIdentically() {
    // Payload is larger than the chunk size, so WritePayload should split.
    var chunkSize = 1024;
    var payload = Enumerable.Range(0, 3 * chunkSize + 137).Select(i => (byte)(i & 0xFF)).ToArray();

    using var ms = new MemoryStream();
    using (var w = new ParagonWriter(ms, compressChunks: true, chunkSize: chunkSize, leaveOpen: true)) {
      w.WritePayload(payload);
      w.Finalise();
    }

    using var rs = new MemoryStream(ms.ToArray());
    using var r = new ParagonReader(rs);
    Assert.That(r.IsCwbpProduced, Is.True);
    Assert.That(r.ChunkCount, Is.EqualTo(4u), "3 full + 1 partial chunk expected.");
    var assembled = r.AssembleLogicalPayload();
    Assert.That(assembled, Is.EqualTo(payload),
      "Full-payload reassembly must recover the original bytes byte-identically.");
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_HighlyCompressibleChunk_StoresSmallerThanLogical() {
    var payload = new byte[16 * 1024];
    Array.Fill(payload, (byte)0xAA);
    var bytes = WriteAndReadBack([payload], compressChunks: true);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.ChunkTable[0].IsCompressed, Is.True);
    Assert.That(r.ChunkTable[0].ChunkSize, Is.LessThan(r.ChunkTable[0].LogicalSize),
      "Highly redundant chunks must compress to less than the logical size.");

    var entry = r.Entries.First(e => e.Name == "chunk_000000.bin");
    Assert.That(entry.Data, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_HighEntropyChunk_FallsBackToStored() {
    // Pseudo-random data should compress to MORE bytes than the input
    // (zlib overhead + no redundancy) — the writer's "if compressed >=
    // logical, fall back to stored" branch must kick in.
    var rng = new Random(42);
    var payload = new byte[8192];
    rng.NextBytes(payload);
    var bytes = WriteAndReadBack([payload], compressChunks: true);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.ChunkTable[0].IsCompressed, Is.False,
      "High-entropy chunks must fall back to stored when zlib produces more bytes than the input.");
    Assert.That(r.ChunkTable[0].ChunkSize, Is.EqualTo(r.ChunkTable[0].LogicalSize));

    var entry = r.Entries.First(e => e.Name == "chunk_000000.bin");
    Assert.That(entry.Data, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Roundtrip_ZeroByteChunk_IsByteIdentical() {
    var bytes = WriteAndReadBack([Array.Empty<byte>()], compressChunks: true);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.ChunkTable, Has.Count.EqualTo(1));
    Assert.That(r.ChunkTable[0].LogicalSize, Is.EqualTo(0u));
    Assert.That(r.ChunkTable[0].ChunkSize, Is.EqualTo(0u));
    Assert.That(r.ChunkTable[0].IsCompressed, Is.False);

    var entry = r.Entries.First(e => e.Name == "chunk_000000.bin");
    Assert.That(entry.Data, Has.Length.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Reader_VerifiesAdler32_OnEachChunk() {
    var payload = Encoding.UTF8.GetBytes("checksum-protected chunk");
    var bytes = WriteAndReadBack([payload], compressChunks: false);

    // Corrupt one body byte; the reader's Adler-32 gate must reject.
    var bodyOffset = ParagonWriter.HeaderSize;
    bytes[bodyOffset] ^= 0xFF;

    using var ms = new MemoryStream(bytes);
    var ex = Assert.Throws<InvalidDataException>(() => _ = new ParagonReader(ms));
    Assert.That(ex!.Message, Does.Contain("adler32"),
      "The reader must reject the corrupted chunk via its Adler-32 gate.");
  }

  [Test, Category("HappyPath")]
  public void Reader_HoldsVendorRoMetadataPath_ForNonCwbpFiles() {
    // A file without the CWBP discriminator (i.e. a vendor-style file)
    // must still parse as R/O metadata, with the two synthetic entries
    // metadata.ini + paragon-backup.bin.
    var img = new byte[256];
    Encoding.ASCII.GetBytes("PImg").CopyTo(img.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(4, 2), 0x0002);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(6, 2), 0x0003);
    // No CWBP marker at +0xF8.

    using var ms = new MemoryStream(img);
    using var r = new ParagonReader(ms);
    Assert.That(r.IsCwbpProduced, Is.False);
    Assert.That(r.ChunkCount, Is.EqualTo(0u));
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "paragon-backup.bin" }),
      "Vendor-style (non-CWBP) files must still fall back to the R/O metadata pass.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CreateAndExtract_Roundtrip() {
    // Full descriptor-level round-trip: ArchiveInputInfo -> Create ->
    // List -> Entries match.
    var d = new ParagonFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("file1.bin", Encoding.UTF8.GetBytes("first")),
      ArchiveInputInfo.InMemory("file2.bin", Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray()),
    };

    using var output = new MemoryStream();
    d.Create(output, inputs, new FormatCreateOptions { MethodName = "zlib" });

    output.Position = 0;
    var entries = d.List(output, password: null);
    var entryNames = entries.Select(e => e.Name).ToList();

    // The descriptor surfaces metadata.ini + N chunk entries.
    Assert.That(entryNames, Does.Contain("metadata.ini"));
    Assert.That(entryNames, Does.Contain("chunk_000000.bin"));
    Assert.That(entryNames, Does.Contain("chunk_000001.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_OpenEntry_ReturnsCorrectBytes() {
    var d = new ParagonFormatDescriptor();
    var payload = Encoding.UTF8.GetBytes("openable entry payload");
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("hello.bin", payload),
    };

    using var output = new MemoryStream();
    d.Create(output, inputs, new FormatCreateOptions { MethodName = "zlib" });

    output.Position = 0;
    using var entryStream = ((IArchiveFormatOperations)d).OpenEntry(output, "chunk_000000.bin", password: null);
    using var sink = new MemoryStream();
    entryStream.CopyTo(sink);
    Assert.That(sink.ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_StoredMethod_DoesNotCompress() {
    var d = new ParagonFormatDescriptor();
    var payload = new byte[1024];
    Array.Fill(payload, (byte)0xCC);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("data.bin", payload),
    };

    using var output = new MemoryStream();
    d.Create(output, inputs, new FormatCreateOptions { MethodName = "stored" });

    output.Position = 0;
    using var r = new ParagonReader(output);
    Assert.That(r.ChunkTable[0].IsCompressed, Is.False,
      "MethodName='stored' must bypass zlib compression even for highly compressible data.");
    Assert.That(r.ChunkTable[0].ChunkSize, Is.EqualTo((uint)payload.Length));
  }

  [Test, Category("Sad")]
  public void Descriptor_Create_UnknownMethod_Throws() {
    var d = new ParagonFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("data.bin", new byte[] { 1, 2, 3 }),
    };

    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() =>
      d.Create(output, inputs, new FormatCreateOptions { MethodName = "lzma" }));
  }

  [Test, Category("Sad")]
  public void Writer_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => _ = new ParagonWriter(null!));
  }

  [Test, Category("Sad")]
  public void Writer_NonSeekableStream_Throws() {
    using var ns = new NonSeekableStream();
    Assert.Throws<ArgumentException>(() => _ = new ParagonWriter(ns));
  }

  [Test, Category("HappyPath")]
  public void ChunkTable_Entries_ExposeArchitecturalLayout() {
    // The per-chunk fields the reverse-engineered "ChunkNumber: %d,
    // ChunkOffSet: 0x%016I64x, ChunkSize: %d, ChunkIsCompress: %c"
    // debug-string round-trip emits must surface as ChunkTable entries
    // with the documented field types (u32 / u64 / u32 / bool).
    var payloads = new[] {
      Encoding.UTF8.GetBytes("first"),
      Encoding.UTF8.GetBytes("second chunk"),
    };
    var bytes = WriteAndReadBack(payloads, compressChunks: false);

    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);
    Assert.That(r.ChunkTable, Has.Count.EqualTo(2));
    Assert.That(r.ChunkTable[0].ChunkNumber, Is.EqualTo(0u));
    Assert.That(r.ChunkTable[1].ChunkNumber, Is.EqualTo(1u));
    Assert.That(r.ChunkTable[0].ChunkOffset, Is.EqualTo((ulong)ParagonWriter.HeaderSize),
      "First chunk body must start immediately after the header.");
    Assert.That(r.ChunkTable[1].ChunkOffset, Is.GreaterThan(r.ChunkTable[0].ChunkOffset));
  }

  [Test, Category("HappyPath")]
  public void Metadata_DocumentsCwbpScope_OnRoundtrippedFile() {
    var bytes = WriteAndReadBack([new byte[] { 1, 2, 3, 4 }]);
    using var ms = new MemoryStream(bytes);
    using var r = new ParagonReader(ms);

    var meta = r.Entries.First(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);

    Assert.That(text, Does.Contain("parse_status=cwbp-roundtrip"),
      "CWBP-discriminated files must surface the cwbp-roundtrip parse_status.");
    Assert.That(text, Does.Contain("cwbp_discriminator_offset=0xF8"));
    Assert.That(text, Does.Contain("cwbp_chunk_entry_size_bytes=40"));
    Assert.That(text, Does.Contain("cwbp_chunk_count=1"));
    Assert.That(text, Does.Contain("Vendor-tool round-trip is explicitly out of scope"),
      "Metadata must affirmatively flag vendor-tool round-trip as out of scope.");
  }

  /// <summary>
  /// Bit-exact self-round-trip: writing twice produces byte-identical
  /// output. Locks in determinism so the writer can be used in
  /// reproducible-build scenarios.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Roundtrip_Determinism_TwoWritesProduceIdenticalBytes() {
    var payload = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
    var a = WriteAndReadBack([payload], compressChunks: false);
    var b = WriteAndReadBack([payload], compressChunks: false);
    Assert.That(a, Is.EqualTo(b), "Two writes of the same input must produce byte-identical PBFs.");
  }

  /// <summary>
  /// Equivalence class: very large chunks (multi-MB). Stresses the
  /// zlib pipeline + chunk-table offset math against a payload that
  /// exceeds the default 128 KiB chunk size.
  /// </summary>
  [Test, Category("Boundary")]
  public void Roundtrip_LargePayload_AcrossManyChunks() {
    // ~1.5 MB at 64 KB chunk size = 24 chunks.
    var chunkSize = 64 * 1024;
    var payload = new byte[1_500_000];
    new Random(7).NextBytes(payload);

    using var ms = new MemoryStream();
    using (var w = new ParagonWriter(ms, compressChunks: true, chunkSize: chunkSize, leaveOpen: true)) {
      w.WritePayload(payload);
      w.Finalise();
    }

    using var rs = new MemoryStream(ms.ToArray());
    using var r = new ParagonReader(rs);
    Assert.That(r.IsCwbpProduced, Is.True);
    Assert.That(r.ChunkCount, Is.GreaterThan(20u));
    var assembled = r.AssembleLogicalPayload();
    Assert.That(assembled, Is.EqualTo(payload));
  }

  private sealed class NonSeekableStream : MemoryStream {
    public override bool CanSeek => false;
  }
}
