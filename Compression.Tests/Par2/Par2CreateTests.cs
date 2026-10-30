using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Par2;

namespace Compression.Tests.Par2;

/// <summary>
/// PAR2 recovery-set creation: the Reed–Solomon arithmetic the specification fixes, the packet
/// framing, and par2cmdline (the reference client) verifying and repairing from what we write.
/// </summary>
[TestFixture]
public class Par2CreateTests {

  private static readonly byte[] TextFile = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello par2 recovery set\n", 40)));
  private static readonly byte[] BinaryFile = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 31 + 7))];

  private static List<(string Type, byte[] Packet)> Packets(byte[] set) {
    var result = new List<(string, byte[])>();
    var pos = 0;
    while (pos + 64 <= set.Length) {
      Assert.That(set.AsSpan(pos, 8).SequenceEqual("PAR2\0PKT"u8), $"packet magic at {pos}");
      var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(set.AsSpan(pos + 8));
      result.Add((Encoding.ASCII.GetString(set, pos + 48, 16).Replace("\0", "|"), set[pos..(pos + length)]));
      pos += length;
    }
    Assert.That(pos, Is.EqualTo(set.Length));
    return result;
  }

  private static byte[] Create(int sliceSize, int recovery, params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    Par2Writer.Write(ms, [.. files.Select(f => new Par2Writer.SourceFile(f.Name, f.Data))], sliceSize, recovery);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Given_TheSpecification_When_ConstantsAreGenerated_Then_TheyMatchItsFirstEleven() {
    var expected = new ushort[] { 2, 4, 16, 128, 256, 2048, 8192, 16384, 4107, 32856, 17132 };
    var n = 0;
    var actual = new List<ushort>();
    for (var i = 0; i < expected.Length; ++i) {
      n = Par2Writer.NextConstantExponent(n);
      actual.Add(Par2Writer.Gf16.Exp[n]);
    }
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void Given_Gf16_When_EveryNonZeroElementIsMultipliedByItsInverse_Then_One() {
    for (var a = 1; a < 65536; a += 97) {
      var inverse = Par2Writer.Gf16.Exp[65535 - Par2Writer.Gf16.Log[a]];
      Assert.That(Par2Writer.Gf16.Multiply((ushort)a, inverse), Is.EqualTo((ushort)1), $"a={a}");
    }
  }

  [Test, Category("HappyPath")]
  public void Given_TwoFiles_When_SetCreated_Then_EveryPacketChecksumsAndCarriesTheSetId() {
    var set = Create(512, 4, ("a.txt", TextFile), ("dir/b.bin", BinaryFile));
    var packets = Packets(set);
    Assert.That(packets.Select(p => p.Type).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()), Is.EqualTo(new Dictionary<string, int> {
      ["PAR 2.0|Main||||"] = 1, ["PAR 2.0|FileDesc"] = 2, ["PAR 2.0|IFSC||||"] = 2, ["PAR 2.0|RecvSlic"] = 4, ["PAR 2.0|Creator|"] = 1,
    }));
    var main = packets.Single(p => p.Type.StartsWith("PAR 2.0|Main", StringComparison.Ordinal)).Packet;
    var setId = MD5.HashData(main.AsSpan(64));
    foreach (var (type, packet) in packets) {
      Assert.That(packet.AsSpan(16, 16).ToArray(), Is.EqualTo(MD5.HashData(packet.AsSpan(32))), type);
      Assert.That(packet.AsSpan(32, 16).ToArray(), Is.EqualTo(setId), type);
    }
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(main.AsSpan(64)), Is.EqualTo(512UL));
  }

  [Test, Category("HappyPath")]
  public void Given_SetCreated_When_ListedByOurReader_Then_FilesIniNamesBothFiles() {
    var d = new Par2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", TextFile), ArchiveInputInfo.InMemory("dir\\b.bin", BinaryFile)], new FormatCreateOptions());
    ms.Position = 0;
    var tmp = Path.Combine(Path.GetTempPath(), "par2c_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      d.Extract(ms, tmp, null, ["files.ini"]);
      var ini = File.ReadAllText(Path.Combine(tmp, "files.ini"));
      Assert.That(ini, Does.Contain("name=a.txt").And.Contain("name=dir/b.bin").And.Contain("length=3000"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Given_RecoverySlices_When_TwoInputSlicesAreLost_Then_ExponentsZeroAndOneRebuildThem() {
    // Three input slices of one file; lose slices 0 and 2 and solve the 2x2 system from recovery 0 and 1.
    const int slice = 64;
    var data = Enumerable.Range(0, 3 * slice).Select(i => (byte)(i * 7 + 3)).ToArray();
    var rec = Par2Writer.ComputeRecovery([data], slice, 2);
    var c = new ushort[3];
    var n = 0;
    for (var i = 0; i < 3; ++i) c[i] = Par2Writer.Gf16.Exp[n = Par2Writer.NextConstantExponent(n)];
    for (var w = 0; w < slice / 2; ++w) {
      ushort Word(int s) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(s * slice + 2 * w));
      var r0 = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(rec[0].AsSpan(2 * w)) ^ Word(1));
      var r1 = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(rec[1].AsSpan(2 * w)) ^ Par2Writer.Gf16.Multiply(c[1], Word(1)));
      // x0 + x2 = r0 ; c0 x0 + c2 x2 = r1  =>  x0 = (r1 + c2 r0) / (c0 + c2)
      var numerator = (ushort)(r1 ^ Par2Writer.Gf16.Multiply(c[2], r0));
      var denominator = (ushort)(c[0] ^ c[2]);
      var x0 = Par2Writer.Gf16.Multiply(numerator, Par2Writer.Gf16.Exp[65535 - Par2Writer.Gf16.Log[denominator]]);
      Assert.That(x0, Is.EqualTo(Word(0)), $"word {w}");
      Assert.That((ushort)(r0 ^ x0), Is.EqualTo(Word(2)), $"word {w}");
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_FileEndingMidSlice_When_Checksummed_Then_TheSliceIsZeroPadded() {
    var set = Create(512, 1, ("odd.bin", [1, 2, 3, 4, 5]));
    var ifsc = Packets(set).Single(p => p.Type.StartsWith("PAR 2.0|IFSC", StringComparison.Ordinal)).Packet;
    var padded = new byte[512];
    new byte[] { 1, 2, 3, 4, 5 }.CopyTo(padded, 0);
    Assert.That(ifsc.AsSpan(64 + 16, 16).ToArray(), Is.EqualTo(MD5.HashData(padded)));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(ifsc.AsSpan(64 + 32)), Is.EqualTo(Compression.Core.Checksums.Crc32.Compute(padded)));
  }

  [Test, Category("EdgeCase")]
  public void Given_ZeroRecoverySlices_When_Created_Then_IndexOnlySet()
    => Assert.That(Packets(Create(512, 0, ("a.txt", TextFile))).Any(p => p.Type.Contains("RecvSlic")), Is.False);

  [Test, Category("EdgeCase")]
  public void Given_NoSliceSize_When_Created_Then_AMultipleOfFourNearTwoThousandSlicesIsChosen() {
    var set = Create(0, 1, ("big.bin", new byte[100_003]));
    var main = Packets(set).Single(p => p.Type.StartsWith("PAR 2.0|Main", StringComparison.Ordinal)).Packet;
    var slice = BinaryPrimitives.ReadUInt64LittleEndian(main.AsSpan(64));
    Assert.That(slice % 4, Is.Zero);
    Assert.That((100_003 + (long)slice - 1) / (long)slice, Is.InRange(1900, 2000));
  }

  [TestCase(510)]
  [TestCase(-4)]
  [Category("EdgeCase")]
  public void Given_InvalidSliceSize_When_Created_Then_Refused(int slice)
    => Assert.Throws<ArgumentException>(() => Create(slice, 1, ("a", [1])));

  [Test, Category("EdgeCase")]
  public void Given_MoreThan32768InputSlices_When_Created_Then_Refused()
    => Assert.Throws<ArgumentException>(() => Create(4, 1, ("a", new byte[4 * 32769])));

  [Test, Category("EdgeCase")]
  public void Given_NoFilesOrDuplicateNames_When_Created_Then_Refused() {
    Assert.Throws<ArgumentException>(() => Create(4, 1));
    Assert.Throws<ArgumentException>(() => Create(4, 1, ("a", [1]), ("a", [2])));
    Assert.Throws<ArgumentException>(() => Create(4, 1, ("/abs", [1])));
  }

  // ── par2cmdline's own set as the reference ───────────────────────────────

  private static byte[] Vector(string name) {
    using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"Par2Vectors.{name}")
      ?? throw new FileNotFoundException($"Embedded PAR2 vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>Every packet as (set id, type, body), the creator packet left out (its text names the program).</summary>
  private static List<(string SetId, string Type, string Body)> PacketBodies(params byte[][] files) {
    var list = new List<(string, string, string)>();
    foreach (var f in files)
      for (var p = 0; p + 64 <= f.Length;) {
        Assert.That(f.AsSpan(p, 8).SequenceEqual("PAR2\0PKT"u8), Is.True, $"packet magic at {p}");
        var length = (int)BitConverter.ToUInt64(f, p + 8);
        var type = Encoding.ASCII.GetString(f, p + 48, 16).TrimEnd('\0');
        if (type != "PAR 2.0\0Creator".TrimEnd('\0'))
          list.Add((Convert.ToHexString(f, p + 32, 16), type, Convert.ToHexString(f, p + 64, length - 64)));
        p += length;
      }
    return [.. list.Distinct().OrderBy(x => x.Item2, StringComparer.Ordinal).ThenBy(x => x.Item3, StringComparer.Ordinal)];
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Par2cmdlineInputs_When_WeCreate_Then_SetIdAndEveryPacketMatchPar2cmdline() {
    // par2cmdline 0.8.1: par2 create -s512 -c4 -n1 set.par2 a.txt b.bin
    using var ms = new MemoryStream();
    new Par2FormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("a.txt", Vector("a.txt")), ArchiveInputInfo.InMemory("b.bin", Vector("b.bin"))],
      new FormatCreateOptions { FormatSpecific = new() { ["slicesize"] = "512", ["recoveryslices"] = "4" } });
    var ours = PacketBodies(ms.ToArray());
    var theirs = PacketBodies(Vector("set.par2"), Vector("set.vol0+4.par2"));
    Assert.That(ours.Select(p => p.SetId).Distinct(), Is.EqualTo(theirs.Select(p => p.SetId).Distinct()));
    Assert.That(ours, Is.EqualTo(theirs));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Par2cmdlineSet_When_Read_Then_FilesIniNamesBothProtectedFiles() {
    var dir = Path.Combine(Path.GetTempPath(), "par2l_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      new Par2FormatDescriptor().Extract(new MemoryStream(Vector("set.par2")), dir, null, ["files.ini"]);
      var ini = File.ReadAllText(Path.Combine(dir, "files.ini"));
      Assert.That(ini, Does.Contain("a.txt").And.Contain("960"));
      Assert.That(ini, Does.Contain("b.bin").And.Contain("3000"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  // ── par2cmdline ─────────────────────────────────────────────────────────

  private static void RequirePar2() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed; par2cmdline is the oracle for PAR2 creation.");
    if (!FsInteropToolbox.WslHasTool("par2"))
      Assert.Ignore("par2cmdline is not installed in WSL: sudo apt install -y par2");
  }

  [Test, Category("ExternalInterop")]
  public void Given_OurSet_When_Par2cmdlineVerifiesAndRepairs_Then_DamagedFileIsRestored() {
    RequirePar2();
    var dir = Path.Combine(Path.GetTempPath(), "par2o_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Combine(dir, "dir"));
    try {
      File.WriteAllBytes(Path.Combine(dir, "a.txt"), TextFile);
      File.WriteAllBytes(Path.Combine(dir, "dir", "b.bin"), BinaryFile);
      using (var fs = File.Create(Path.Combine(dir, "set.par2")))
        new Par2FormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("a.txt", TextFile), ArchiveInputInfo.InMemory("dir/b.bin", BinaryFile)],
          new FormatCreateOptions { FormatSpecific = new() { ["slicesize"] = "256", ["recoveryslices"] = "3" } });
      var w = FsInteropToolbox.WinToWsl(dir);

      var verify = FsInteropToolbox.RunWsl($"cd {w} && par2 verify set.par2");
      Assert.That(verify.ExitCode, Is.Zero, verify.StdOut + verify.StdErr);
      Assert.That(verify.StdOut, Does.Contain("All files are correct"));

      // Damage two slices of b.bin and delete a.txt-sized region? Damage within b.bin's first and last slice.
      var damaged = (byte[])BinaryFile.Clone();
      damaged[10] ^= 0xFF;
      damaged[2999] ^= 0x55;
      File.WriteAllBytes(Path.Combine(dir, "dir", "b.bin"), damaged);
      var repair = FsInteropToolbox.RunWsl($"cd {w} && par2 repair set.par2");
      Assert.That(repair.ExitCode, Is.Zero, repair.StdOut + repair.StdErr);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "dir", "b.bin")), Is.EqualTo(BinaryFile));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
