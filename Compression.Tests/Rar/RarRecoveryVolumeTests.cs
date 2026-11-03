using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Recovery volumes (<c>.rev</c>): rar's rebuild here, ours rebuild here and in rar (<c>rar rc</c>).
/// Layouts in docs/RAR-RECOVERY-ON-DISK.md.
/// </summary>
[TestFixture]
public class RarRecoveryVolumeTests {
  /// <summary>Copies the files of a fixture set into a fresh directory.</summary>
  private static string CopySet(string prefix) {
    var dir = RarTools.TempDir("rarrev");
    foreach (var f in Directory.GetFiles(RarConformanceSet.Directory(RarConformanceSet.Modern), prefix + "*"))
      File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
    return dir;
  }

  private static Dictionary<string, byte[]> Snapshot(string dir, string pattern)
    => Directory.GetFiles(dir, pattern).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

  [TestCase("r5-vol-rev.", "r5-vol-rev.part2.rar", "r5-vol-rev.part1.rar", RarRecoveryKind.Rar5Volumes)]
  [TestCase("r4-vol-rev", "r4-vol-rev.r00", "r4-vol-rev.rar", RarRecoveryKind.Rar3Volumes)]
  public void Given_ASetRarProtected_When_AVolumeIsMissing_Then_ReconstructRebuildsItByteExact(string prefix, string victim, string first, RarRecoveryKind kind) {
    var dir = CopySet(prefix);
    try {
      var original = File.ReadAllBytes(Path.Combine(dir, victim));
      var intact = RarRecoveryVolumes.Verify(Path.Combine(dir, first));
      Assert.That(intact.Kind, Is.EqualTo(kind), intact.Message);
      Assert.That(intact.Intact, Is.True, intact.Message);

      File.Delete(Path.Combine(dir, victim));
      var report = RarRecoveryVolumes.Reconstruct(Path.Combine(dir, first));
      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, victim)), Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [Test]
  public void Given_ASetRarProtected_When_TheLastVolumeIsMissing_Then_ItIsRebuiltToItsRealLength() {
    var dir = CopySet("r4-vol-rev");
    try {
      var original = File.ReadAllBytes(Path.Combine(dir, "r4-vol-rev.r02"));
      File.Delete(Path.Combine(dir, "r4-vol-rev.r02"));
      var report = RarRecoveryVolumes.Reconstruct(Path.Combine(dir, "r4-vol-rev1.rev"));
      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "r4-vol-rev.r02")), Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  private static string WriteOurSet(string dir, string name, string target, int revs, int size = 150_000) {
    var data = new byte[size];
    new Random(size).NextBytes(data);
    var path = Path.Combine(dir, name);
    using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
      new RarFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("data.bin", data)], new FormatCreateOptions("store") {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          [FormatOptionKeys.TargetCompatibility] = target,
          [FormatOptionKeys.VolumeSize] = "30k",
          [RarFormatDescriptor.RecoveryVolumesOption] = revs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
      });
    return path;
  }

  [TestCase("set.part1.rar", "Rar5", "set.part*.r*", RarRecoveryKind.Rar5Volumes)]
  [TestCase("set.part1.rar", "Rar4", "set.part*.r*", RarRecoveryKind.Rar3Volumes)]
  [TestCase("set.rar", "Rar4", "set*.r*", RarRecoveryKind.Rar3Volumes)]
  public void Given_OurRecoveryVolumes_When_AsManyVolumesAreLostOrDamaged_Then_ReconstructRestoresTheSet(string name, string target, string pattern, RarRecoveryKind kind) {
    var dir = RarTools.TempDir("rarrevours");
    try {
      var first = WriteOurSet(dir, name, target, 2);
      var before = Snapshot(dir, pattern);
      var volumes = before.Keys.Where(k => !k.EndsWith(".rev", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList();
      Assert.That(before.Keys.Count(k => k.EndsWith(".rev", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(2));
      var intact = RarRecoveryVolumes.Verify(first);
      Assert.That(intact.Kind, Is.EqualTo(kind));
      Assert.That(intact.Intact, Is.True, intact.Message);

      // One volume gone, another damaged in place.
      File.Delete(Path.Combine(dir, volumes[1]));
      var damaged = File.ReadAllBytes(Path.Combine(dir, volumes[2]));
      damaged[1000] ^= 0xFF;
      File.WriteAllBytes(Path.Combine(dir, volumes[2]), damaged);
      var report = RarRecoveryVolumes.Reconstruct(first);
      Assert.That(report.Repaired, Is.True, report.Message);
      foreach (var v in volumes) Assert.That(File.ReadAllBytes(Path.Combine(dir, v)), Is.EqualTo(before[v]), v);
      Assert.That(File.Exists(Path.Combine(dir, volumes[2] + ".bad")), Is.True, "the damaged volume is kept aside");
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [Test]
  public void Given_OurRecoveryVolumes_When_MoreVolumesAreLostThanThereAreRevs_Then_ItIsNotRepairable() {
    var dir = RarTools.TempDir("rarrevlost");
    try {
      var first = WriteOurSet(dir, "set.part1.rar", "Rar5", 1);
      File.Delete(Path.Combine(dir, "set.part2.rar"));
      File.Delete(Path.Combine(dir, "set.part3.rar"));
      var report = RarRecoveryVolumes.Reconstruct(first);
      Assert.That(report.Repairable, Is.False, report.Message);
      Assert.That(report.Repaired, Is.False);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [Test]
  public void Given_RecoveryVolumesWithoutVolumes_When_Requested_Then_TheDescriptorRefuses() {
    using var ms = new MemoryStream();
    Assert.That(() => new RarFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a", [1, 2, 3])], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [RarFormatDescriptor.RecoveryVolumesOption] = "1" },
    }), Throws.TypeOf<NotSupportedException>());
  }

  // ---- rar against ours (tool-gated) ----

  [TestCase(5, "set.part1.rar", "set.part2.rar")]
  [TestCase(4, "set.part1.rar", "set.part2.rar")]
  [TestCase(4, "set.rar", "set.r00")]
  [Category("ExternalInterop")]
  public void Given_OurRecoveryVolumes_When_RarRebuildsAMissingVolume_Then_ItIsOursByteExact(int generation, string name, string victim) {
    var rar = generation == 5 ? RarTools.Rar : RarTools.Rar4;
    if (rar == null) Assert.Ignore("Set CWB_TOOL_RAR / CWB_TOOL_RAR4 to run rar rc against our recovery volumes.");
    var dir = RarTools.TempDir("rarrevrc");
    try {
      WriteOurSet(dir, name, generation == 5 ? "Rar5" : "Rar4", 2);
      var original = File.ReadAllBytes(Path.Combine(dir, victim));
      File.Delete(Path.Combine(dir, victim));
      var rc = RarTools.Run(rar!, ["rc", "-cfg-", "-y", name], dir);
      Assert.That(File.Exists(Path.Combine(dir, victim)), Is.True, rc.Output);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, victim)), Is.EqualTo(original), rc.Output);
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase(5, "-v30k", "t.part1.rar", "t.part2.rar")]
  [TestCase(4, "-v30k", "t.part1.rar", "t.part3.rar")]
  [TestCase(4, "-vn -v30001b", "t.rar", "t.r01")]
  [Category("ExternalInterop")]
  public void Given_RecoveryVolumesRarJustWrote_When_WeRebuildAMissingVolume_Then_ItIsRarsByteExact(int generation, string vol, string first, string victim) {
    var rar = generation == 5 ? RarTools.Rar : RarTools.Rar4;
    if (rar == null) Assert.Ignore("Set CWB_TOOL_RAR / CWB_TOOL_RAR4.");
    var dir = RarTools.TempDir("rarrevtheirs");
    try {
      var data = new byte[140_000];
      new Random(5).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "in.bin"), data);
      var args = new List<string> { "a", "-cfg-", "-idq", "-y", "-m0", "-rv2", generation == 5 ? "-ma5" : "-ma4" };
      args.AddRange(vol.Split(' '));
      args.AddRange(["t.rar", "in.bin"]);
      var a = RarTools.Run(rar!, args, dir);
      Assert.That(a.ExitCode, Is.Zero, a.Output);
      var original = File.ReadAllBytes(Path.Combine(dir, victim));
      File.Delete(Path.Combine(dir, victim));
      var report = RarRecoveryVolumes.Reconstruct(Path.Combine(dir, first));
      Assert.That(report.Repaired, Is.True, report.Message);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, victim)), Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }
}
