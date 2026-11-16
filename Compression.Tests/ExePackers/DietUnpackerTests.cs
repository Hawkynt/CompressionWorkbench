using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using static Compression.Tests.ExePackers.DosPackerVectors;

namespace Compression.Tests.ExePackers;

/// <summary>
/// DIET 1.45f files made from T1/T2 (EXE) and T5 (COM). UNP 4.11 restored every one of them to
/// the original byte for byte (see DosVectors/README.md), so the original is the oracle's output.
/// </summary>
[TestFixture]
public class DietUnpackerTests {
  [TestCase("P1.EXE", "T1.EXE", "1.45 (exe)")]
  [TestCase("P2.EXE", "T2.EXE", "1.45 (exe)")]
  [TestCase("P5.COM", "T5.COM", "1.45 (exe from com)")] // DIET 1.45f turns a COM into an EXE
  [Category("Oracle")]
  public void Given_ADietFile_When_Unpacked_Then_ItIsTheOriginalByteForByte(string packedName, string originalName, string variant) {
    var packed = Load($"diet145f.{packedName}");
    Assert.That(DietUnpacker.Instance.Detect(packed), Is.EqualTo(variant));
    var restored = DietUnpacker.Instance.Unpack(packed);
    Assert.That(restored.Executable, Is.EqualTo(Load(originalName)));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ADietExe_When_ListedAndUnpackedThroughTheRegistry_Then_TheProgramIsRestored() {
    var packed = Load("diet145f.P2.EXE");
    using var ms = new MemoryStream(packed);
    Assert.That(new DietFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "unpacked.exe", "metadata.ini" }));
    Assert.That(ExecutablePackerHandlers.DetectBest(packed)?.Handler.Id, Is.EqualTo("diet"));
    var result = ExecutablePackerHandlers.TryUnpack(packed)!;
    Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
    Assert.That(result.Artifacts.Single(a => a.Name == "unpacked.exe").Data, Is.EqualTo(Load("T2.EXE")));
  }

  [TestCase("T1.EXE")]
  [TestCase("T5.COM")]
  [TestCase("pklite201.P1.EXE")]
  [TestCase("exepack406.P1.EXE")]
  [Category("EdgeCase")]
  public void Given_AFileOtherThanDiet_When_Detected_Then_ItIsNotDiet(string name) =>
    Assert.That(DietUnpacker.Instance.Detect(Load(name)), Is.Null);

  [Test]
  [Category("Exception")]
  public void Given_ATruncatedDietFile_When_Unpacked_Then_ItIsRefused() {
    var packed = Load("diet145f.P2.EXE");
    Assert.That(() => DietUnpacker.Instance.Unpack(packed[..(packed.Length - 3000)]), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase("diet145f.P2.EXE")]
  [TestCase("diet145f.P5.COM")]
  [Category("Exception")]
  public void Given_CorruptedBytes_When_Unpacked_Then_OnlyInvalidDataIsEverThrown(string name) {
    var packed = Load(name);
    var rng = new Random(145);
    for (var round = 0; round < 200; ++round) {
      var bad = (byte[])packed.Clone();
      for (var i = 0; i < 4; ++i)
        bad[rng.Next(0x20, bad.Length)] ^= (byte)rng.Next(1, 256);
      try {
        DietUnpacker.Instance.Unpack(bad);
      } catch (InvalidDataException) {
      }
    }
  }

  // ── live oracle ──────────────────────────────────────────────────────

  /// <summary>
  /// Packs T1/T2 with the real DIET under DOSBox, restores them with UNP 4.11 there, and checks
  /// ours equals UNP's output. Needs CWB_DOSBOX, CWB_TOOL_DIET (DIET.EXE) and CWB_TOOL_UNP.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_TheRealDietAndUnp_When_TheyPackAndRestore_Then_OursMatchesUnp() {
    RequireDosTools("DIET", "UNP");
    var drive = TempDrive("diet");
    try {
      File.Copy(Tool("DIET")!, Path.Combine(drive, "DIET.EXE"));
      File.Copy(Tool("UNP")!, Path.Combine(drive, "UNP.EXE"));
      var lines = new List<string>();
      foreach (var n in new[] { "1", "2" }) {
        File.WriteAllBytes(Path.Combine(drive, $"P{n}.EXE"), Load($"T{n}.EXE"));
        lines.AddRange([$"DIET -B P{n}.EXE > NUL", $"P{n}.EXE > R{n}.TXT", $"UNP e -k- P{n}.EXE U{n}.EXE > NUL"]);
      }
      RunDos(drive, lines);
      foreach (var n in new[] { "1", "2" }) {
        Assert.That(ReadText(Path.Combine(drive, $"R{n}.TXT")), Does.StartWith("OK"), $"packed T{n} did not run");
        var ours = DietUnpacker.Instance.Unpack(File.ReadAllBytes(Path.Combine(drive, $"P{n}.EXE"))).Executable;
        Assert.That(ours, Is.EqualTo(File.ReadAllBytes(Path.Combine(drive, $"U{n}.EXE"))), $"T{n}: ours vs UNP");
      }
    } finally {
      try { Directory.Delete(drive, true); } catch (IOException) { }
    }
  }
}
