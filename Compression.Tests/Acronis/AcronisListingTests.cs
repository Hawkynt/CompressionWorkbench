using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

[TestFixture]
public class AcronisListingTests {

  /// <summary>
  /// Builds a synthetic single-volume Windows .tib slice containing exactly one Listing record
  /// holding the supplied entries, followed by a file-system trailer + mirror footer.
  /// </summary>
  private static byte[] BuildSyntheticTib(IReadOnlyList<(string Path, string Name, long FileSize)> entries) {
    using var ms = new MemoryStream();

    // 1) Volume header — Windows format, header length 0x20, block size 32.
    Span<byte> hdr = stackalloc byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);               // magic
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], 0x20);                  // header length
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);                     // version = Windows
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], 0x11111111);            // archive key
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], 0x22222222);           // slice key
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], 0x33333333);           // volume key
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], 1);                    // sequence
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], 0);                    // adler (unchecked)
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);                   // block size
    ms.Write(hdr);

    // 2) Build the uncompressed Listing payload.
    var payload = BuildListingPayload(entries);

    // 3) Record metadata offset is where the Listing record starts.
    var metaOffset = (long)ms.Position;

    // 4) Listing record: 1-byte type + raw deflate(payload) + 4-byte checksum (we use zeros).
    ms.WriteByte((byte)AcronisRecordType.Listing);
    var deflateStart = ms.Position;
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    var deflateLen = ms.Position - deflateStart;
    Span<byte> checksum = stackalloc byte[4];
    ms.Write(checksum);

    // 5) Trailer payload: uint64 LE metaOffset + 4-byte file-system magic.
    Span<byte> trailerWord = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailerWord, metaOffset);
    trailerWord[8] = 0x2C; trailerWord[9] = 0x8A; trailerWord[10] = 0xE1; trailerWord[11] = 0x94;
    ms.Write(trailerWord);

    // 6) 48-byte mirror footer: 8-byte slice size + 8 zero bytes + 32-byte reversed header.
    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length); // slice size (not strictly checked)
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);

    _ = deflateLen; // suppress unused-warning in release builds
    return ms.ToArray();
  }

  /// <summary>
  /// Builds the uncompressed payload for a Listing record, matching the layout decoded by
  /// <see cref="AcronisRecordReader"/>: uint32 numEntries, then for each entry
  /// (path/name/shortName length-prefixed UTF-16LE + timestamps + sizes + meta offset + 38 reserved).
  /// </summary>
  private static byte[] BuildListingPayload(IReadOnlyList<(string Path, string Name, long FileSize)> entries) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)entries.Count);
    foreach (var (path, name, fileSize) in entries) {
      WriteCountedUtf16(w, path);
      w.Write(0u); // unknown uint32
      WriteCountedUtf16(w, name);
      WriteCountedUtf16(w, "");      // shortName
      WriteUInt48(w, 0); w.Write((ushort)0); // time (48-bit) + 16-bit pad → 8 bytes
      w.Write(0u);                    // unknown uint32
      WriteUInt48(w, (ulong)fileSize); w.Write((ushort)0); // fileSize
      WriteUInt48(w, (ulong)fileSize); w.Write((ushort)0); // fileSize2
      WriteUInt48(w, 0); w.Write((ushort)0);              // metaOffset (unknown, irrelevant for listing)
      w.Write(new byte[38]);          // 38-byte tail
    }
    w.Flush();
    return ms.ToArray();
  }

  private static void WriteCountedUtf16(BinaryWriter w, string s) {
    w.Write((uint)s.Length);
    if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
  }

  private static void WriteUInt48(BinaryWriter w, ulong v) {
    for (var i = 0; i < 6; i++) w.Write((byte)((v >> (i * 8)) & 0xFF));
  }

  // ----- Direct payload + record-stream parsing -----

  [Test, Category("HappyPath")]
  public void ParseListing_PayloadRoundTrip() {
    var payload = BuildListingPayload([("d/", "x.txt", 5L), ("d/sub/", "y.bin", 42L)]);
    var files = AcronisRecordReader.ParseListing(payload);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(2));
      Assert.That(files[0].Name, Is.EqualTo("x.txt"));
      Assert.That(files[0].FileSize, Is.EqualTo(5));
      Assert.That(files[1].Name, Is.EqualTo("y.bin"));
      Assert.That(files[1].FileSize, Is.EqualTo(42));
    });
  }

  [Test, Category("HappyPath")]
  public void ReadAll_StopsAtUnknownTypeWithoutLosingPriorRecords() {
    // Build: Listing record then a single sentinel byte that decodes as an unknown record type.
    var src = BuildListingPayload([("d/", "x.txt", 5L)]);
    using var ms = new MemoryStream();
    ms.WriteByte((byte)AcronisRecordType.Listing);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(src);
    ms.Write(stackalloc byte[4]);     // 4-byte checksum
    ms.WriteByte(0xEE);               // unknown record type — walker must stop cleanly
    ms.Position = 0;

    var recs = AcronisRecordReader.ReadAll(ms, ms.Length);
    Assert.Multiple(() => {
      Assert.That(recs, Has.Count.EqualTo(1));
      Assert.That(recs[0].Type, Is.EqualTo(AcronisRecordType.Listing));
      Assert.That(recs[0].Files, Is.Not.Null);
      Assert.That(recs[0].Files!.Count, Is.EqualTo(1));
    });
  }

  // ----- Volume header tests -----

  [Test, Category("HappyPath")]
  public void Header_Read_Windows() {
    var tib = BuildSyntheticTib([("dir/", "a.txt", 11)]);
    using var ms = new MemoryStream(tib);
    var hdr = AcronisVolumeHeader.Read(ms);
    Assert.Multiple(() => {
      Assert.That(hdr.HeaderLength, Is.EqualTo(0x20));
      Assert.That(hdr.Version, Is.EqualTo(AcronisVolumeVersion.Windows));
      Assert.That(hdr.BlockSize, Is.EqualTo(32u));
      Assert.That(hdr.ArchiveKey, Is.EqualTo(0x11111111u));
      Assert.That(hdr.Sequence, Is.EqualTo(1u));
    });
  }

  [Test, Category("ErrorHandling")]
  public void Header_BadMagic_Throws() {
    using var ms = new MemoryStream(new byte[64]);
    Assert.Throws<InvalidDataException>(() => AcronisVolumeHeader.Read(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Header_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[16]);
    Assert.Throws<InvalidDataException>(() => AcronisVolumeHeader.Read(ms));
  }

  // ----- Trailer tests -----

  [Test, Category("HappyPath")]
  public void Trailer_Read_FileSystem() {
    var tib = BuildSyntheticTib([("d/", "x.txt", 5)]);
    using var ms = new MemoryStream(tib);
    var hdr = AcronisVolumeHeader.Read(ms);
    var trailer = AcronisSliceTrailer.TryRead(ms, hdr);
    Assert.That(trailer, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(trailer!.MirrorValid, Is.True, "header mirror image must validate");
      Assert.That(trailer.Form, Is.EqualTo(AcronisSliceForm.FileSystem));
      Assert.That(trailer.MetadataOffset, Is.EqualTo(32L), "metadata offset = end of header");
    });
  }

  [Test, Category("EdgeCase")]
  public void Trailer_TooSmall_ReturnsNull() {
    // Build only a header — no footer, no trailer.
    var buf = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0x20);
    using var ms = new MemoryStream(buf);
    var hdr = AcronisVolumeHeader.Read(ms);
    var trailer = AcronisSliceTrailer.TryRead(ms, hdr);
    Assert.That(trailer, Is.Null);
  }

  // ----- Listing extraction tests -----

  [Test, Category("HappyPath")]
  public void List_ReturnsListedFiles() {
    var entries = new[] {
      ("C:\\dir1\\", "alpha.txt", 100L),
      ("C:\\dir1\\sub\\", "beta.bin", 4096L),
      ("C:\\", "root.dat", 7L),
    };
    var tib = BuildSyntheticTib(entries);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var list = desc.List(ms, null);

    Assert.That(list, Has.Count.EqualTo(3));
    Assert.Multiple(() => {
      Assert.That(list[0].Name, Does.EndWith("alpha.txt"));
      Assert.That(list[0].OriginalSize, Is.EqualTo(100));
      Assert.That(list[1].Name, Does.EndWith("beta.bin"));
      Assert.That(list[1].OriginalSize, Is.EqualTo(4096));
      Assert.That(list[2].Name, Does.EndWith("root.dat"));
    });
  }

  [Test, Category("HappyPath")]
  public void List_PreservesFullPath() {
    var tib = BuildSyntheticTib([("C:\\users\\admin\\Documents\\", "report.docx", 12345L)]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var list = desc.List(ms, null);
    Assert.That(list, Has.Count.EqualTo(1));
    Assert.That(list[0].Name, Does.Contain("admin"));
    Assert.That(list[0].Name, Does.EndWith("report.docx"));
  }

  [Test, Category("EdgeCase")]
  public void List_EmptyArchive() {
    var tib = BuildSyntheticTib([]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var list = desc.List(ms, null);
    Assert.That(list, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void List_BoundaryFileSize() {
    var tib = BuildSyntheticTib([
      ("", "zero.bin", 0L),
      ("", "huge.bin", (1L << 40) + 17), // ~1 TiB, fits in 48-bit
    ]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var list = desc.List(ms, null);
    Assert.Multiple(() => {
      Assert.That(list[0].OriginalSize, Is.EqualTo(0));
      Assert.That(list[1].OriginalSize, Is.EqualTo((1L << 40) + 17));
    });
  }

  // ----- Honest fallback: listing-only slices (no RecordIndex) refuse extraction -----

  [Test, Category("ErrorHandling")]
  public void Extract_ListingOnlySlice_RefusesWithPairingDiagnostic() {
    // BuildSyntheticTib emits a Listing record but no per-file RecordIndex(108), so the
    // sequential Listing↔RecordIndex pairing gate must reject extraction (honest fallback —
    // see AcronisExtractionTests for the positive end-to-end path).
    var tib = BuildSyntheticTib([("d/", "x.txt", 5)]);
    var desc = new AcronisFormatDescriptor();
    using var ms = new MemoryStream(tib);
    var tempDir = Path.Combine(Path.GetTempPath(), "acronis_test_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      var ex = Assert.Throws<NotSupportedException>(() => desc.Extract(ms, tempDir, null, null));
      Assert.That(ex!.Message, Does.Contain("pairing").Or.Contain("RecordIndex").Or.Contain("Listing"));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  // ----- Descriptor metadata -----

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new AcronisFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(desc.Id, Is.EqualTo("AcronisTib"));
      Assert.That(desc.Extensions, Does.Contain(".tib"));
      Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
      Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
      Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
      Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
      Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xCE, 0x24, 0xB9, 0xA2 }));
    });
  }

  // ----- Mac volume: detected as Mac, listing is empty (not parsed). -----

  [Test, Category("EdgeCase")]
  public void Reader_MacVolume_NoEntries() {
    var buf = new byte[256];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0x24);   // mac header length
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), 1);      // version = Mac
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 1);     // sequence
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28), 4096);  // block size
    using var ms = new MemoryStream(buf);
    var r = new AcronisReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.Header.Version, Is.EqualTo(AcronisVolumeVersion.Mac));
      Assert.That(r.Entries, Is.Empty);
    });
  }
}
