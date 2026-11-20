using System.Reflection;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

/// <summary>
/// SQX recovery records. External records Squeez CommandLine 5.2 wrote (<c>-rr1</c>, <c>-rr3</c>
/// with <c>-rre</c>) are reproduced byte for byte and repair archives we damage; the SQX SDK's
/// <c>DamagedArchive.sqx</c>, damaged as shipped, is repaired from its internal record. See
/// ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public sealed class SqxRecoveryTests {
  private static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SqxRecovery." + name)
      ?? throw new InvalidOperationException("Missing resource SqxRecovery." + name);
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  [TestCase("a3000_1.sqx", 1), TestCase("a60000_3.sqx", 3)]
  [Category("ThirdPartyVector")]
  public void Given_AnArchive_When_ItsExternalRecordIsCreated_Then_ItIsByteForByteSqueezs(string archive, int level)
    => Assert.That(SqxRecovery.CreateExternal(Resource(archive), level), Is.EqualTo(Resource(archive + ".sqr")));

  [Test, Category("ThirdPartyVector")]
  public void Given_DamageInSeveralParityGroups_When_RepairedFromSqueezsExternalRecord_Then_TheArchiveIsRestored() {
    var original = Resource("a60000_3.sqx");
    var damaged = original.ToArray();
    damaged[5000] ^= 0xFF;       // block 9
    damaged[30000] ^= 0x55;      // block 58
    Array.Clear(damaged, 512 * 100, 512);   // block 100, a whole zeroed sector
    var report = SqxRecovery.Repair(damaged, Resource("a60000_3.sqx.sqr"));
    Assert.That((report.Damaged, report.Repaired), Is.EqualTo((3, true)), report.Message);
    Assert.That(damaged, Is.EqualTo(original));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_TheSdksDamagedArchive_When_RepairedFromItsInternalRecord_Then_EveryFilePassesItsCrc() {
    var archive = Resource("sdk_DamagedArchive.sqx");
    using (var reader = new SqxReader(new MemoryStream(archive))) {
      Assert.That(reader.HasRecoveryRecord, Is.True);
      Assert.That(reader.Entries, Has.Count.EqualTo(10));
      Assert.Throws<InvalidDataException>(() => {
        foreach (var e in reader.Entries) reader.ExtractEntry(e);
      });
    }
    var report = SqxRecovery.Repair(archive);
    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(report.Damaged, Is.GreaterThan(0));
    using var repaired = new SqxReader(new MemoryStream(archive));
    foreach (var e in repaired.Entries)
      Assert.That(repaired.ExtractEntry(e), Has.Length.EqualTo(e.OriginalSize), e.FileName);
  }

  [Test, Category("HappyPath")]
  public void Given_OurOwnArchive_When_RecordedDamagedAndRepaired_Then_ItIsRestored() {
    var writer = new SqxWriter();
    writer.AddFile("a.bin", Enumerable.Range(0, 40_000).Select(i => (byte)(i * 7 ^ i >> 5)).ToArray());
    var archive = writer.ToArray();
    var record = SqxRecovery.CreateExternal(archive, 5);
    var damaged = archive.ToArray();
    damaged[^3] ^= 1;   // the last, short block
    Assert.That(SqxRecovery.Repair(damaged, record).Repaired, Is.True);
    Assert.That(damaged, Is.EqualTo(archive));
    Assert.That(SqxRecovery.Repair(damaged, record).Message, Does.Contain("intact"));
  }

  [Test, Category("Exception")]
  public void Given_TwoDamagedBlocksInOneParityGroup_When_Repaired_Then_NothingIsChanged() {
    var original = Resource("a60000_3.sqx");   // 4 parity blocks: blocks 1 and 5 share a group
    var damaged = original.ToArray();
    damaged[512 * 1 + 3] ^= 1;
    damaged[512 * 5 + 3] ^= 1;
    var before = damaged.ToArray();
    var report = SqxRecovery.Repair(damaged, Resource("a60000_3.sqx.sqr"));
    Assert.That(report.Repaired, Is.False);
    Assert.That(damaged, Is.EqualTo(before));
  }

  [Test, Category("Exception")]
  public void Given_AMismatchedOrMissingRecord_When_Repaired_Then_Refused() {
    Assert.Throws<InvalidDataException>(() => SqxRecovery.Repair(Resource("a3000_1.sqx"), Resource("a60000_3.sqx.sqr")));
    Assert.Throws<InvalidDataException>(() => SqxRecovery.Repair(Resource("a3000_1.sqx")));
    var broken = Resource("a3000_1.sqx.sqr");
    broken[^1] ^= 1;
    Assert.Throws<InvalidDataException>(() => SqxRecovery.Repair(Resource("a3000_1.sqx"), broken));
    Assert.Throws<ArgumentOutOfRangeException>(() => SqxRecovery.CreateExternal([1], 0));
  }
}
