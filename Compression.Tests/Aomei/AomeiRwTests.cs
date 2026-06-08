using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Aomei;

namespace Compression.Tests.Aomei;

/// <summary>
/// R/W tests for the AOMEI BIFH/BIFT outer container, exercising the
/// implementation that ports docs/AOMEI_FORMAT_SPEC.md to working code:
/// BR_STANDARD_HEADER + BRCrc32 (zlib CRC-32) + INFO records + writer
/// round-trip.
/// </summary>
[TestFixture]
public class AomeiRwTests {

  // ─── BRCrc32 ───────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BrCrc32_KnownVectors_MatchZlibCrc32() {
    // Known zlib CRC-32 vectors — the spec confirms BRCrc32 == standard zlib CRC-32.
    Assert.That(BrCrc32.Compute([]), Is.EqualTo(0x00000000u));
    Assert.That(BrCrc32.Compute("a"u8), Is.EqualTo(0xE8B7BE43u));
    Assert.That(BrCrc32.Compute("123456789"u8), Is.EqualTo(0xCBF43926u));
  }

  [Test, Category("HappyPath")]
  public void BrCrc32_ZeroedField_ProducesStableValue() {
    var buf = new byte[16];
    new BrStandardHeader(16, 0x123, 0xDEADBEEF).Write(buf);
    var crc = BrCrc32.ComputeWithZeroedCrc(buf);
    Assert.That(crc, Is.Not.EqualTo(0u)); // sanity
    // Recompute on a buffer where the CRC field is *already* zero — should match.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 0);
    var direct = BrCrc32.Compute(buf);
    Assert.That(direct, Is.EqualTo(crc));
  }

  // ─── BR_STANDARD_HEADER ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BrStandardHeader_RoundTrip_PreservesFields() {
    var buf = new byte[12];
    new BrStandardHeader(0x1234, 0x107, 0xCAFEBABE, reserved: 0).Write(buf);
    var hdr = BrStandardHeader.Read(buf);
    Assert.That(hdr.Size, Is.EqualTo(0x1234u));
    Assert.That(hdr.Type, Is.EqualTo((ushort)0x107));
    Assert.That(hdr.Reserved, Is.EqualTo((ushort)0));
    Assert.That(hdr.Crc32, Is.EqualTo(0xCAFEBABEu));
  }

  [Test, Category("HappyPath")]
  public void BrStandardHeader_SealAndVerify_RoundTrips() {
    var buf = new byte[32];
    // Fill body with arbitrary bytes
    for (var i = 12; i < buf.Length; ++i) buf[i] = (byte)(i ^ 0x55);
    new BrStandardHeader(32, 0xF001, 0).Write(buf);
    var sealedCrc = BrStandardHeader.SealCrc(buf);
    Assert.That(sealedCrc, Is.Not.EqualTo(0u));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    // Tamper a single body byte — CRC must reject.
    buf[20] ^= 0xFF;
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.False);
  }

  [Test, Category("Boundary")]
  public void BrStandardHeader_ShortBuffer_Throws() {
    Assert.Throws<ArgumentException>(() => BrStandardHeader.Read(new byte[8]));
    Assert.Throws<ArgumentException>(() => new BrStandardHeader(0, 0, 0).Write(new byte[8]));
    Assert.Throws<ArgumentException>(() => BrCrc32.ComputeWithZeroedCrc(new byte[8]));
  }

  // ─── BIFH / BIFT ───────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BrFileHead_BuildEmpty_HasCorrectFlagSizeAndCrc() {
    var buf = BrFileHead.BuildEmpty();
    Assert.That(buf.Length, Is.EqualTo(AomeiConstants.BifhSize));
    // Flag, Size and ASCII magic must all line up.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)), Is.EqualTo(AomeiConstants.BifhFlag));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)), Is.EqualTo((uint)AomeiConstants.BifhSize));
    Assert.That(buf[..5], Is.EqualTo(AomeiConstants.BifhMagicAscii));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    var head = BrFileHead.Read(buf);
    Assert.That(head.MagicAndSizeValid, Is.True);
  }

  [Test, Category("HappyPath")]
  public void BrFileTail_BuildEmpty_HasCorrectFlagSizeAndCrc() {
    var buf = BrFileTail.BuildEmpty();
    Assert.That(buf.Length, Is.EqualTo(AomeiConstants.BiftSize));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)), Is.EqualTo(AomeiConstants.BiftFlag));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)), Is.EqualTo((uint)AomeiConstants.BiftSize));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    // BrFileTail.Read reads the last BiftSize bytes — synthesise a buffer that
    // has those bytes at the right position.
    var image = new byte[AomeiConstants.BifhSize + AomeiConstants.BiftSize];
    buf.CopyTo(image, AomeiConstants.BifhSize);
    var tail = BrFileTail.Read(image);
    Assert.That(tail.MagicAndSizeValid, Is.True);
  }

  // ─── INFO record encoding ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void InfoRecord_BuildCompress_RoundTrips() {
    var buf = AomeiInfoRecord.BuildCompress(method: 1u, level: 6u);
    Assert.That(buf.Length, Is.EqualTo(0x18));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    var hdr = BrStandardHeader.Read(buf);
    Assert.That(hdr.Type, Is.EqualTo(AomeiConstants.InfoTypeImageCompress));
    Assert.That(hdr.Size, Is.EqualTo(0x18u));
    var record = new AomeiInfoRecord(hdr, true, buf.AsSpan(12).ToArray(), 0);
    Assert.That(record.TryGetCompressInfo(out var m, out var l), Is.True);
    Assert.That(m, Is.EqualTo(1u));
    Assert.That(l, Is.EqualTo(6u));
  }

  [Test, Category("HappyPath")]
  public void InfoRecord_BuildEncrypt_RoundTrips() {
    var buf = AomeiInfoRecord.BuildEncrypt(method: 2u, keyLen: 16u);
    Assert.That(buf.Length, Is.EqualTo(0x18));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    var hdr = BrStandardHeader.Read(buf);
    Assert.That(hdr.Type, Is.EqualTo(AomeiConstants.InfoTypeImageEncrypt));
    var record = new AomeiInfoRecord(hdr, true, buf.AsSpan(12).ToArray(), 0);
    Assert.That(record.TryGetEncryptInfo(out var m, out var k), Is.True);
    Assert.That(m, Is.EqualTo(2u));
    Assert.That(k, Is.EqualTo(16u));
  }

  [Test, Category("HappyPath")]
  public void InfoRecord_BuildPassword_StoresMd5OfUtf16Le() {
    // Per spec §7.1: interactive password's UTF-16LE bytes are MD5'd directly.
    var buf = AomeiInfoRecord.BuildPassword("Secret123");
    Assert.That(buf.Length, Is.EqualTo(0x20));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    var hdr = BrStandardHeader.Read(buf);
    Assert.That(hdr.Type, Is.EqualTo(AomeiConstants.InfoTypeImagePassword));
    var record = new AomeiInfoRecord(hdr, true, buf.AsSpan(12).ToArray(), 0);
    Assert.That(record.TryGetPasswordMd5(out var md5), Is.True);
    var expected = MD5.HashData(Encoding.Unicode.GetBytes("Secret123"));
    Assert.That(md5, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void InfoRecord_BuildBackupType_RoundTrips() {
    var buf = AomeiInfoRecord.BuildBackupType(kind: 42u);
    Assert.That(buf.Length, Is.EqualTo(0x14));
    Assert.That(BrStandardHeader.VerifyCrc(buf), Is.True);
    var hdr = BrStandardHeader.Read(buf);
    Assert.That(hdr.Type, Is.EqualTo(AomeiConstants.InfoTypeBackupType));
    var record = new AomeiInfoRecord(hdr, true, buf.AsSpan(12).ToArray(), 0);
    Assert.That(record.TryGetBackupType(out var k), Is.True);
    Assert.That(k, Is.EqualTo(42u));
  }

  [Test, Category("EquivalenceClass")]
  public void InfoRecord_TryGetXxx_RejectsWrongType() {
    var compress = AomeiInfoRecord.BuildCompress(1, 6);
    var hdr = BrStandardHeader.Read(compress);
    var rec = new AomeiInfoRecord(hdr, true, compress.AsSpan(12).ToArray(), 0);
    Assert.That(rec.TryGetEncryptInfo(out _, out _), Is.False);
    Assert.That(rec.TryGetPasswordMd5(out _), Is.False);
    Assert.That(rec.TryGetBackupType(out _), Is.False);
  }

  // ─── Writer + Reader round trip ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_EmptyContainer_RoundTripsThroughReader() {
    var writer = new AomeiWriter();
    var bytes = writer.Build();
    Assert.That(bytes.Length, Is.EqualTo(AomeiConstants.BifhSize + AomeiConstants.BiftSize));
    using var ms = new MemoryStream(bytes);
    var r = new AomeiReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.Head, Is.Not.Null);
    Assert.That(r.Tail, Is.Not.Null);
    Assert.That(r.HeadCrcValid, Is.True);
    Assert.That(r.TailCrcValid, Is.True);
    Assert.That(r.ParseStatus, Is.EqualTo("ok"));
    Assert.That(r.Records, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Writer_AllInfoRecords_RoundTrip() {
    var writer = new AomeiWriter {
      BackupTypeKind = 7u,
      CompressInfo = (1u, 6u),
      EncryptInfo = (2u, 16u),
      Password = "Hello",
    };
    var bytes = writer.Build();
    using var ms = new MemoryStream(bytes);
    var r = new AomeiReader(ms);
    Assert.That(r.HeadCrcValid, Is.True);
    Assert.That(r.TailCrcValid, Is.True);
    Assert.That(r.Records, Has.Count.EqualTo(4));
    Assert.That(r.BackupTypeKind, Is.EqualTo(7u));
    Assert.That(r.CompressMethod, Is.EqualTo(1u));
    Assert.That(r.CompressLevel, Is.EqualTo(6u));
    Assert.That(r.EncryptMethod, Is.EqualTo(2u));
    Assert.That(r.EncryptKeyLen, Is.EqualTo(16u));
    Assert.That(r.PasswordMd5, Is.Not.Null);
    Assert.That(r.PasswordMd5, Is.EqualTo(MD5.HashData(Encoding.Unicode.GetBytes("Hello"))));
    foreach (var rec in r.Records)
      Assert.That(rec.CrcValid, Is.True, $"record at offset 0x{rec.FileOffset:X} failed CRC");
  }

  [Test, Category("HappyPath")]
  public void Writer_UserData_IsExtractableThroughReader() {
    var writer = new AomeiWriter {
      UserData = [
        ("hello.txt", Encoding.UTF8.GetBytes("Hello, AOMEI!")),
        ("payload.bin", new byte[] { 1, 2, 3, 4, 5 }),
      ],
    };
    var bytes = writer.Build();
    using var ms = new MemoryStream(bytes);
    var r = new AomeiReader(ms);
    Assert.That(r.Records, Has.Count.EqualTo(2));
    Assert.That(r.Records[0].Header.Type, Is.EqualTo(AomeiWriter.UserDataTypeTag));
    Assert.That(AomeiWriter.ReadUserDataName(r.Records[0].Body), Is.EqualTo("hello.txt"));
    Assert.That(AomeiWriter.ReadUserDataPayload(r.Records[0].Body),
                Is.EqualTo(Encoding.UTF8.GetBytes("Hello, AOMEI!")));
    Assert.That(AomeiWriter.ReadUserDataName(r.Records[1].Body), Is.EqualTo("payload.bin"));
    Assert.That(AomeiWriter.ReadUserDataPayload(r.Records[1].Body),
                Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  // ─── Descriptor Create / List / Extract round trip ─────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesValidBifhContainer() {
    var d = new AomeiFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.txt", Encoding.UTF8.GetBytes("hello")),
      ArchiveInputInfo.InMemory("doc.bin", new byte[] { 0xAA, 0xBB, 0xCC }),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions {
      Password = "swordfish",
      FormatSpecific = new Dictionary<string, string> {
        ["backup_type"] = "3",
        ["compress_method"] = "1",
        ["compress_level"] = "5",
        ["encrypt_method"] = "2",
        ["encrypt_key_len"] = "16",
      },
    });
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.bifh"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("head.bin"));
    Assert.That(names, Does.Contain("tail.bin"));
    Assert.That(names, Does.Contain("userdata/readme.txt"));
    Assert.That(names, Does.Contain("userdata/doc.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_Extract_RoundTripsUserData() {
    var d = new AomeiFormatDescriptor();
    var helloBytes = Encoding.UTF8.GetBytes("hello AOMEI");
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.txt", helloBytes),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "aomei_rw_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(outDir, "userdata", "readme.txt"));
      Assert.That(extracted, Is.EqualTo(helloBytes));
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("head_crc_valid=true"));
      Assert.That(meta, Does.Contain("tail_crc_valid=true"));
      Assert.That(meta, Does.Contain("record_count=1"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_Extract_SurfacesPasswordMd5InMetadata() {
    var d = new AomeiFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions { Password = "swordfish" });
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "aomei_pw_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      var expectedMd5 = MD5.HashData(Encoding.Unicode.GetBytes("swordfish"));
      var hex = new StringBuilder();
      foreach (var b in expectedMd5) hex.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:x2}", b);
      Assert.That(meta, Does.Contain("password_md5=" + hex));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ─── Tamper detection ──────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Reader_CorruptHead_DetectsCrcFailure() {
    var writer = new AomeiWriter();
    var bytes = writer.Build();
    // Flip a byte inside the head body but not at the CRC field (offset 8..11).
    bytes[100] ^= 0xFF;
    using var ms = new MemoryStream(bytes);
    var r = new AomeiReader(ms);
    Assert.That(r.HeadCrcValid, Is.False);
    Assert.That(r.ParseStatus, Is.EqualTo("magic_ok_crc_failed"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_CorruptRecord_FlagsBadCrcButContinues() {
    var writer = new AomeiWriter { BackupTypeKind = 1u, CompressInfo = (1u, 1u) };
    var bytes = writer.Build();
    // The first record starts at offset BifhSize. Flip a body byte.
    bytes[AomeiConstants.BifhSize + 14] ^= 0xFF;
    using var ms = new MemoryStream(bytes);
    var r = new AomeiReader(ms);
    Assert.That(r.Records, Has.Count.EqualTo(2));
    Assert.That(r.Records[0].CrcValid, Is.False);
    Assert.That(r.Records[1].CrcValid, Is.True);
  }

  // ─── Boundary cases ────────────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Reader_TruncatedTail_StaysHeadOnly() {
    var writer = new AomeiWriter();
    var bytes = writer.Build();
    // Drop the tail.
    var truncated = bytes.AsSpan(0, AomeiConstants.BifhSize).ToArray();
    using var ms = new MemoryStream(truncated);
    var r = new AomeiReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.Head, Is.Not.Null);
    Assert.That(r.Tail, Is.Null);
    Assert.That(r.ParseStatus, Is.EqualTo("tail_missing"));
  }

  [Test, Category("Boundary")]
  public void Reader_ShortHeader_DegradesGracefully() {
    // Magic + a few bytes — not enough for a full BIFH.
    var bytes = new byte[100];
    AomeiConstants.BifhMagicAscii.CopyTo(bytes, 0);
    using var ms = new MemoryStream(bytes);
    var r = new AomeiReader(ms);
    Assert.That(r.Valid, Is.True); // magic detected
    Assert.That(r.Head, Is.Null);
    Assert.That(r.ParseStatus, Is.EqualTo("header_short"));
  }

  [Test, Category("Boundary")]
  public void Writer_ZeroSizeRecord_DoesNotInfiniteLoop() {
    // Synthesise a corrupted record with Size=0 — the reader must not loop.
    using var ms = new MemoryStream();
    ms.Write(BrFileHead.BuildEmpty());
    var bogus = new byte[12]; // Size=0 by default
    ms.Write(bogus);
    ms.Write(BrFileTail.BuildEmpty());
    ms.Position = 0;
    var r = new AomeiReader(ms);
    // The walker stops at the bogus record — no records logged.
    Assert.That(r.Records, Has.Count.EqualTo(0));
  }

  [Test, Category("EquivalenceClass")]
  public void Writer_TruncatesLongUserDataName() {
    // 31 ASCII chars + NUL fits exactly; 32 chars overflow.
    var longName = new string('x', 64);
    var record = AomeiWriter.BuildUserDataRecord(longName, [1, 2, 3]);
    var body = record.AsSpan(AomeiConstants.StandardHeaderSize).ToArray();
    var name = AomeiWriter.ReadUserDataName(body);
    Assert.That(name.Length, Is.LessThanOrEqualTo(AomeiWriter.UserDataNameLength - 1));
    Assert.That(name, Is.EqualTo(new string('x', AomeiWriter.UserDataNameLength - 1)));
    Assert.That(AomeiWriter.ReadUserDataPayload(body), Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new AomeiFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }
}
