using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;
using static Compression.Tests.ExePackers.DosPackerVectors;

namespace Compression.Tests.ExePackers;

/// <summary>
/// LZEXE 0.91 and 1.00a files made by the real packers from T1/T2, restored and compared with
/// the original programs and with what UNP 4.11 restored from the same files.
/// </summary>
[TestFixture]
public class LzExeUnpackerTests {
  // The variant names the stub layout: LZEXE 0.91 writes the stub known as "0.91e", 1.00a the "0.91" one.
  [TestCase("lzexe091", "1", "0.91e")]
  [TestCase("lzexe091", "2", "0.91e")]
  [TestCase("lzexe100", "1", "0.91")]
  [TestCase("lzexe100", "2", "0.91")]
  [Category("HappyPath")]
  public void Given_ALzexePackedProgram_When_Unpacked_Then_ItLoadsLikeTheOriginal(string set, string n, string variant) {
    var packed = Load($"{set}.P{n}.EXE");
    Assert.That(LzExeUnpacker.Instance.Detect(packed), Is.EqualTo(variant));
    var restored = LzExeUnpacker.Instance.Unpack(packed);
    Assert.That(restored.Variant, Is.EqualTo(variant));
    AssertLoadsLike(Load($"T{n}.EXE"), restored.Executable, $"{set} T{n}");
  }

  [TestCase("lzexe091", "1")]
  [TestCase("lzexe091", "2")]
  [TestCase("lzexe100", "1")]
  [TestCase("lzexe100", "2")]
  [Category("Oracle")]
  public void Given_ALzexePackedProgram_When_Unpacked_Then_ItIsUnpsOutputWithTheRecordedMinimumAllocation(string set, string n) {
    var restored = LzExeUnpacker.Instance.Unpack(Load($"{set}.P{n}.EXE")).Executable;
    var unp = Load($"lzexe.U{n}.EXE");
    var original = Load($"T{n}.EXE");
    // UNP writes its own minimum allocation; everything else is byte for byte.
    unp[10] = original[10];
    unp[11] = original[11];
    Assert.That(restored, Is.EqualTo(unp));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ALzexePackedProgram_When_ListedThroughTheDescriptor_Then_TheRestoredProgramIsAnEntry() {
    using var ms = new MemoryStream(Load("lzexe091.P2.EXE"));
    var d = new LzExeFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "unpacked.exe", "metadata.ini" }));
    var dir = Path.Combine(Path.GetTempPath(), "cwb_lzexe_" + Guid.NewGuid().ToString("N"));
    try {
      ms.Position = 0;
      d.Extract(ms, dir, null, null);
      AssertLoadsLike(Load("T2.EXE"), File.ReadAllBytes(Path.Combine(dir, "unpacked.exe")), "extracted");
      Assert.That(File.ReadAllText(Path.Combine(dir, "metadata.ini")), Does.Contain("variant = 0.91e").And.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ALzexePackedProgram_When_TheHandlerRegistryUnpacksIt_Then_LzexeRebuildsTheExecutable() {
    var packed = Load("lzexe100.P1.EXE");
    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match?.Handler.Id, Is.EqualTo("lzexe"));
    var result = ExecutablePackerHandlers.TryUnpack(packed)!;
    Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
    AssertLoadsLike(Load("T1.EXE"), result.Artifacts.Single(a => a.Name == "unpacked.exe").Data, "handler");
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnUnpackedProgram_When_Detected_Then_ItIsNotLzexe() {
    Assert.That(LzExeUnpacker.Instance.Detect(Load("T1.EXE")), Is.Null);
    Assert.That(() => LzExeUnpacker.Instance.Unpack(Load("T1.EXE")), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase(0)]
  [TestCase(27)]
  [TestCase(64)]
  [Category("EdgeCase")]
  public void Given_TooShortInput_When_Detected_Then_ItIsNotLzexe(int length) =>
    Assert.That(LzExeUnpacker.Instance.Detect(Load("lzexe091.P1.EXE")[..length]), Is.Null);

  [Test]
  [Category("Exception")]
  public void Given_ATruncatedLzexeFile_When_Unpacked_Then_ItIsRefusedAndTheDescriptorListsItPartially() {
    var packed = Load("lzexe091.P2.EXE");
    // Cutting the tail removes the stub's relocation table, so the stub is still recognised
    // (its entry bytes survive) but the program cannot be restored.
    var truncated = packed[..(packed.Length - 100)];
    Assert.That(LzExeUnpacker.Instance.Detect(truncated), Is.Not.Null);
    Assert.That(() => LzExeUnpacker.Instance.Unpack(truncated), Throws.InstanceOf<InvalidDataException>());
    using var ms = new MemoryStream(truncated);
    var entries = new LzExeFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "metadata.ini", "packed_payload.bin" }));
  }

  [Test]
  [Category("Exception")]
  public void Given_CorruptedCompressedBytes_When_Unpacked_Then_OnlyInvalidDataIsEverThrown() {
    var packed = Load("lzexe091.P2.EXE");
    var rng = new Random(91);
    for (var round = 0; round < 200; ++round) {
      var bad = (byte[])packed.Clone();
      for (var i = 0; i < 4; ++i)
        bad[rng.Next(0x20, 0x20 + 6000)] ^= (byte)rng.Next(1, 256);
      try {
        LzExeUnpacker.Instance.Unpack(bad);
      } catch (InvalidDataException) {
      }
    }
  }

  [Test]
  [Category("Exception")]
  public void Given_AnUnterminatedRelocationTable_When_Unpacked_Then_ItIsRefused() {
    var packed = Load("lzexe091.P2.EXE");
    var exe = MzView.Parse(packed);
    // The private header sits at CS:0; its last word is the stub length, which bounds the
    // relocation table. Shrinking it to the table's start leaves the table unterminated.
    var hdr = 16 * (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(8)) + exe.CS);
    var bad = (byte[])packed.Clone();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(hdr + 12), 344);
    Assert.That(() => LzExeUnpacker.Instance.Unpack(bad), Throws.InstanceOf<InvalidDataException>());
  }

  // ── live oracle ──────────────────────────────────────────────────────

  /// <summary>
  /// Packs T1/T2 with the real LZEXE under DOSBox, restores them with UNP 4.11 there, and checks
  /// both the packed and the restored programs still print OK and that ours equals UNP's output
  /// apart from the minimum allocation. Needs CWB_DOSBOX, CWB_TOOL_LZEXE (lzexe.exe of 0.91 or
  /// 1.00a) and CWB_TOOL_UNP (UNP.EXE 4.11).
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void Given_TheRealLzexeAndUnp_When_TheyPackAndRestore_Then_OursMatchesUnp() {
    RequireDosTools("LZEXE", "UNP");
    var drive = TempDrive("lzexe");
    try {
      File.Copy(Tool("LZEXE")!, Path.Combine(drive, "LZEXE.EXE"));
      File.Copy(Tool("UNP")!, Path.Combine(drive, "UNP.EXE"));
      var lines = new List<string>();
      foreach (var n in new[] { "1", "2" }) {
        File.WriteAllBytes(Path.Combine(drive, $"P{n}.EXE"), Load($"T{n}.EXE"));
        lines.AddRange([$"LZEXE P{n}.EXE > NUL", $"P{n}.EXE > R{n}.TXT", $"UNP e -k- P{n}.EXE U{n}.EXE > NUL", $"U{n}.EXE > RU{n}.TXT"]);
      }
      RunDos(drive, lines);
      foreach (var n in new[] { "1", "2" }) {
        Assert.That(ReadText(Path.Combine(drive, $"R{n}.TXT")), Does.StartWith("OK"), $"packed T{n} did not run");
        Assert.That(ReadText(Path.Combine(drive, $"RU{n}.TXT")), Does.StartWith("OK"), $"UNP-restored T{n} did not run");
        var ours = LzExeUnpacker.Instance.Unpack(File.ReadAllBytes(Path.Combine(drive, $"P{n}.EXE"))).Executable;
        var unp = File.ReadAllBytes(Path.Combine(drive, $"U{n}.EXE"));
        var original = Load($"T{n}.EXE");
        AssertLoadsLike(original, ours, $"T{n}");
        unp[10] = original[10];
        unp[11] = original[11];
        Assert.That(ours, Is.EqualTo(unp), $"T{n}: ours vs UNP");
      }
    } finally {
      try { Directory.Delete(drive, true); } catch (IOException) { }
    }
  }
}
