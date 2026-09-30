#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Zip;
using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP64 decisions of the central-directory and end-record writers that
/// <see cref="ZipModifier"/> uses after every add: the all-ones values are sentinels, so
/// a value equal to one already needs the 64-bit form.
/// </summary>
[TestFixture]
public class ZipZip64SentinelTests {

  private const int CdFlags = 8;
  private const int CdUncompressedSize = 24;
  private const int CdExtraLength = 30;
  private const int CdLocalHeaderOffset = 42;
  private const int CdFixedLength = 46;

  // ── ZIP64 sentinel boundaries (APPNOTE.TXT 4.4.8/4.4.9/4.4.16: 0xFFFFFFFF means "see ZIP64") ──

  [TestCase(0xFFFFFFFEL, false, TestName = "CentralDirectory_GivenSizeOneBelowSentinel_ThenNo64BitField")]
  [TestCase(0xFFFFFFFFL, true, TestName = "CentralDirectory_GivenSizeEqualToSentinel_ThenUses64BitField")]
  [TestCase(0x100000000L, true, TestName = "CentralDirectory_GivenSizeAboveSentinel_ThenUses64BitField")]
  [Category("Contract")]
  public void CentralDirectory_GivenUncompressedSize_ThenZip64DecisionHonoursSentinel(long size, bool expectZip64) {
    var entry = new ZipEntry { FileName = "e", UncompressedSize = size, CompressedSize = 10 };

    var record = WriteCentral(entry);

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(CdUncompressedSize)) == 0xFFFFFFFFu, Is.EqualTo(expectZip64));
    Assert.That(ReadCentral(record).UncompressedSize, Is.EqualTo(size));
  }

  [TestCase(0xFFFFFFFEL, false, TestName = "CentralDirectory_GivenOffsetOneBelowSentinel_ThenNo64BitField")]
  [TestCase(0xFFFFFFFFL, true, TestName = "CentralDirectory_GivenOffsetEqualToSentinel_ThenUses64BitField")]
  [TestCase(0x1_0000_1000L, true, TestName = "CentralDirectory_GivenOffsetAboveFourGiB_ThenUses64BitField")]
  [Category("Contract")]
  public void CentralDirectory_GivenLocalHeaderOffset_ThenZip64DecisionHonoursSentinel(long offset, bool expectZip64) {
    var entry = new ZipEntry { FileName = "e", LocalHeaderOffset = offset };

    var record = WriteCentral(entry);

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(CdLocalHeaderOffset)) == 0xFFFFFFFFu, Is.EqualTo(expectZip64));
    Assert.That(ReadCentral(record).LocalHeaderOffset, Is.EqualTo(offset));
  }

  [TestCase(0xFFFE, false, TestName = "EndRecord_GivenCountOneBelowSentinel_ThenNoZip64Record")]
  [TestCase(0xFFFF, true, TestName = "EndRecord_GivenCountEqualToSentinel_ThenZip64Record")]
  [Category("Contract")]
  public void EndRecord_GivenEntryCount_ThenZip64DecisionHonoursSentinel(int count, bool expectZip64) {
    using var ms = new MemoryStream();
    using (var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true))
      ZipEndOfCentralDirectory.Write(w, 0, 0, count, null);

    var signature = BinaryPrimitives.ReadUInt32LittleEndian(ms.ToArray());
    Assert.That(signature == ZipConstants.Zip64EndOfCentralDirectorySignature, Is.EqualTo(expectZip64));
    Assert.That(ZipEndOfCentralDirectory.Read(ms).Count, Is.EqualTo(count));
  }

  [TestCase(0xFFFD, false, TestName = "AddFile_GivenArchiveReachingOneBelowCountSentinel_ThenEndRecordStays32Bit")]
  [TestCase(0xFFFE, true, TestName = "AddFile_GivenArchiveReachingCountSentinel_ThenEndRecordSwitchesToZip64")]
  [Category("RoundTrip")]
  public void AddFile_GivenEntryCountAtSentinelBoundary_ThenEndRecordHonoursSentinel(int existing, bool expectZip64) {
    using var zip = new MemoryStream();
    var w = new ZipWriter(zip, leaveOpen: true);
    for (var i = 0; i < existing; ++i)
      w.AddEntry($"f{i}", [], ZipCompressionMethod.Store);
    w.Finish();

    ZipModifier.AddFile(zip, "added.txt", "x"u8.ToArray());

    var bytes = zip.ToArray();
    var hasZip64End = bytes.AsSpan().IndexOf(stackalloc byte[] { 0x50, 0x4B, 0x06, 0x06 }) >= 0;
    Assert.That(hasZip64End, Is.EqualTo(expectZip64));
    zip.Position = 0;
    using var check = new SysZipArchive(zip, SysZipArchiveMode.Read, leaveOpen: true);
    Assert.That(check.Entries, Has.Count.EqualTo(existing + 1));
    Assert.That(check.Entries[^1].FullName, Is.EqualTo("added.txt"));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static byte[] WriteCentral(ZipEntry entry) {
    using var ms = new MemoryStream();
    using (var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true))
      ZipCentralDirectoryEntry.Write(w, entry);
    return ms.ToArray();
  }

  private static ZipEntry ReadCentral(byte[] record) {
    using var r = new BinaryReader(new MemoryStream(record), Encoding.Latin1);
    return ZipCentralDirectoryEntry.Read(r);
  }
}
