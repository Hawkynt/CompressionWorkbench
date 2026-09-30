#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Pst;

namespace Compression.Tests.Pst;

[TestFixture]
public class PstTests {

  // Specification-shaped header fixture. Unicode headers occupy 564 bytes;
  // ANSI headers occupy 512 bytes.
  private static byte[] MakeMinimalPst(ushort wVer, ulong rootBbt, ulong rootNbt, byte cryptMethod = 0) {
    var unicode = wVer >= 23;
    var blob = new byte[unicode ? 564 : 512];
    blob[0] = 0x21; blob[1] = 0x42; blob[2] = 0x44; blob[3] = 0x4E; // "!BDN"
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 0xDEADBEEFu);  // dwCRCPartial (reported, not verified)
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), 0x4D53);       // wMagicClient "SM"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(10), wVer);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(12), 19);          // wVerClient

    if (unicode) {
      // Unicode ROOT: NBT BREF at +36 and BBT BREF at +52.
      const int rootOff = 180;
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(rootOff + 44), rootNbt);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(rootOff + 60), rootBbt);
      blob[512] = 0x80; // bSentinel
    } else {
      // ANSI ROOT: NBT BREF at +20 and BBT BREF at +28.
      const int rootOff = 164;
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(rootOff + 24), (uint)rootNbt);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(rootOff + 32), (uint)rootBbt);
      blob[460] = 0x80; // bSentinel
    }
    blob[unicode ? 513 : 461] = cryptMethod;
    // Header CRCs per MS-PST 2.2.2.6, so the fixture passes the reader's integrity check.
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), PstFormatDescriptor.MsPstCrc(blob.AsSpan(8, 471)));
    if (unicode) BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(524), PstFormatDescriptor.MsPstCrc(blob.AsSpan(8, 516)));
    return blob;
  }

  // First 564 bytes of two real Outlook files from java-libpst's test resources
  // (https://github.com/rjohnsondev/java-libpst, Apache-2.0, src/test/resources): dist-list.pst
  // (Unicode, wVer 23, permute crypt) and example-2013.ost (Unicode 4K, wVer 36).
  private const string DistListPstHeader = "IUJETqsCGVlTTRcAEwABATQCAABMOFEAAAAAAAAAAAALDAAAAAAAAG0BAAAABAAADwQAABEEAAAEQAAAEgABAAgEAAAEQAAABEAAAAOAAAAABAAAAAQAAAAEAAAABAAAEQQAABEEAAARBAAABEAAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAABwQAAAAAAAABAAAAAAAAAAAkBAAAAAAAAEQAAAAAAACAOwIAAAAAAAAyAAAAAAAABwwAAAAAAAAAfAEAAAAAAAoMAAAAAAAAAKwAAAAAAAACAAAAAAAAAP8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAf/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////+AAQAA8BIAAAAAAABRQOZRA9EBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
  private const string Example2013OstHeader = "IUJETrJmbBtTTyQADAABAVwAAABl1CHSAAAAAAAAAADjGQAAAAAAAM8FAAAABAAAAAQAABUEAAAFQAAALwABAAIEAAAFQAAABUAAAA6AAAAABAAAAAQAAAAEAAAABAAAFQQAABUEAAAVBAAABUAAAAAEAAAABAAAAAQAABUEAAAVBAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAKgQAAAAAAAAKAAAAAAAAAACgAAEAAAAAACACAAAAAAAAFPsAAAAAAAAAAAAAAAAAtxkAAAAAAAAA0AgAAAAAAOIZAAAAAAAAAFAGAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAALDgAAAAAAAAZS59f1XpjAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

  [TestCase(DistListPstHeader, 23, 19, "0x01", 271360UL, 97280UL, 44032UL, TestName = "RealHeader_DistListPst")]
  [TestCase(Example2013OstHeader, 36, 12, "0x00", 16818176UL, 577536UL, 413696UL, TestName = "RealHeader_Example2013Ost")]
  public void List_GivenRealOutlookHeader_ThenFieldsAndCrcsMatch(string b64, int version, int client, string crypt, ulong eof, ulong nbt, ulong bbt) {
    using var ms = new MemoryStream(Convert.FromBase64String(b64));
    var ini = Encoding.UTF8.GetString(new PstFormatDescriptor().ExtractEntryToMemory(ms, "metadata.ini", null));
    Assert.Multiple(() => {
      Assert.That(ini, Does.Contain("format=unicode"));
      Assert.That(ini, Does.Contain($"version={version}\n").Or.Contain($"version={version}\r\n"));
      Assert.That(ini, Does.Contain($"version_client={client}"));
      Assert.That(ini, Does.Contain("header_crc_valid=true"));
      Assert.That(ini, Does.Contain($"crypt_method={crypt}"));
      Assert.That(ini, Does.Contain($"file_eof={eof}"));
      Assert.That(ini, Does.Contain($"root_nbt_offset={nbt}"));
      Assert.That(ini, Does.Contain($"root_bbt_offset={bbt}"));
    });
  }

  [TestCase(20, TestName = "Damage_InsidePartialCrcRange")]
  [TestCase(500, TestName = "Damage_InsideFullCrcRangeOnly")]
  public void List_GivenDamagedRealHeader_ThenPartialMetadataInsteadOfThrow(int offset) {
    var bytes = Convert.FromBase64String(DistListPstHeader);
    bytes[offset] ^= 0x01;
    using var ms = new MemoryStream(bytes);
    var entries = new PstFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.pst", "metadata.ini" }));
    var ini = Encoding.UTF8.GetString(new PstFormatDescriptor().ExtractEntryToMemory(ms, "metadata.ini", null));
    Assert.That(ini, Does.Contain("parse_status=partial"));
    Assert.That(ini, Does.Contain("CRC"));
    var tmp = Path.Combine(Path.GetTempPath(), "pst_bad_" + Guid.NewGuid().ToString("N"));
    try {
      Assert.Throws<InvalidDataException>(() => new PstFormatDescriptor().Extract(new MemoryStream(bytes), tmp, null, null));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_UnicodePst_ReturnsCanonicalEntries() {
    var data = MakeMinimalPst(0x17, rootBbt: 0x1000UL, rootNbt: 0x2000UL);
    using var ms = new MemoryStream(data);
    var entries = new PstFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.pst"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True);

    var headerEntry = entries.Single(e => e.Name == "header.bin");
    Assert.That(headerEntry.OriginalSize, Is.EqualTo(564));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_UnicodePst_WritesFilesAndMetadata() {
    var data = MakeMinimalPst(0x17, rootBbt: 0x1000UL, rootNbt: 0x2000UL);
    var tmp = Path.Combine(Path.GetTempPath(), "pst_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PstFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.pst")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "header.bin")), Is.True);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=unicode"));
      Assert.That(ini, Does.Contain("version=23"));
      Assert.That(ini, Does.Contain("root_bbt_offset=4096"));
      Assert.That(ini, Does.Contain("root_nbt_offset=8192"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_AnsiPst_RecognizedAsAnsi() {
    var data = MakeMinimalPst(0x0E, rootBbt: 0x100UL, rootNbt: 0x200UL);
    var tmp = Path.Combine(Path.GetTempPath(), "pst_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PstFormatDescriptor().Extract(ms, tmp, null, null);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=ansi"));
      Assert.That(ini, Does.Contain("version=14"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("Regression")]
  [TestCase((ushort)14)]
  [TestCase((ushort)15)]
  public void AnsiVersions14And15UseTheAnsiHeaderLayout(ushort version) {
    var data = MakeMinimalPst(version, rootBbt: 0x100, rootNbt: 0x200);
    using var ms = new MemoryStream(data);
    var entries = new PstFormatDescriptor().List(ms, null);
    Assert.That(entries.Single(e => e.Name == "header.bin").OriginalSize, Is.EqualTo(512));
    Assert.That(Encoding.UTF8.GetString(new PstFormatDescriptor().ExtractEntryToMemory(ms, "metadata.ini", null)),
      Does.Contain("root_nbt_offset=512"));
  }

  [Category("Validation")]
  [Test]
  public void List_GivenUnsupportedVersionOrBadSentinel_ThenOnlyFullAndPartialMetadata() {
    var invalidVersion = MakeMinimalPst(21, 0, 0);
    using (var ms = new MemoryStream(invalidVersion))
      Assert.That(new PstFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "FULL.pst", "metadata.ini" }));

    var invalidSentinel = MakeMinimalPst(23, 0, 0);
    invalidSentinel[512] = 0;
    using var bad = new MemoryStream(invalidSentinel);
    Assert.That(new PstFormatDescriptor().List(bad, null).Select(e => e.Name), Is.EqualTo(new[] { "FULL.pst", "metadata.ini" }));
  }

  [TestCase(0, TestName = "Truncated_Empty")]
  [TestCase(100, TestName = "Truncated_InsideHeader")]
  [TestCase(563, TestName = "Truncated_OneByteShort")]
  public void List_GivenTruncatedHeader_ThenDoesNotThrow(int length) {
    var bytes = Convert.FromBase64String(DistListPstHeader).AsSpan(0, length).ToArray();
    using var ms = new MemoryStream(bytes);
    Assert.That(new PstFormatDescriptor().List(ms, null).Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicAndExtensions() {
    var d = new PstFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".pst"));
    Assert.That(d.Extensions, Does.Contain(".ost"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x21, 0x42, 0x44, 0x4E }));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.False, "nothing here writes a PST");
  }
}
