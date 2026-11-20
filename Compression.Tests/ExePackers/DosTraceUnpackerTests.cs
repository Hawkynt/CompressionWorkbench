using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using static Compression.Tests.ExePackers.DosPackerVectors;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Packers restored by running their stub in the 8086 emulator. The fixtures are T1/T2/T6
/// packed by the real packers; T6 starts in a later segment at a non-zero offset.
/// </summary>
[TestFixture]
public class DosTraceUnpackerTests {
  public static IEnumerable<TestCaseData> Fixtures() {
    foreach (var (file, original, variant, sp) in new[] {
      ("apack.P1.EXE", "T1.EXE", "1.00", 0), ("apack.P2.EXE", "T2.EXE", "1.00", 0), ("apack.P6.EXE", "T6.EXE", "1.00", 0),
      ("apack-s.P1.EXE", "T1.EXE", "1.00 (-s)", 0), ("apack-t.P1.EXE", "T1.EXE", "1.00 (tiny, -t)", 0),
      ("exelite.P1.EXE", "T1.EXE", "2.00", 0), ("exelite.P2.EXE", "T2.EXE", "2.00", 0), ("exelite.P6.EXE", "T6.EXE", "2.00", 0),
      ("tinyprog.P1.EXE", "T1.EXE", "3.x", 0), ("tinyprog.P2.EXE", "T2.EXE", "3.x", 0),
      ("axe.P1.EXE", "T1.EXE", "2.2", 2), ("axe.P2.EXE", "T2.EXE", "2.2", 2),
    })
      yield return new TestCaseData(file, original, variant, sp).SetName($"Traced_{file.Replace('.', '_')}");
  }

  private static IDosExeUnpacker For(string file) => file.Split('.')[0] switch {
    "apack" or "apack-s" or "apack-t" => ApackUnpacker.Instance,
    "exelite" => ExeLiteUnpacker.Instance,
    "tinyprog" => TinyProgUnpacker.Instance,
    _ => AxeUnpacker.Instance,
  };

  [TestCaseSource(nameof(Fixtures))]
  [Category("Oracle")]
  public void Given_AFilePackedByTheRealPacker_When_TracedUnpacked_Then_TheOriginalProgramComesBack(string file, string original, string variant, int sp) {
    var packed = Load(file);
    var unpacker = For(file);
    Assert.That(unpacker.Detect(packed), Is.EqualTo(variant));
    var restored = unpacker.Unpack(packed);
    AssertSameProgram(Load(original), restored.Executable, file, sp);
  }

  [TestCase("exelite.P1.EXE", "T1.EXE")]
  [TestCase("exelite.P6.EXE", "T6.EXE")]
  [Category("Oracle")]
  public void Given_AnExeLiteFile_When_Unpacked_Then_TheStoredHeaderRestoresTheAllocation(string file, string original) {
    var o = MzView.Parse(Load(original));
    var r = MzView.Parse(ExeLiteUnpacker.Instance.Unpack(Load(file)).Executable);
    Assert.That((r.MinAlloc, r.MaxAlloc), Is.EqualTo((o.MinAlloc, o.MaxAlloc)));
  }

  /// <summary>UNP 4.11 restores AXE files with the same entry registers as the emulator, SP included.</summary>
  [TestCase("1")]
  [TestCase("2")]
  [Category("Oracle")]
  public void Given_AnAxeFile_When_Unpacked_Then_ItMatchesUnpsRestoration(string n) {
    var ours = MzView.Parse(AxeUnpacker.Instance.Unpack(Load($"axe.P{n}.EXE")).Executable);
    var unp = MzView.Parse(Load($"axe.U{n}.EXE"));
    Assert.That(ours.Image, Is.EqualTo(unp.Image));
    Assert.That(ours.LinearRelocs.Order(), Is.EqualTo(unp.LinearRelocs.Order()));
    Assert.That((ours.CS, ours.IP, ours.SS, ours.SP), Is.EqualTo((unp.CS, unp.IP, unp.SS, unp.SP)));
  }

  /// <summary>
  /// The emulator against the converted unpackers: for files of four other packers it restores
  /// the same image, relocations and registers, so the emulation itself is checked on stubs whose
  /// result is known exactly.
  /// </summary>
  [TestCase("lzexe091.P2.EXE", "T2.EXE")]
  [TestCase("pklite201.P2.EXE", "T2.EXE")]
  [TestCase("diet145f.P2.EXE", "T2.EXE")]
  [Category("Oracle")]
  public void Given_AFileAConvertedUnpackerRestores_When_Traced_Then_TheSameProgramComesBack(string file, string original) =>
    AssertSameProgram(Load(original), DosTracer.Unpack(Load(file)).Executable, file);

  [Test]
  [Category("Oracle")]
  public void Given_APkliteCom_When_Traced_Then_TheOriginalComComesBack() {
    var r = DosTracer.Unpack(Load("pklite201.P5.COM"));
    Assert.That(r.IsCom, Is.True);
    Assert.That(r.Executable, Is.EqualTo(Load("T5.COM")));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_AnApackFile_When_ListedAndUnpackedThroughTheRegistry_Then_TheProgramIsRestored() {
    var packed = Load("apack.P2.EXE");
    using var ms = new MemoryStream(packed);
    Assert.That(new ApackFormatDescriptor().List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "unpacked.exe", "metadata.ini" }));
    Assert.That(ExecutablePackerHandlers.DetectBest(packed)?.Handler.Id, Is.EqualTo("apack"));
    Assert.That(ExecutablePackerHandlers.TryUnpack(packed)!.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
  }

  [TestCase("T1.EXE")]
  [TestCase("T6.EXE")]
  [TestCase("lzexe091.P1.EXE")]
  [TestCase("pklite201.P1.EXE")]
  [Category("EdgeCase")]
  public void Given_AnotherFile_When_Detected_Then_NoTracedPackerClaimsIt(string name) {
    var file = Load(name);
    Assert.Multiple(() => {
      Assert.That(ApackUnpacker.Instance.Detect(file), Is.Null);
      Assert.That(ExeLiteUnpacker.Instance.Detect(file), Is.Null);
      Assert.That(TinyProgUnpacker.Instance.Detect(file), Is.Null);
      Assert.That(AxeUnpacker.Instance.Detect(file), Is.Null);
    });
  }

  [Test]
  [Category("Exception")]
  public void Given_AnUnpackedProgram_When_Traced_Then_ItIsRefusedBecauseNoStubEnds() =>
    Assert.That(() => DosTracer.Unpack(Load("T1.EXE")), Throws.InstanceOf<InvalidDataException>());

  [Test]
  [Category("Exception")]
  public void Given_ATruncatedFile_When_Traced_Then_OnlyInvalidDataIsThrown() {
    var packed = Load("apack.P2.EXE");
    Assert.That(() => ApackUnpacker.Instance.Unpack(packed[..(packed.Length / 2)]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  [Category("Exception")]
  public void Given_CorruptedBytes_When_Traced_Then_OnlyInvalidDataIsEverThrown() {
    var packed = Load("apack.P1.EXE");
    var rng = new Random(100);
    for (var round = 0; round < 40; ++round) {
      var bad = (byte[])packed.Clone();
      for (var i = 0; i < 4; ++i)
        bad[rng.Next(0x40, bad.Length)] ^= (byte)rng.Next(1, 256);
      try {
        DosTracer.Unpack(bad, 2_000_000);
      } catch (InvalidDataException) {
      }
    }
  }

  // ── live oracle ──────────────────────────────────────────────────────

  public static IEnumerable<TestCaseData> LivePackers() {
    yield return new TestCaseData("APACK", "APACK T{0}.EXE P{0}.EXE", 0).SetName("Live_Apack");
    yield return new TestCaseData("ELITE", "copy T{0}.EXE P{0}.EXE > NUL\r\nELITE a P{0}.EXE", 0).SetName("Live_ExeLite");
    yield return new TestCaseData("TINYPROG", "TINYPROG T{0}.EXE P{0}.EXE", 0).SetName("Live_TinyProg");
    yield return new TestCaseData("AXE", "copy T{0}.EXE P{0}.EXE > NUL\r\nAXE P{0}.EXE B{0}.EXE -f", 2).SetName("Live_Axe");
  }

  /// <summary>
  /// Packs T1/T2/T6 with the real packer under DOSBox (CWB_DOSBOX, CWB_TOOL_&lt;name&gt;), checks
  /// the packed program still prints OK there, and that the emulator restores the original.
  /// </summary>
  [TestCaseSource(nameof(LivePackers))]
  [Category("ExternalInterop")]
  public void Given_TheRealPacker_When_ItPacksTheTestPrograms_Then_TheTraceRestoresThem(string tool, string command, int sp) {
    RequireDosTools(tool);
    var drive = TempDrive(tool.ToLowerInvariant());
    try {
      File.Copy(Tool(tool)!, Path.Combine(drive, tool + ".EXE"));
      var lines = new List<string>();
      foreach (var n in new[] { "1", "2", "6" }) {
        File.WriteAllBytes(Path.Combine(drive, $"T{n}.EXE"), Load($"T{n}.EXE"));
        lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, command, n) + " > NUL");
        lines.Add($"P{n}.EXE > R{n}.TXT");
      }
      RunDos(drive, lines);
      foreach (var n in new[] { "1", "2", "6" }) {
        Assert.That(ReadText(Path.Combine(drive, $"R{n}.TXT")), Does.StartWith("OK"), $"packed T{n} did not run");
        var restored = DosTracer.Unpack(File.ReadAllBytes(Path.Combine(drive, $"P{n}.EXE"))).Executable;
        AssertSameProgram(Load($"T{n}.EXE"), restored, $"{tool} T{n}", sp);
      }
    } finally {
      try { Directory.Delete(drive, true); } catch (IOException) { }
    }
  }
}
