using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Shifting damage: bytes inserted into or lost from the protected part of an archive. rar's
/// <c>r</c> finds the units that moved ("relocated") and rebuilds the rest; measured with rar 7.12
/// and 6.24 to give back the original archive byte for byte, which is what these tests require of
/// <see cref="RarRecovery"/> and, tool-gated, of rar on our records.
/// </summary>
[TestFixture]
public class RarRecoveryShiftTests {

  private static byte[] Mixed(int length, int seed) {
    var rnd = new Random(seed);
    var data = new byte[length];
    rnd.NextBytes(data);
    for (var i = 0; i < length; i += 7) data[i] = (byte)'a';
    return data;
  }

  private static byte[] Ours(int generation, byte[] data, int percent = 5) {
    using var ms = new MemoryStream();
    if (generation == 5) {
      using var w = new RarWriter(ms, leaveOpen: true, method: 0, recoveryPercent: percent);
      w.AddFile("data.bin", data);
    } else {
      using var w = new Rar4Writer(ms, leaveOpen: true, method: RarConstants.Rar4MethodStore, recoveryPercent: percent);
      w.AddFile("data.bin", data);
    }
    return ms.ToArray();
  }

  private static byte[] Insert(byte[] b, int at, int count) {
    var filler = new byte[count];
    for (var i = 0; i < count; ++i) filler[i] = (byte)(i * 37 + 11);
    return [.. b[..at], .. filler, .. b[at..]];
  }

  private static byte[] Delete(byte[] b, int at, int count) => [.. b[..at], .. b[(at + count)..]];

  /// <summary>The edits rar was measured on: 100 inserted, 100 removed, and 3 inserted with 50 removed further on.</summary>
  private static byte[] Edit(byte[] b, string kind) => kind switch {
    "insert" => Insert(b, 30_000, 100),
    "delete" => Delete(b, 30_000, 100),
    "both" => Delete(Insert(b, 30_000, 3), 120_003, 50),
    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
  };

  private static (RarRecoveryReport Report, byte[] Output) Repair(byte[] damaged) {
    using var src = new MemoryStream(damaged, writable: false);
    using var dst = new MemoryStream();
    var report = RarRecovery.Repair(src, dst);
    return (report, dst.ToArray());
  }

  // ---- ours, repaired here ----

  [Category("RoundTrip")]
  [TestCase(5, "insert")]
  [TestCase(5, "delete")]
  [TestCase(5, "both")]
  [TestCase(4, "insert")]
  [TestCase(4, "delete")]
  [TestCase(4, "both")]
  public void Given_BytesInsertedOrRemoved_When_Repaired_Then_TheOriginalArchiveComesBack(int generation, string kind) {
    var original = Ours(generation, Mixed(250_000, 1));
    var damaged = Edit(original, kind);

    var (report, output) = Repair(damaged);

    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Category("Boundary")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_BytesInsertedRightAfterTheSignature_When_Repaired_Then_EveryUnitIsRelocated(int generation) {
    var original = Ours(generation, Mixed(60_000, 2));
    var damaged = Insert(original, 1, 1);

    var (report, output) = Repair(damaged);

    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Category("Boundary")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_BytesInsertedJustBeforeTheRecord_When_Repaired_Then_TheTailIsCutBack(int generation) {
    var original = Ours(generation, Mixed(60_000, 3));
    using var probe = new MemoryStream(original);
    var protectedEnd = generation == 5 ? Rar5RecoveryRecord.Find(probe)!.Protected : Rar4RecoveryRecord.Find(probe)!.Protected;
    var damaged = Insert(original, (int)protectedEnd - 1, 40);

    var (report, output) = Repair(damaged);

    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Category("Boundary")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_InsertionAndInPlaceDamageTogether_When_Repaired_Then_BothAreUndone(int generation) {
    var original = Ours(generation, Mixed(250_000, 4), percent: 10);
    var damaged = Insert(original, 20_000, 64);
    for (var i = 150_000; i < 150_300; ++i) damaged[i] ^= 0x5A;

    var (report, output) = Repair(damaged);

    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(original));
  }

  [Category("Exception")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_MoreLostThanTheRecordCovers_When_Repaired_Then_ItIsRefusedAndTheDataLeftAlone(int generation) {
    var original = Ours(generation, Mixed(250_000, 5), percent: 1);
    var damaged = Delete(original, 20_000, 60_000);

    var (report, output) = Repair(damaged);

    Assert.That(report.Repaired, Is.False, report.Message);
    Assert.That(output, Is.EqualTo(damaged));
  }

  [Test]
  public void Given_AShiftedArchive_When_Verified_Then_ItIsReportedDamagedButRepairable() {
    var damaged = Insert(Ours(5, Mixed(100_000, 6)), 5_000, 10);

    using var ms = new MemoryStream(damaged);
    var report = RarRecovery.Verify(ms);

    Assert.That(report.Kind, Is.EqualTo(RarRecoveryKind.Rar5Record));
    Assert.That(report.Intact, Is.False);
    Assert.That(report.Repairable, Is.True, report.Message);
    Assert.That(report.Message, Does.Contain("inserted or removed"));
  }

  [Category("RoundTrip")]
  [TestCase("r5-rr.rar")]
  [TestCase("r4-rr.rar")]
  public void Given_ARecordRarWrote_When_BytesAreInsertedOrLost_Then_RepairRestoresItByteExact(string name) {
    var original = File.ReadAllBytes(RarConformanceSet.PathOf(RarConformanceSet.Modern, name));
    // Both edits stay inside one shard (RAR 5, 1004 bytes) or sector (RAR 4): the fixtures carry one recovery unit.
    foreach (var damaged in new[] { Insert(original, 1_500, 10), Delete(original, 1_500, 10) }) {
      var (report, output) = Repair(damaged);
      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(output, Is.EqualTo(original));
    }
  }

  // ---- the rolling checksums the search relies on ----

  [TestCase(1)]
  [TestCase(7)]
  [TestCase(512)]
  [TestCase(1252)]
  [TestCase(70_001)]
  public void Given_AWindow_When_RolledAcrossData_Then_EveryRegisterMatchesADirectComputation(int window) {
    var data = Mixed(window + 300, window);
    var out64 = RollingCrc64.OutgoingTable(window);
    var out32 = RollingCrc32.OutgoingTable(window);
    var c64 = RarCrc64.Update(0, data.AsSpan(0, window));
    var c32 = RarCrc32Raw.Update(0, data.AsSpan(0, window));

    for (var at = 0; at + window < data.Length; ++at) {
      c64 = RollingCrc64.Roll(c64, data[at + window], out64[data[at]]);
      c32 = RollingCrc32.Roll(c32, data[at + window], out32[data[at]]);
      Assert.That(c64, Is.EqualTo(RarCrc64.Update(0, data.AsSpan(at + 1, window))), $"CRC-64 at {at + 1}");
      Assert.That(c32, Is.EqualTo(RarCrc32Raw.Update(0, data.AsSpan(at + 1, window))), $"CRC-32 at {at + 1}");
    }
  }

  // ---- rar against ours, ours against rar (tool-gated) ----

  private static string RequireRar(int generation) {
    var exe = generation == 5 ? RarTools.Rar : RarTools.Rar4;
    if (exe == null) Assert.Ignore("Set CWB_TOOL_RAR (rar 7.x) and CWB_TOOL_RAR4 (rar 6.x, -ma4) to run rar against shifted archives.");
    return exe!;
  }

  [TestCase(5, "insert")]
  [TestCase(5, "delete")]
  [TestCase(5, "both")]
  [TestCase(4, "insert")]
  [TestCase(4, "delete")]
  [TestCase(4, "both")]
  [Category("ExternalInterop")]
  public void Given_OurRecord_When_BytesMoveAndRarRepairs_Then_RarRestoresOurArchiveByteExact(int generation, string kind) {
    var rar = RequireRar(generation);
    var dir = RarTools.TempDir("rarshift");
    try {
      var original = Ours(generation, Mixed(250_000, 7));
      File.WriteAllBytes(Path.Combine(dir, "dmg.rar"), Edit(original, kind));
      var r = RarTools.Run(rar, ["r", "-cfg-", "-y", "dmg.rar"], dir);
      var fixedPath = Path.Combine(dir, "fixed.dmg.rar");
      Assert.That(File.Exists(fixedPath), Is.True, r.Output);
      Assert.That(File.ReadAllBytes(fixedPath), Is.EqualTo(original), r.Output);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase(5, "insert")]
  [TestCase(5, "delete")]
  [TestCase(5, "both")]
  [TestCase(4, "insert")]
  [TestCase(4, "delete")]
  [TestCase(4, "both")]
  [Category("ExternalInterop")]
  public void Given_ARecordRarJustWrote_When_BytesMoveAndWeRepair_Then_TheArchiveIsRarsByteExactAndRarTestsIt(int generation, string kind) {
    var rar = RequireRar(generation);
    var dir = RarTools.TempDir("rarshift2");
    try {
      File.WriteAllBytes(Path.Combine(dir, "in.bin"), Mixed(250_000, 8));
      var a = RarTools.Run(rar, ["a", "-cfg-", "-idq", "-y", "-m0", "-rr5%", generation == 5 ? "-ma5" : "-ma4", "theirs.rar", "in.bin"], dir);
      Assert.That(a.ExitCode, Is.Zero, a.Output);
      var original = File.ReadAllBytes(Path.Combine(dir, "theirs.rar"));

      var (report, output) = Repair(Edit(original, kind));

      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(output, Is.EqualTo(original));
      File.WriteAllBytes(Path.Combine(dir, "ours.rar"), output);
      var t = RarTools.Run(rar, ["t", "-cfg-", "ours.rar"], dir);
      Assert.That(t.ExitCode, Is.Zero, t.Output);
    } finally {
      RarTools.TryDelete(dir);
    }
  }
}
