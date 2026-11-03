using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Recovery records: rar-made ones verify and repair here, ours verify and repair here and in rar
/// (<c>rar t</c>, <c>rar r</c>). The layouts are in docs/RAR-RECOVERY-ON-DISK.md.
/// </summary>
[TestFixture]
public class RarRecoveryTests {
  private static byte[] Fixture(string name) => File.ReadAllBytes(RarConformanceSet.PathOf(RarConformanceSet.Modern, name));

  private static byte[] Mixed(int length, int seed) {
    var rnd = new Random(seed);
    var data = new byte[length];
    rnd.NextBytes(data);
    for (var i = 0; i < length; i += 7) data[i] = (byte)'a';
    return data;
  }

  private static byte[] Damage(byte[] archive, long at, int length, byte xor = 0x5A) {
    var d = (byte[])archive.Clone();
    for (var i = 0; i < length; ++i) d[at + i] ^= xor;
    return d;
  }

  private static (RarRecoveryReport Report, byte[] Output) Repair(byte[] damaged) {
    using var src = new MemoryStream(damaged, writable: false);
    using var dst = new MemoryStream();
    var report = RarRecovery.Repair(src, dst);
    return (report, dst.ToArray());
  }

  private static byte[] Ours5(int percent, byte[] data, string? password = null, bool encryptHeaders = false) {
    using var ms = new MemoryStream();
    using (var w = new RarWriter(ms, leaveOpen: true, method: 0, password: password, encryptHeaders: encryptHeaders, recoveryPercent: percent))
      w.AddFile("data.bin", data);
    return ms.ToArray();
  }

  private static byte[] Ours4(int percent, byte[] data) {
    using var ms = new MemoryStream();
    using (var w = new Rar4Writer(ms, leaveOpen: true, method: RarConstants.Rar4MethodStore, recoveryPercent: percent))
      w.AddFile("data.bin", data);
    return ms.ToArray();
  }

  // ---- rar-made records ----

  [TestCase("r5-rr.rar", RarRecoveryKind.Rar5Record)]
  [TestCase("r4-rr.rar", RarRecoveryKind.Rar4Record)]
  public void Given_ARecordRarWrote_When_Verified_Then_ItIsIntact(string name, RarRecoveryKind kind) {
    using var ms = new MemoryStream(Fixture(name));
    var r = RarRecovery.Verify(ms);
    Assert.That(r.Kind, Is.EqualTo(kind), r.Message);
    Assert.That(r.Intact, Is.True, r.Message);
  }

  [TestCase("r5-rr.rar")]
  [TestCase("r4-rr.rar")]
  public void Given_ARecordRarWrote_When_TheArchiveIsDamagedWithinItsReach_Then_RepairRestoresItByteExact(string name) {
    var original = Fixture(name);
    var damaged = Damage(original, 100, 40);
    using (var check = new MemoryStream(damaged)) Assert.That(RarRecovery.Verify(check).Intact, Is.False);
    var (report, output) = Repair(damaged);
    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Test]
  public void Given_NoRecord_When_Verified_Then_NoneIsReported() {
    using var ms = new MemoryStream(Fixture("r5-m0.rar"));
    Assert.That(RarRecovery.Verify(ms).Kind, Is.EqualTo(RarRecoveryKind.None));
  }

  // ---- our records ----

  [TestCase(1, 4_000)]           // one data shard, one recovery block
  [TestCase(5, 70_000)]
  [TestCase(10, 300_000)]        // more than 200 KB: 200 shards of 1.5 KB
  public void Given_OurRar5Record_When_DamageFitsTheRecord_Then_RepairRestoresTheArchive(int percent, int size) {
    var data = Mixed(size, size);
    var original = Ours5(percent, data);
    using (var check = new MemoryStream(original)) {
      var v = RarRecovery.Verify(check);
      Assert.That(v.Kind, Is.EqualTo(RarRecoveryKind.Rar5Record));
      Assert.That(v.Intact, Is.True, v.Message);
    }
    // As many shards as there are recovery blocks, one byte each.
    Rar5RecoveryRecord.Layout layout;
    using (var s = new MemoryStream(original)) layout = Rar5RecoveryRecord.Find(s)!;
    Assert.That(layout.Protected, Is.LessThan(original.Length));
    var damaged = (byte[])original.Clone();
    for (var k = 0; k < Math.Min(layout.RecoveryShards, layout.DataShards); ++k) damaged[k * layout.ShardSize + 50] ^= 0xFF;
    var (report, output) = Repair(damaged);
    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
    using var reader = new RarReader(new MemoryStream(output), leaveOpen: false);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Test]
  public void Given_OurRar5Record_When_MoreShardsAreDamagedThanItHasBlocks_Then_ItIsNotRepairable() {
    var original = Ours5(1, Mixed(100_000, 1));
    var damaged = Damage(original, 200, 1);
    damaged[5_000] ^= 1;          // a second shard, with one recovery block
    var (report, _) = Repair(damaged);
    Assert.That(report.Repairable, Is.False, report.Message);
    Assert.That(report.Repaired, Is.False);
  }

  [Test]
  public void Given_OurRar5Record_When_ARecoveryBlockIsDamaged_Then_RepairRebuildsIt() {
    var original = Ours5(10, Mixed(50_000, 2));
    var damaged = Damage(original, original.Length - 300, 10);   // inside the last block
    using (var check = new MemoryStream(damaged)) Assert.That(RarRecovery.Verify(check).Intact, Is.False);
    var (report, output) = Repair(damaged);
    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Test]
  public void Given_OurRar5RecordWithEncryptedHeaders_When_Damaged_Then_RepairRestoresIt() {
    var original = Ours5(5, Mixed(30_000, 3), "secret", encryptHeaders: true);
    var (report, output) = Repair(Damage(original, 500, 30));
    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [TestCase(5, 70_000)]
  [TestCase(1, 1_000)]
  public void Given_OurRar4Record_When_ContiguousDamageFitsTheParity_Then_RepairRestoresTheArchive(int percent, int size) {
    var data = Mixed(size, size);
    var original = Ours4(percent, data);
    using (var check = new MemoryStream(original)) {
      var v = RarRecovery.Verify(check);
      Assert.That(v.Kind, Is.EqualTo(RarRecoveryKind.Rar4Record));
      Assert.That(v.Intact, Is.True, v.Message);
    }
    Rar4RecoveryRecord.Layout layout;
    using (var s = new MemoryStream(original)) layout = Rar4RecoveryRecord.Find(s)!;
    // Contiguous damage over as many sectors as there are parity sectors.
    var length = (int)Math.Min(layout.RecoverySectors * 512L, layout.Protected - 60) - 80;
    var damaged = Damage(original, 60, length);
    var (report, output) = Repair(damaged);
    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Test]
  public void Given_OurRar4Record_When_TwoSectorsShareAParitySector_Then_ItIsNotRepairable() {
    var original = Ours4(1, Mixed(70_000, 4));
    Rar4RecoveryRecord.Layout layout;
    using (var s = new MemoryStream(original)) layout = Rar4RecoveryRecord.Find(s)!;
    var damaged = Damage(original, 100, 1);
    damaged[100 + 512 * layout.RecoverySectors] ^= 1;   // the next sector of the same parity group
    var (report, _) = Repair(damaged);
    Assert.That(report.Repairable, Is.False, report.Message);
  }

  [Test]
  public void Given_ARecordRequest_When_TheOutputCannotBeReadBack_Then_TheWriterRefuses() {
    Assert.That(() => new RarWriter(new WriteOnlyStream(), recoveryPercent: 5), Throws.TypeOf<NotSupportedException>());
    Assert.That(() => new Rar4Writer(new WriteOnlyStream(), recoveryPercent: 5), Throws.TypeOf<NotSupportedException>());
    Assert.That(() => new RarWriter(new MemoryStream(), recoveryPercent: 5, volumeSize: 4096, openVolume: _ => new MemoryStream()),
      Throws.TypeOf<NotSupportedException>());
    Assert.That(() => new RarWriter(new MemoryStream(), recoveryPercent: 101), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  private sealed class WriteOnlyStream : MemoryStream {
    public override bool CanRead => false;
  }

  // ---- rar against ours, ours against rar (tool-gated) ----

  private static string RequireRar(int generation) {
    var exe = generation == 5 ? RarTools.Rar : RarTools.Rar4 ?? RarTools.Rar;
    if (exe == null) Assert.Ignore("Set CWB_TOOL_RAR (and CWB_TOOL_RAR4 for -ma4) to run rar against the recovery records.");
    return exe!;
  }

  [TestCase(5)]
  [TestCase(4)]
  [Category("ExternalInterop")]
  public void Given_OurRecord_When_RarTestsAndRepairsIt_Then_RarRestoresOurArchiveByteExact(int generation) {
    var rar = RequireRar(generation);
    var dir = RarTools.TempDir("rarrr");
    try {
      var original = generation == 5 ? Ours5(5, Mixed(300_000, 9)) : Ours4(5, Mixed(300_000, 9));
      File.WriteAllBytes(Path.Combine(dir, "ours.rar"), original);
      var t = RarTools.Run(rar, ["t", "-cfg-", "ours.rar"], dir);
      Assert.That(t.ExitCode, Is.Zero, t.Output);
      if (generation == 5) Assert.That(t.Output, Does.Contain("recovery record").IgnoreCase, t.Output);

      var damaged = generation == 5 ? Damage(original, 2_000, 1_000) : Damage(original, 2_000, 1_000);
      File.WriteAllBytes(Path.Combine(dir, "dmg.rar"), damaged);
      var r = RarTools.Run(rar, ["r", "-cfg-", "-y", "dmg.rar"], dir);
      var fixedPath = Directory.GetFiles(dir, "*dmg.rar").FirstOrDefault(p => !Path.GetFileName(p).Equals("dmg.rar", StringComparison.OrdinalIgnoreCase));
      Assert.That(fixedPath, Is.Not.Null, r.Output);
      Assert.That(File.ReadAllBytes(fixedPath!), Is.EqualTo(original), r.Output);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase(5, "-rr5%")]
  [TestCase(5, "-rr20%")]
  [TestCase(4, "-rr5%")]
  [Category("ExternalInterop")]
  public void Given_ARecordRarJustWrote_When_WeRepairDamageWithinItsReach_Then_TheArchiveIsRarsByteExact(int generation, string rr) {
    var rar = RequireRar(generation);
    var dir = RarTools.TempDir("rarrr2");
    try {
      File.WriteAllBytes(Path.Combine(dir, "in.bin"), Mixed(500_000, 11));
      var args = new List<string> { "a", "-cfg-", "-idq", "-y", "-m0", rr, generation == 5 ? "-ma5" : "-ma4", "theirs.rar", "in.bin" };
      var a = RarTools.Run(rar, args, dir);
      Assert.That(a.ExitCode, Is.Zero, a.Output);
      var original = File.ReadAllBytes(Path.Combine(dir, "theirs.rar"));
      var damaged = Damage(original, 10_000, generation == 5 ? 2_000 : 1_500);
      var (report, output) = Repair(damaged);
      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(output, Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }
}
