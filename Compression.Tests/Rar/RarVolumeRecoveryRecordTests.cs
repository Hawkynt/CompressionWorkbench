using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Recovery records inside volume sets: like rar (<c>-v -rr</c>), every volume carries its own
/// record over its own bytes, within the volume size. Ours are tested and repaired by rar
/// (7.12 for RAR 5, 6.24 for RAR 4); rar's are repaired here. Layout notes in
/// docs/RAR-RECOVERY-ON-DISK.md.
/// </summary>
[TestFixture]
public class RarVolumeRecoveryRecordTests {

  private static byte[] Mixed(int length, int seed) {
    var rnd = new Random(seed);
    var data = new byte[length];
    rnd.NextBytes(data);
    for (var i = 0; i < length; i += 7) data[i] = (byte)'a';
    return data;
  }

  private static byte[] Damage(byte[] volume, long at, int length) {
    var d = (byte[])volume.Clone();
    for (var i = 0; i < length; ++i) d[at + i] ^= 0x5A;
    return d;
  }

  private static byte[][] Ours(int generation, long volumeSize, int percent, byte[] data, int method = 0, string? password = null) {
    var volumes = new List<MemoryStream> { new() };
    Stream Open(int _) {
      var m = new MemoryStream();
      volumes.Add(m);
      return m;
    }

    if (generation == 5) {
      using var w = new RarWriter(volumes[0], leaveOpen: true, method: method, password: password,
        volumeSize: volumeSize, openVolume: Open, recoveryPercent: percent);
      w.AddFile("data.bin", data);
      w.AddFile("small.txt", "after the split"u8);
    } else {
      using var w = new Rar4Writer(volumes[0], leaveOpen: true, method: method == 0 ? RarConstants.Rar4MethodStore : RarConstants.Rar4MethodNormal,
        password: password, volumeSize: volumeSize, openVolume: Open, newVolumeNaming: true, recoveryPercent: percent);
      w.AddFile("data.bin", data);
      w.AddFile("small.txt", "after the split"u8);
    }
    return [.. volumes.Select(v => v.ToArray())];
  }

  private static RarRecoveryReport Verify(byte[] volume) {
    using var ms = new MemoryStream(volume, writable: false);
    return RarRecovery.Verify(ms);
  }

  private static (RarRecoveryReport Report, byte[] Output) Repair(byte[] damaged) {
    using var src = new MemoryStream(damaged, writable: false);
    using var dst = new MemoryStream();
    var report = RarRecovery.Repair(src, dst);
    return (report, dst.ToArray());
  }

  // ---- written and read here ----

  [Category("RoundTrip")]
  [TestCase(5, 0, 5, null)]
  [TestCase(5, 0, 20, null)]
  [TestCase(5, 3, 5, null)]
  [TestCase(5, 0, 5, "secret")]
  [TestCase(4, 0, 5, null)]
  [TestCase(4, 0, 20, null)]
  [TestCase(4, 3, 5, "secret")]
  public void Given_AVolumeSetWithARecord_When_Written_Then_EveryVolumeFitsCarriesAnIntactRecordAndTheSetReads(int generation, int method, int percent, string? password) {
    const long volumeSize = 40_000;
    var data = Mixed(150_000, 3);

    var volumes = Ours(generation, volumeSize, percent, data, method, password);

    Assert.That(volumes, Has.Length.GreaterThan(2));
    var kind = generation == 5 ? RarRecoveryKind.Rar5Record : RarRecoveryKind.Rar4Record;
    for (var n = 0; n < volumes.Length; ++n) {
      Assert.That(volumes[n].LongLength, Is.LessThanOrEqualTo(volumeSize), $"volume {n + 1}");
      var report = Verify(volumes[n]);
      Assert.That(report.Kind, Is.EqualTo(kind), $"volume {n + 1}: {report.Message}");
      Assert.That(report.Intact, Is.True, $"volume {n + 1}: {report.Message}");
    }
    using var reader = new RarReader(volumes.Select(v => (Stream)new MemoryStream(v)).ToList(), password);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
    Assert.That(reader.Extract(1), Is.EqualTo("after the split"u8.ToArray()));
  }

  [Category("RoundTrip")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_DamageInAMiddleVolume_When_RepairedHere_Then_TheVolumeIsByteExact(int generation) {
    var volumes = Ours(generation, 40_000, 10, Mixed(150_000, 4));
    var damaged = Damage(volumes[1], 3_000, 1_000);
    Assert.That(Verify(damaged).Intact, Is.False);

    var (report, output) = Repair(damaged);

    Assert.That(report.Repaired, Is.True, report.Message);
    Assert.That(output, Is.EqualTo(volumes[1]));
  }

  [Category("Boundary")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_AVolumeSizeThatTheRecordNearlyFills_When_Written_Then_VolumesStayWithinIt(int generation) {
    // Small volumes: the record is about a shard plus its table, a large share of each volume.
    var volumes = Ours(generation, 6_000, 5, Mixed(30_000, 5));

    Assert.That(volumes.All(v => v.LongLength <= 6_000), Is.True);
    Assert.That(volumes.All(v => Verify(v).Intact), Is.True);
  }

  [Category("Exception")]
  [Test]
  public void Given_AVolumeTooSmallForAnyRecord_When_Written_Then_ItIsRefused() {
    // A 1 KB RAR 5 volume cannot hold one 1 KB shard's recovery block next to any data.
    Assert.Throws<InvalidOperationException>(() => Ours(5, 1024, 5, Mixed(5_000, 6)));
  }

  [Category("Exception")]
  [TestCase(5)]
  [TestCase(4)]
  public void Given_AnUnreadableNextVolume_When_ARecordIsWritten_Then_ItIsRefused(int generation) {
    var first = new MemoryStream();
    Stream Open(int _) => new WriteOnlyStream();

    void Write() {
      if (generation == 5) {
        using var w = new RarWriter(first, leaveOpen: true, method: 0, volumeSize: 8_000, openVolume: Open, recoveryPercent: 5);
        w.AddFile("data.bin", Mixed(30_000, 7));
      } else {
        using var w = new Rar4Writer(first, leaveOpen: true, method: RarConstants.Rar4MethodStore, volumeSize: 8_000, openVolume: Open, recoveryPercent: 5);
        w.AddFile("data.bin", Mixed(30_000, 7));
      }
    }

    Assert.Throws<NotSupportedException>(Write);
  }

  private sealed class WriteOnlyStream : MemoryStream {
    public override bool CanRead => false;
  }

  [Test]
  public void Given_RecordSizes_When_FittingData_Then_DataPlusRecordNeverExceedsTheRoom() {
    // A step function like the real record sizes: grows by whole blocks.
    static long Record(long protectedSize) => 100 + (protectedSize / 1000 + 1) * 64;

    foreach (var room in new long[] { 0, 1, 50, 163, 164, 1_000, 10_000, 123_457 }) {
      var data = RarWriter.FitBeforeRecord(room, 500, Record);
      if (data > 0)
        Assert.That(data + Record(500 + data), Is.LessThanOrEqualTo(room), $"room {room}");
      Assert.That(data, Is.GreaterThanOrEqualTo(room - Record(500 + room)), $"room {room}: no worse than sizing the record for the whole room");
    }
  }

  // ---- rar against ours, ours against rar (tool-gated) ----

  private static string RequireRar(int generation) {
    var exe = generation == 5 ? RarTools.Rar : RarTools.Rar4;
    if (exe == null) Assert.Ignore("Set CWB_TOOL_RAR (rar 7.x) and CWB_TOOL_RAR4 (rar 6.x, -ma4) to run rar against volume recovery records.");
    return exe!;
  }

  private static string VolumeName(int n) => $"set.part{n + 1}.rar";

  [TestCase(5)]
  [TestCase(4)]
  [Category("ExternalInterop")]
  public void Given_OurVolumeSetWithRecords_When_RarTestsAndRepairsAMiddleVolume_Then_TheRepairedSetIsAccepted(int generation) {
    var rar = RequireRar(generation);
    var dir = RarTools.TempDir("rarvolrr");
    try {
      var volumes = Ours(generation, 100 * 1024, 5, Mixed(250_000, 9));
      for (var n = 0; n < volumes.Length; ++n) File.WriteAllBytes(Path.Combine(dir, VolumeName(n)), volumes[n]);
      var t = RarTools.Run(rar, ["t", "-cfg-", VolumeName(0)], dir);
      Assert.That(t.ExitCode, Is.Zero, t.Output);
      if (generation == 5) Assert.That(t.Output, Does.Contain("recovery record").IgnoreCase, t.Output);

      File.WriteAllBytes(Path.Combine(dir, VolumeName(1)), Damage(volumes[1], 20_000, 1_000));
      var r = RarTools.Run(rar, ["r", "-cfg-", "-y", VolumeName(1)], dir);
      var fixedPath = Path.Combine(dir, "fixed." + VolumeName(1));
      Assert.That(File.Exists(fixedPath), Is.True, r.Output);
      var rebuilt = File.ReadAllBytes(fixedPath);
      if (generation == 4) {
        Assert.That(rebuilt, Is.EqualTo(volumes[1]), r.Output);
      } else {
        // rar 7.12 rewrites a repaired volume's end header (end flags 0) and drops the padding,
        // as it does for its own volumes; everything the record protects comes back exactly.
        using var s = new MemoryStream(volumes[1]);
        var protectedSize = Rar5RecoveryRecord.Find(s)!.Protected;
        Assert.That(rebuilt[..(int)protectedSize], Is.EqualTo(volumes[1][..(int)protectedSize]), r.Output);
      }

      File.Delete(Path.Combine(dir, VolumeName(1)));
      File.Move(fixedPath, Path.Combine(dir, VolumeName(1)));
      var t2 = RarTools.Run(rar, ["t", "-cfg-", VolumeName(0)], dir);
      Assert.That(t2.ExitCode, Is.Zero, t2.Output);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase(5, "-rr5%")]
  [TestCase(5, "-rr20%")]
  [TestCase(4, "-rr5%")]
  [Category("ExternalInterop")]
  public void Given_RarsVolumeSetWithRecords_When_WeRepairAMiddleVolume_Then_ItIsRarsByteExact(int generation, string rr) {
    var rar = RequireRar(generation);
    var dir = RarTools.TempDir("rarvolrr2");
    try {
      File.WriteAllBytes(Path.Combine(dir, "in.bin"), Mixed(250_000, 11));
      var a = RarTools.Run(rar, ["a", "-cfg-", "-idq", "-y", "-m0", "-v100k", rr, generation == 5 ? "-ma5" : "-ma4", "set.rar", "in.bin"], dir);
      Assert.That(a.ExitCode, Is.Zero, a.Output);
      var original = File.ReadAllBytes(Path.Combine(dir, "set.part2.rar"));
      Assert.That(Verify(original).Intact, Is.True);

      var (report, output) = Repair(Damage(original, 20_000, generation == 5 ? 2_000 : 1_500));

      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(output, Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }
}
