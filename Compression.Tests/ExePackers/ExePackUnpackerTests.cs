using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using static Compression.Tests.ExePackers.DosPackerVectors;

namespace Compression.Tests.ExePackers;

/// <summary>
/// EXEPACK 4.06 (MASM 5.1) files made from T1/T2, restored and compared with UNP 4.11's
/// restoration of the same files and with the original programs.
/// </summary>
[TestFixture]
public class ExePackUnpackerTests {
  /// <summary>
  /// UNP sizes the minimum allocation its own way; ours keeps the packed program's total memory
  /// (0x40 and 0x48 paragraphs here). Everything else is UNP's output byte for byte.
  /// </summary>
  [TestCase("1", 0x40)]
  [TestCase("2", 0x48)]
  [Category("Oracle")]
  public void Given_AnExepackFile_When_Unpacked_Then_ItIsUnpsOutputApartFromTheMinimumAllocation(string n, int minAlloc) {
    var packed = Load($"exepack406.P{n}.EXE");
    Assert.That(ExePackUnpacker.Instance.Detect(packed), Is.EqualTo("EXEPACK 4.05/4.06"));
    var restored = ExePackUnpacker.Instance.Unpack(packed).Executable;
    var unp = Load($"exepack406.U{n}.EXE");
    unp[10] = (byte)minAlloc;
    unp[11] = (byte)(minAlloc >> 8);
    Assert.That(restored, Is.EqualTo(unp));
  }

  [TestCase("1")]
  [TestCase("2")]
  [Category("HappyPath")]
  public void Given_AnExepackFile_When_Unpacked_Then_TheOriginalImageRelocationsAndRegistersComeBack(string n) {
    var original = MzView.Parse(Load($"T{n}.EXE"));
    var restored = MzView.Parse(ExePackUnpacker.Instance.Unpack(Load($"exepack406.P{n}.EXE")).Executable);
    // EXEPACK rounds the image up to whole paragraphs; the original is its prefix.
    Assert.That(restored.Image[..original.Image.Length], Is.EqualTo(original.Image));
    Assert.That(restored.Image.Length - original.Image.Length, Is.InRange(0, 15));
    Assert.That(restored.LinearRelocs, Is.EqualTo(original.LinearRelocs));
    Assert.That((restored.CS, restored.IP, restored.SS, restored.SP), Is.EqualTo((original.CS, original.IP, original.SS, original.SP)));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_AnExepackFile_When_ListedAndUnpackedThroughTheRegistry_Then_TheProgramIsRestored() {
    var packed = Load("exepack406.P2.EXE");
    using var ms = new MemoryStream(packed);
    Assert.That(new ExePackFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "unpacked.exe", "metadata.ini" }));
    Assert.That(ExecutablePackerHandlers.DetectBest(packed)?.Handler.Id, Is.EqualTo("exepack"));
    Assert.That(ExecutablePackerHandlers.TryUnpack(packed)!.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
  }

  [TestCase("T1.EXE")]
  [TestCase("lzexe091.P1.EXE")]
  [TestCase("pklite201.P1.EXE")]
  [Category("EdgeCase")]
  public void Given_AFileOtherThanExepack_When_Detected_Then_ItIsNotExepack(string name) =>
    Assert.That(ExePackUnpacker.Instance.Detect(Load(name)), Is.Null);

  [Test]
  [Category("Exception")]
  public void Given_ACorruptedFinalOpcode_When_Unpacked_Then_ItIsRefused() {
    var packed = Load("exepack406.P1.EXE");
    var exe = MzView.Parse(packed);
    // The compressed image ends just below the stub at CS:0; its last byte is the final opcode.
    var hdr = 16 * BitConverter.ToUInt16(packed, 8);
    var bad = (byte[])packed.Clone();
    var last = hdr + 16 * exe.CS - 1;
    while (bad[last] == 0xFF)
      --last;
    bad[last] = 0x42;
    Assert.That(() => ExePackUnpacker.Instance.Unpack(bad), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("Exception")]
  public void Given_CorruptedBytes_When_Unpacked_Then_OnlyInvalidDataIsEverThrown() {
    var packed = Load("exepack406.P2.EXE");
    var rng = new Random(406);
    for (var round = 0; round < 200; ++round) {
      var bad = (byte[])packed.Clone();
      for (var i = 0; i < 4; ++i)
        bad[rng.Next(0x20, bad.Length)] ^= (byte)rng.Next(1, 256);
      try {
        ExePackUnpacker.Instance.Unpack(bad);
      } catch (InvalidDataException) {
      }
    }
  }

  // ── live oracle ──────────────────────────────────────────────────────

  /// <summary>
  /// Packs T1/T2 with the real EXEPACK under DOSBox, restores them with UNP 4.11 there, and
  /// checks ours equals UNP's output apart from the minimum allocation. Needs CWB_DOSBOX,
  /// CWB_TOOL_EXEPACK (EXEPACK.EXE) and CWB_TOOL_UNP (UNP.EXE 4.11).
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_TheRealExepackAndUnp_When_TheyPackAndRestore_Then_OursMatchesUnp() {
    RequireDosTools("EXEPACK", "UNP");
    var drive = TempDrive("exepack");
    try {
      File.Copy(Tool("EXEPACK")!, Path.Combine(drive, "EXEPACK.EXE"));
      File.Copy(Tool("UNP")!, Path.Combine(drive, "UNP.EXE"));
      var lines = new List<string>();
      foreach (var n in new[] { "1", "2" }) {
        File.WriteAllBytes(Path.Combine(drive, $"T{n}.EXE"), Load($"T{n}.EXE"));
        lines.AddRange([$"EXEPACK T{n}.EXE P{n}.EXE > NUL", $"P{n}.EXE > R{n}.TXT", $"UNP e -k- P{n}.EXE U{n}.EXE > NUL", $"U{n}.EXE > RU{n}.TXT"]);
      }
      RunDos(drive, lines);
      foreach (var n in new[] { "1", "2" }) {
        Assert.That(ReadText(Path.Combine(drive, $"R{n}.TXT")), Does.StartWith("OK"), $"packed T{n} did not run");
        Assert.That(ReadText(Path.Combine(drive, $"RU{n}.TXT")), Does.StartWith("OK"), $"UNP-restored T{n} did not run");
        var ours = ExePackUnpacker.Instance.Unpack(File.ReadAllBytes(Path.Combine(drive, $"P{n}.EXE"))).Executable;
        var unp = File.ReadAllBytes(Path.Combine(drive, $"U{n}.EXE"));
        unp[10] = ours[10];
        unp[11] = ours[11];
        Assert.That(ours, Is.EqualTo(unp), $"T{n}: ours vs UNP");
      }
    } finally {
      try { Directory.Delete(drive, true); } catch (IOException) { }
    }
  }
}
