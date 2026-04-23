using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Nrv2b;
using Compression.Core.Dictionary.Nrv2d;
using Compression.Core.Dictionary.Nrv2e;
using FileFormat.Upx;

namespace Compression.Tests.Upx;

[TestFixture]
public class UpxTests {

  /// <summary>
  /// Builds a minimal PE skeleton with the UPX0/UPX1/UPX2 section layout but
  /// no runnable code. Enough to exercise section-table parsing in the reader.
  /// </summary>
  private static byte[] BuildUpxLikePe() {
    var buf = new byte[0x400];
    // DOS header.
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    const int eLfanew = 0x80;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), eLfanew);

    // PE header.
    buf[eLfanew] = (byte)'P'; buf[eLfanew + 1] = (byte)'E';
    // COFF header at eLfanew+4: numSections=3 @ offset 6, optHdrSize=0 @ offset 20.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(eLfanew + 4 + 2), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(eLfanew + 4 + 16), 0);

    // Section table at eLfanew + 24 + optHdrSize (= 0) = eLfanew + 24.
    var secTable = eLfanew + 24;
    string[] names = ["UPX0", "UPX1", "UPX2"];
    for (var i = 0; i < 3; i++) {
      var off = secTable + i * 40;
      var nameBytes = Encoding.ASCII.GetBytes(names[i]);
      nameBytes.CopyTo(buf.AsSpan(off));
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 8), 0x1000); // vsize
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 12), (uint)(0x1000 * (i + 1))); // vaddr
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 16), (uint)(i == 0 ? 0 : 0x100)); // rsize
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 20), (uint)(i == 0 ? 0 : 0x200 + i * 0x100)); // roff
    }
    return buf;
  }

  /// <summary>
  /// Writes a plausible UPX packer header (with magic "UPX!" + a 32-byte struct)
  /// into the supplied buffer at <paramref name="offset"/>.
  /// </summary>
  private static void PatchPackerHeader(byte[] buf, int offset,
      byte version = 0x04, byte format = 9 /* WIN32_PE */, byte method = 14 /* LZMA */,
      byte level = 8, uint uLen = 0x10000, uint cLen = 0x4000,
      uint uAdler = 0xABCDEF01, uint cAdler = 0x12345678) {
    UpxReader.PackerMagic.CopyTo(buf.AsSpan(offset));
    buf[offset + 4] = version;
    buf[offset + 5] = format;
    buf[offset + 6] = method;
    buf[offset + 7] = level;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), uLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), cLen);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 16), uAdler);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 20), cAdler);
    // Filter id + cto size + filter trailer bytes — zero them out explicitly so
    // the reader's sanity gates (filterId ≤ 0xFFFF, etc.) accept the header.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 24), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 28), 0);
  }

  [Test, Category("HappyPath")]
  public void Read_DetectsUpxSectionNames() {
    var buf = BuildUpxLikePe();
    var info = UpxReader.Read(buf);
    Assert.That(info.Kind, Is.EqualTo(UpxReader.ContainerKind.Pe));
    Assert.That(info.IsUpxPacked, Is.True);
    Assert.That(info.PeSections, Has.Count.EqualTo(3));
    Assert.That(info.PeSections.Select(s => s.Name), Is.EquivalentTo(new[] { "UPX0", "UPX1", "UPX2" }));
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesPackerHeader() {
    var buf = BuildUpxLikePe();
    PatchPackerHeader(buf, 0x300);
    var info = UpxReader.Read(buf);
    Assert.That(info.Header, Is.Not.Null);
    Assert.That(info.Header!.Offset, Is.EqualTo(0x300));
    Assert.That(info.Header.Method, Is.EqualTo((byte)14));
    Assert.That(UpxReader.MethodName(info.Header.Method), Is.EqualTo("LZMA"));
    Assert.That(UpxReader.FormatName(info.Header.Format), Is.EqualTo("UPX_F_WIN32_PE"));
    Assert.That(info.Header.UncompressedSize, Is.EqualTo(0x10000u));
    Assert.That(info.Header.CompressedSize, Is.EqualTo(0x4000u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_ProducesExpectedEntries() {
    var buf = BuildUpxLikePe();
    PatchPackerHeader(buf, 0x300);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(buf);
      new UpxFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "section_UPX0.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "section_UPX1.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "section_UPX2.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "upx_packer_header.bin")), Is.True);

      var headerBytes = File.ReadAllBytes(Path.Combine(tmp, "upx_packer_header.bin"));
      Assert.That(headerBytes[0], Is.EqualTo((byte)'U'));
      Assert.That(headerBytes[1], Is.EqualTo((byte)'P'));
      Assert.That(headerBytes[2], Is.EqualTo((byte)'X'));
      Assert.That(headerBytes[3], Is.EqualTo((byte)'!'));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Read_DetectsToolingString() {
    var banner = "$Info: This file is packed with the UPX executable packer http://upx.sf.net $"u8;
    var buf = new byte[1024];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    banner.CopyTo(buf.AsSpan(0x200));
    var info = UpxReader.Read(buf);
    Assert.That(info.IsUpxPacked, Is.True);
    Assert.That(info.ToolingString, Does.Contain("UPX"));
  }

  [Test, Category("EdgeCase")]
  public void Read_PlainPeWithoutUpxMarkers_IsNotFlaggedAsPacked() {
    var buf = new byte[0x400];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), 0x80);
    buf[0x80] = (byte)'P'; buf[0x81] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x86), 1); // 1 section
    var secTable = 0x80 + 24;
    Encoding.ASCII.GetBytes(".text").CopyTo(buf.AsSpan(secTable));

    var info = UpxReader.Read(buf);
    Assert.That(info.IsUpxPacked, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_NonUpxFile_ThrowsSoDetectorCanFallBack() {
    var buf = new byte[64];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    using var ms = new MemoryStream(buf);
    Assert.That(() => new UpxFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void Read_DetectsElfContainer() {
    var buf = new byte[64];
    buf[0] = 0x7F; buf[1] = (byte)'E'; buf[2] = (byte)'L'; buf[3] = (byte)'F';
    PatchPackerHeader(buf, 32);
    var info = UpxReader.Read(buf);
    Assert.That(info.Kind, Is.EqualTo(UpxReader.ContainerKind.Elf));
    Assert.That(info.IsUpxPacked, Is.True);
  }

  // ── Tampered-binary detection ──────────────────────────────────────────

  /// <summary>
  /// Builds a structurally UPX-shaped PE: 3 sections, BSS-style first section
  /// (RawSize=0, VirtualSize&gt;0), entry point in the last section, RWX flags,
  /// and a 16 KB high-entropy payload in the middle section. Section names are
  /// caller-supplied so we can simulate renamed/wiped variants.
  /// </summary>
  private static byte[] BuildStructuralUpxPe(string[] sectionNames, uint entryRva, byte[]? payload = null) {
    var buf = new byte[0x10000];
    buf[0] = (byte)'M'; buf[1] = (byte)'Z';
    const int eLfanew = 0x80;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C), eLfanew);
    buf[eLfanew] = (byte)'P'; buf[eLfanew + 1] = (byte)'E';
    // numSections=3 @ COFF+2, optHdrSize=224 @ COFF+16 (standard PE32 OptionalHeader size).
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(eLfanew + 4 + 2), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(eLfanew + 4 + 16), 224);
    // OptionalHeader.AddressOfEntryPoint at offset 16 inside OptionalHeader.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(eLfanew + 24 + 16), entryRva);

    var secTable = eLfanew + 24 + 224;
    // Section 0: BSS-like (RawSize=0)
    Encoding.ASCII.GetBytes(sectionNames[0]).CopyTo(buf.AsSpan(secTable));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 8), 0x4000);   // VirtualSize
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 12), 0x1000);  // VAddr
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 16), 0);       // RawSize=0 (key signal)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 20), 0);       // RawOffset=0
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 36), 0xE0000080); // RWX flags

    // Section 1: compressed payload
    Encoding.ASCII.GetBytes(sectionNames[1]).CopyTo(buf.AsSpan(secTable + 40));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 40 + 8), 0x4000);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 40 + 12), 0x5000);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 40 + 16), 0x4000); // RawSize
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 40 + 20), 0x1000); // RawOffset
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 40 + 36), 0xE0000080);

    // Section 2: tiny, contains entry-point area
    Encoding.ASCII.GetBytes(sectionNames[2]).CopyTo(buf.AsSpan(secTable + 80));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 80 + 8), 0x100);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 80 + 12), 0x9000);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 80 + 16), 0x100);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 80 + 20), 0x5000);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 80 + 36), 0xE0000040);

    // Fill section 1 with high-entropy bytes (or supplied payload).
    var rng = new Random(unchecked((int)0xDEADBEEF));
    for (var i = 0; i < 0x4000; i++) buf[0x1000 + i] = (byte)rng.Next(256);
    if (payload != null) payload.CopyTo(buf.AsSpan(0x1000));
    return buf;
  }

  [Test, Category("HappyPath")]
  public void Detect_RenamedSections_ButPackHeaderIntact() {
    // Section names wiped to non-UPX, but PackHeader magic is still there.
    var buf = BuildStructuralUpxPe(["a000", "a001", "a002"], entryRva: 0x9000);
    PatchPackerHeader(buf, 0x4FE0);
    var info = UpxReader.Read(buf);
    Assert.That(info.Confidence, Is.EqualTo(UpxReader.DetectionConfidence.Confirmed));
    Assert.That(info.Header, Is.Not.Null);
    Assert.That(info.Header!.MagicIntact, Is.True);
    Assert.That(info.Evidence.SectionNamesMatch, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Detect_WipedMagic_ButValidStructPresent() {
    // Build a buffer with packer header BUT wipe just the 4 magic bytes.
    var buf = BuildStructuralUpxPe(["a000", "a001", "a002"], entryRva: 0x9000);
    PatchPackerHeader(buf, 0x4FE0, method: 2 /* NRV2B */);
    // Wipe the "UPX!" string but leave the rest of the header intact.
    buf[0x4FE0] = 0; buf[0x4FE1] = 0; buf[0x4FE2] = 0; buf[0x4FE3] = 0;

    var info = UpxReader.Read(buf);
    Assert.That(info.Confidence, Is.EqualTo(UpxReader.DetectionConfidence.Confirmed));
    Assert.That(info.Header, Is.Not.Null);
    Assert.That(info.Header!.MagicIntact, Is.False, "magic should report as wiped");
    Assert.That(info.Header.Method, Is.EqualTo((byte)2), "method byte should still be readable");
  }

  [Test, Category("HappyPath")]
  public void Detect_FullyTampered_StructuralFingerprintCatchesIt() {
    // Renamed sections, no PackHeader anywhere, but the structural shape (BSS-style
    // first section, entry in last, RWX flags, high entropy) still gives it away.
    var buf = BuildStructuralUpxPe(["foo", "bar", "baz"], entryRva: 0x9000);
    var info = UpxReader.Read(buf);
    Assert.That(info.Confidence, Is.EqualTo(UpxReader.DetectionConfidence.Heuristic));
    Assert.That(info.Evidence.StructuralFingerprintMatch, Is.True);
    Assert.That(info.Evidence.FingerprintScore, Is.GreaterThanOrEqualTo(50));
    Assert.That(info.Evidence.PackHeaderFound, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Detect_NormalPe_Without_StructuralShape_IsNotPacked() {
    // Build the same structure but with NO BSS-style first section.
    var buf = BuildStructuralUpxPe([".text", ".data", ".rsrc"], entryRva: 0x9000);
    // Patch section 0 to have RawSize > 0 (looks like .text, not UPX0).
    var secTable = 0x80 + 24 + 224;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 16), 0x100);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(secTable + 20), 0x5800);
    var info = UpxReader.Read(buf);
    Assert.That(info.Confidence, Is.EqualTo(UpxReader.DetectionConfidence.None),
      $"score={info.Evidence.FingerprintScore}, reasoning={info.Evidence.FingerprintReasoning}");
  }

  // ── Decompression round-trip via NRV2B ──────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2bPayload() {
    // Compress a known plaintext with NRV2B, embed it as the "compressed payload"
    // in a synthetic UPX-shaped binary, and verify the descriptor surfaces a
    // matching decompressed_payload.bin entry.
    var nrv2b = new Nrv2bBuildingBlock();
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Hello UPX! ", 32)));
    var compressedWithHeader = nrv2b.Compress(original);
    // Strip the 4-byte size header that the building block prepends — UPX payloads are raw.
    var compressed = compressedWithHeader[4..];

    var buf = BuildStructuralUpxPe(["UPX0", "UPX1", "UPX2"], entryRva: 0x9000);
    // Place compressed payload at the start of section 1 (RawOffset 0x1000).
    compressed.CopyTo(buf.AsSpan(0x1000));
    // Place PackHeader immediately after the compressed payload.
    var headerOffset = 0x1000 + compressed.Length;
    PatchPackerHeader(buf, headerOffset,
      method: 2 /* NRV2B_LE32 */,
      uLen: (uint)original.Length,
      cLen: (uint)compressed.Length);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(buf);
      new UpxFormatDescriptor().Extract(ms, tmp, null, null);
      var decompressedPath = Path.Combine(tmp, "decompressed_payload.bin");
      Assert.That(File.Exists(decompressedPath), Is.True, "decompressed_payload.bin should be emitted");
      Assert.That(File.ReadAllBytes(decompressedPath), Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2dPayload() {
    // Compress with NRV2D, embed in synthetic UPX-shaped binary, verify decompressed_payload.bin.
    var nrv2d = new Nrv2dBuildingBlock();
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Hello UPX-D! ", 32)));
    var compressedWithHeader = nrv2d.Compress(original);
    var compressed = compressedWithHeader[4..];

    var buf = BuildStructuralUpxPe(["UPX0", "UPX1", "UPX2"], entryRva: 0x9000);
    compressed.CopyTo(buf.AsSpan(0x1000));
    var headerOffset = 0x1000 + compressed.Length;
    PatchPackerHeader(buf, headerOffset,
      method: 3 /* NRV2D_LE32 */,
      uLen: (uint)original.Length,
      cLen: (uint)compressed.Length);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(buf);
      new UpxFormatDescriptor().Extract(ms, tmp, null, null);
      var decompressedPath = Path.Combine(tmp, "decompressed_payload.bin");
      Assert.That(File.Exists(decompressedPath), Is.True, "decompressed_payload.bin should be emitted");
      Assert.That(File.ReadAllBytes(decompressedPath), Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2ePayload() {
    // Compress with NRV2E, embed in synthetic UPX-shaped binary, verify decompressed_payload.bin.
    var nrv2e = new Nrv2eBuildingBlock();
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Hello UPX-E! ", 32)));
    var compressedWithHeader = nrv2e.Compress(original);
    var compressed = compressedWithHeader[4..];

    var buf = BuildStructuralUpxPe(["UPX0", "UPX1", "UPX2"], entryRva: 0x9000);
    compressed.CopyTo(buf.AsSpan(0x1000));
    var headerOffset = 0x1000 + compressed.Length;
    PatchPackerHeader(buf, headerOffset,
      method: 8 /* NRV2E_LE32 */,
      uLen: (uint)original.Length,
      cLen: (uint)compressed.Length);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(buf);
      new UpxFormatDescriptor().Extract(ms, tmp, null, null);
      var decompressedPath = Path.Combine(tmp, "decompressed_payload.bin");
      Assert.That(File.Exists(decompressedPath), Is.True, "decompressed_payload.bin should be emitted");
      Assert.That(File.ReadAllBytes(decompressedPath), Is.EqualTo(original).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // ── LE16 / 8-bit width-variant decoding via UPX descriptor ─────────────
  //
  // Each of these tests embeds a width-matched payload (built by
  // CompressBare(data, widthBytes)) into a synthetic UPX-shaped binary with the
  // matching method byte set in the PackHeader, then asserts that the
  // descriptor's TryDecompress dispatch picks the correct width helper and
  // round-trips the plaintext into decompressed_payload.bin.

  private static void AssertWidthVariantRoundTrips(byte method, byte[] compressed, byte[] original) {
    var buf = BuildStructuralUpxPe(["UPX0", "UPX1", "UPX2"], entryRva: 0x9000);
    compressed.CopyTo(buf.AsSpan(0x1000));
    var headerOffset = 0x1000 + compressed.Length;
    PatchPackerHeader(buf, headerOffset, method: method,
      uLen: (uint)original.Length, cLen: (uint)compressed.Length);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(buf);
      new UpxFormatDescriptor().Extract(ms, tmp, null, null);
      var decompressedPath = Path.Combine(tmp, "decompressed_payload.bin");
      Assert.That(File.Exists(decompressedPath), Is.True,
        $"method {method}: decompressed_payload.bin should be emitted");
      Assert.That(File.ReadAllBytes(decompressedPath), Is.EqualTo(original).AsCollection,
        $"method {method}: round-tripped payload mismatch");
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2bLe16_Method4() {
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("UPX-2B-LE16 ", 24)));
    var compressed = Compression.Core.Dictionary.Nrv2b.Nrv2bBuildingBlock.CompressBare(original, refillWidthBytes: 2);
    AssertWidthVariantRoundTrips(method: 4, compressed, original);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2b8_Method6() {
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("UPX-2B-8 ", 24)));
    var compressed = Compression.Core.Dictionary.Nrv2b.Nrv2bBuildingBlock.CompressBare(original, refillWidthBytes: 1);
    AssertWidthVariantRoundTrips(method: 6, compressed, original);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2dLe16_Method5() {
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("UPX-2D-LE16 ", 24)));
    var compressed = Compression.Core.Dictionary.Nrv2d.Nrv2dBuildingBlock.CompressBare(original, refillWidthBytes: 2);
    AssertWidthVariantRoundTrips(method: 5, compressed, original);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2d8_Method7() {
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("UPX-2D-8 ", 24)));
    var compressed = Compression.Core.Dictionary.Nrv2d.Nrv2dBuildingBlock.CompressBare(original, refillWidthBytes: 1);
    AssertWidthVariantRoundTrips(method: 7, compressed, original);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2eLe16_Method9() {
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("UPX-2E-LE16 ", 24)));
    var compressed = Compression.Core.Dictionary.Nrv2e.Nrv2eBuildingBlock.CompressBare(original, refillWidthBytes: 2);
    AssertWidthVariantRoundTrips(method: 9, compressed, original);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_DecompressesNrv2e8_Method10() {
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("UPX-2E-8 ", 24)));
    var compressed = Compression.Core.Dictionary.Nrv2e.Nrv2eBuildingBlock.CompressBare(original, refillWidthBytes: 1);
    AssertWidthVariantRoundTrips(method: 10, compressed, original);
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_UnsupportedMethod_LeavesNoteInMetadata() {
    var buf = BuildStructuralUpxPe(["UPX0", "UPX1", "UPX2"], entryRva: 0x9000);
    // Method 15 is DEFLATE-packed UPX — still unported, so this surfaces a note.
    PatchPackerHeader(buf, 0x4FE0, method: 15 /* DEFLATE, not yet supported */);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(buf);
      new UpxFormatDescriptor().Extract(ms, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("[decompression]"));
      Assert.That(meta, Does.Contain("DEFLATE"));
      Assert.That(File.Exists(Path.Combine(tmp, "decompressed_payload.bin")), Is.False);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
