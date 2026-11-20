using System.Reflection;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

/// <summary>
/// SQX recovery records. External records Squeez CommandLine 5.2 wrote (<c>-rr1</c>, <c>-rr3</c>
/// with <c>-rre</c>) are reproduced byte for byte and repair archives we damage; the SQX SDK's
/// <c>DamagedArchive.sqx</c>, damaged as shipped, is repaired from its internal record. Internal
/// records SpeedCommander 22's sqc.exe wrote (<c>-rr2</c>, <c>-rr5</c>) are reproduced byte for
/// byte, and Squeez accepts archives we write with one. See ReferenceVectors/README.md.
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

  /// <summary>Offset of the internal record: the last block reading as one.</summary>
  private static int RecordOffset(byte[] archive) => archive.AsSpan().LastIndexOf("SQ4RD"u8) - 7;

  [TestCase("sc22_rr2.sqx", 2), TestCase("sc22_rr5.sqx", 5)]
  [Category("ThirdPartyVector")]
  public void Given_SqueezsInternalRecord_When_RecreatedFromTheDataInFrontOfIt_Then_ItIsByteForByteSqueezs(string archive, int level) {
    var data = Resource(archive);
    var at = RecordOffset(data);
    Assert.That(SqxRecovery.CreateInternal(data.AsSpan(0, at), level), Is.EqualTo(data[at..]));
  }

  [TestCase("sc22_rr2.sqx"), TestCase("sc22_rr5.sqx")]
  [Category("ThirdPartyVector")]
  public void Given_AnArchiveWithSqueezsInternalRecord_When_Read_Then_EveryFilePassesItsCrc(string archive) {
    using var reader = new SqxReader(new MemoryStream(Resource(archive)));
    Assert.That(reader.HasRecoveryRecord, Is.True);
    Assert.That(reader.Entries.Select(e => e.FileName), Is.EquivalentTo(new[] { "text.txt", "noise.bin" }));
    foreach (var e in reader.Entries)
      Assert.That(reader.ExtractEntry(e), Has.Length.EqualTo(e.OriginalSize), e.FileName);
  }

  private static (byte[] Archive, byte[] Text, byte[] Noise) OursWithRecord(int level) {
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => "word" + i % 37 + " ")));
    var noise = new byte[30_000];
    new Random(41).NextBytes(noise);
    var writer = new SqxWriter(recoveryLevel: level);
    writer.AddFile("text.txt", text);
    writer.AddFile("noise.bin", noise);
    return (writer.ToArray(), text, noise);
  }

  [TestCase(1), TestCase(3), TestCase(100)]
  [Category("RoundTrip")]
  public void Given_OurArchiveWithAnInternalRecord_When_DamagedAndRepaired_Then_ItIsRestoredAndReads(int level) {
    var (archive, text, noise) = OursWithRecord(level);
    var at = RecordOffset(archive);
    Assert.That(SqxRecoveryRecord.TryRead(archive.AsSpan(at), out var record), Is.True);
    Assert.That((record.Level, record.DataSize, at + record.Length), Is.EqualTo(((uint)level, (long)at, (long)archive.Length)), "the record ends the archive and protects what precedes it");
    var damaged = archive.ToArray();
    damaged[100] ^= 0x5A;
    Assert.That(SqxRecovery.Repair(damaged).Repaired, Is.True);
    Assert.That(damaged, Is.EqualTo(archive));
    using var reader = new SqxReader(new MemoryStream(archive));
    Assert.That(reader.HasRecoveryRecord, Is.True);
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "text.txt")), Is.EqualTo(text));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "noise.bin")), Is.EqualTo(noise));
  }

  [TestCase(-1), TestCase(101)]
  [Category("Boundary")]
  public void Given_AnOutOfRangeRecoveryLevel_When_TheWriterIsMade_Then_Refused(int level)
    => Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SqxWriter(recoveryLevel: level));

  /// <summary>
  /// Squeez CommandLine 5.2 for Linux (CWB_SQC) tests and extracts our archive with its internal
  /// record, and refuses it once the record's header CRC is broken, so it does read the record.
  /// (Its repair command answers "Missing argument" to every form, here and on Squeez's own
  /// archives, so it cannot be the repair oracle; the record's body is checked byte for byte against
  /// Squeez's in the test above.)
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void Given_OurArchiveWithAnInternalRecord_When_SqueezTestsAndExtractsIt_Then_ItAcceptsItAndReadsTheRecord() {
    var sqc = Environment.GetEnvironmentVariable("CWB_SQC");
    if (string.IsNullOrEmpty(sqc)) Assert.Ignore("Set CWB_SQC to Squeez CommandLine 5.2 for Linux (sqc_linux.tgz of the SQX SDK mirror).");
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    var (archive, text, noise) = OursWithRecord(3);
    var brokenHeader = archive.ToArray();
    brokenHeader[RecordOffset(archive)] ^= 1;
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sqcrr_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      File.WriteAllBytes(Path.Combine(tmp, "ours.sqx"), archive);
      File.WriteAllBytes(Path.Combine(tmp, "broken.sqx"), brokenHeader);
      var dir = OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(tmp) : tmp;
      var l = $"/tmp/cwb_sqcrr_{Guid.NewGuid():N}";
      var run = FsInteropToolbox.RunWsl(
        $"mkdir -p {l}/out && cp '{dir}/ours.sqx' '{dir}/broken.sqx' {l}/ && cd {l} && " +
        $"timeout 60 {sqc} t ours.sqx < /dev/null > /dev/null 2>&1; echo ours=$?; " +
        $"timeout 60 {sqc} t broken.sqx < /dev/null > /dev/null 2>&1; echo broken=$?; " +
        $"timeout 60 {sqc} x -q -o ours.sqx out/ < /dev/null > /dev/null 2>&1; cp out/* '{dir}/' ; cd / ; rm -rf {l}");
      Assert.That(run.StdOut, Does.Contain("ours=0"), $"sqc t on ours:\n{run.StdOut}\n{run.StdErr}");
      Assert.That(run.StdOut, Does.Contain("broken=4"), $"sqc t on ours with a broken record header:\n{run.StdOut}\n{run.StdErr}");
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "text.txt")), Is.EqualTo(text));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "noise.bin")), Is.EqualTo(noise));
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
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
