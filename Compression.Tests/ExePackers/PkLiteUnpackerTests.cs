using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using static Compression.Tests.ExePackers.DosPackerVectors;

namespace Compression.Tests.ExePackers;

/// <summary>
/// PKLITE 1.15 and 2.01 files made by the real packers from T1/T2 (EXE) and T5 (COM). For all
/// of them UNP 4.11 and the packer's own <c>pklite -x</c> restored the original byte for byte
/// (see DosVectors/README.md), so the original is the oracle's output.
/// </summary>
[TestFixture]
public class PkLiteUnpackerTests {
  [TestCase("pklite115", "P1.EXE", "T1.EXE", "1.15")]
  [TestCase("pklite115", "P2.EXE", "T2.EXE", "1.15")]
  [TestCase("pklite115", "P5.COM", "T5.COM", "1.14")] // PKLITE 1.15 stamps its COM files as 1.14

  [TestCase("pklite201", "P1.EXE", "T1.EXE", "2.01")]
  [TestCase("pklite201", "P2.EXE", "T2.EXE", "2.01")]
  [TestCase("pklite201", "P5.COM", "T5.COM", "2.01")]
  [Category("Oracle")]
  public void Given_APkliteFile_When_Unpacked_Then_ItIsTheOriginalByteForByte(string set, string packedName, string originalName, string version) {
    var packed = Load($"{set}.{packedName}");
    Assert.That(PkLiteUnpacker.Instance.Detect(packed), Does.StartWith(version));
    var restored = PkLiteUnpacker.Instance.Unpack(packed);
    Assert.That(restored.Executable, Is.EqualTo(Load(originalName)));
    Assert.That(restored.FileName, Is.EqualTo(originalName.EndsWith(".COM", StringComparison.Ordinal) ? "unpacked.com" : "unpacked.exe"));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_APkliteExe_When_ListedThroughTheDescriptor_Then_TheRestoredProgramIsAnEntry() {
    using var ms = new MemoryStream(Load("pklite201.P2.EXE"));
    var entries = new PkLiteFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "unpacked.exe", "metadata.ini" }));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(Load("T2.EXE").Length));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_APkliteCom_When_ListedThroughTheDescriptor_Then_TheRestoredComIsAnEntry() {
    using var ms = new MemoryStream(Load("pklite115.P5.COM"));
    var entries = new PkLiteFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "unpacked.com", "metadata.ini" }));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_APkliteExe_When_TheHandlerRegistryUnpacksIt_Then_PkliteRebuildsTheExecutable() {
    var packed = Load("pklite115.P1.EXE");
    Assert.That(ExecutablePackerHandlers.DetectBest(packed)?.Handler.Id, Is.EqualTo("pklite"));
    var result = ExecutablePackerHandlers.TryUnpack(packed)!;
    Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
    Assert.That(result.Artifacts.Single(a => a.Name == "unpacked.exe").Data, Is.EqualTo(Load("T1.EXE")));
  }

  [TestCase("T1.EXE")]
  [TestCase("T5.COM")]
  [TestCase("lzexe091.P1.EXE")]
  [Category("EdgeCase")]
  public void Given_AFileOtherThanPklite_When_Detected_Then_ItIsNotPklite(string name) =>
    Assert.That(PkLiteUnpacker.Instance.Detect(Load(name)), Is.Null);

  [Test]
  [Category("Exception")]
  public void Given_ATruncatedPkliteFile_When_Unpacked_Then_ItIsRefusedAndListedPartially() {
    var packed = Load("pklite201.P2.EXE");
    var truncated = packed[..(packed.Length / 2)];
    Assert.That(() => PkLiteUnpacker.Instance.Unpack(truncated), Throws.InstanceOf<InvalidDataException>());
    using var ms = new MemoryStream(truncated);
    Assert.That(new PkLiteFormatDescriptor().List(ms, null).Select(e => e.Name),
      Is.EquivalentTo(new[] { "metadata.ini", "packed_payload.bin" }));
  }

  [TestCase("pklite201.P2.EXE")]
  [TestCase("pklite115.P5.COM")]
  [Category("Exception")]
  public void Given_CorruptedCompressedBytes_When_Unpacked_Then_OnlyInvalidDataIsEverThrown(string name) {
    var packed = Load(name);
    var rng = new Random(201);
    for (var round = 0; round < 200; ++round) {
      var bad = (byte[])packed.Clone();
      for (var i = 0; i < 4; ++i)
        bad[rng.Next(0x60, bad.Length)] ^= (byte)rng.Next(1, 256);
      try {
        PkLiteUnpacker.Instance.Unpack(bad);
      } catch (InvalidDataException) {
      }
    }
  }

  // ── live oracle ──────────────────────────────────────────────────────

  /// <summary>
  /// Packs T1/T2 with the real PKLITE under DOSBox, restores them with <c>pklite -x</c> and
  /// UNP 4.11 there, and checks ours equals both. Needs CWB_DOSBOX, CWB_TOOL_PKLITE (PKLITE.EXE)
  /// and CWB_TOOL_UNP (UNP.EXE 4.11).
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_TheRealPkliteAndUnp_When_TheyPackAndRestore_Then_OursMatchesBoth() {
    RequireDosTools("PKLITE", "UNP");
    var drive = TempDrive("pklite");
    try {
      File.Copy(Tool("PKLITE")!, Path.Combine(drive, "PKLITE.EXE"));
      File.Copy(Tool("UNP")!, Path.Combine(drive, "UNP.EXE"));
      var lines = new List<string>();
      foreach (var n in new[] { "1", "2" }) {
        File.WriteAllBytes(Path.Combine(drive, $"T{n}.EXE"), Load($"T{n}.EXE"));
        lines.AddRange([$"PKLITE T{n}.EXE P{n}.EXE > NUL", $"P{n}.EXE > R{n}.TXT", $"UNP e -k- P{n}.EXE U{n}.EXE > NUL", $"PKLITE -x P{n}.EXE X{n}.EXE > NUL"]);
      }
      RunDos(drive, lines);
      foreach (var n in new[] { "1", "2" }) {
        Assert.That(ReadText(Path.Combine(drive, $"R{n}.TXT")), Does.StartWith("OK"), $"packed T{n} did not run");
        var ours = PkLiteUnpacker.Instance.Unpack(File.ReadAllBytes(Path.Combine(drive, $"P{n}.EXE"))).Executable;
        Assert.That(ours, Is.EqualTo(File.ReadAllBytes(Path.Combine(drive, $"U{n}.EXE"))), $"T{n}: ours vs UNP");
        Assert.That(ours, Is.EqualTo(File.ReadAllBytes(Path.Combine(drive, $"X{n}.EXE"))), $"T{n}: ours vs pklite -x");
      }
    } finally {
      try { Directory.Delete(drive, true); } catch (IOException) { }
    }
  }
}
