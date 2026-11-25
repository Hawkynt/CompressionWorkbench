using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Arc;

namespace Compression.Tests.Arc;

/// <summary>
/// PAK method 10 (Crushed). Hand-built code streams pin the code modes, string definition and
/// the end code. Real data: NoGate Consulting's own PAK 2.51 distribution, <c>pak251.exe</c>
/// (103,338 bytes, Garbo/Simtel <c>arcers/</c>). It is a PAK self-extractor whose READ.ME is
/// Crushed and whose other eight members are Distilled (Arc/ReferenceVectors/README.md). The Crushed
/// member must pass the CRC-16 PAK stored, and every member The Unarchiver's unar (XADMaster) extracts
/// must equal ours.
/// </summary>
[TestFixture]
public class ArcCrushedTests {
  private const string Pak251 = "pak251.exe";
  private const string Pak251Sha256 = "0a3195a3a23f4d9526a048abf549f6d2b403fbe7b79308d4531c7854a0fa5417";
  private const int Pak251ArchiveOffset = 0x1AD3;

  /// <summary>Packs codes least-significant bit first.</summary>
  private static byte[] Pack(params (int Value, int Bits)[] codes) {
    var bytes = new List<byte>();
    int acc = 0, n = 0;
    foreach (var (value, bits) in codes) {
      acc |= value << n;
      n += bits;
      while (n >= 8) {
        bytes.Add((byte)acc);
        acc >>= 8;
        n -= 8;
      }
    }

    if (n > 0) bytes.Add((byte)acc);
    return [.. bytes];
  }

  // In the starting mode every code is a flag bit, then 8 literal bits (flag 0) or a string code of the current width (flag 1).
  private static (int, int)[] Literal(char c) => [(0, 1), (c, 8)];
  private static (int, int)[] String(int symbol, int width) => [(1, 1), (symbol - 256, width)];

  private static string Decode(byte[] data) => Encoding.Latin1.GetString(ArcCrushed.Decode(data));

  [Test, Category("HappyPath")]
  public void GivenLiteralsAndTheEndCode_WhenDecoded_ThenTheLiteralsComeOut() {
    var data = Pack([.. Literal('A'), .. Literal('B'), .. String(256, 1)]);
    Assert.That(Decode(data), Is.EqualTo("AB"));
  }

  [Test, Category("HappyPath")]
  public void GivenADefinedString_WhenDecoded_ThenItExpandsAndTheCodeWidens() {
    // 'A','B' define 257 = "AB"; 257 then defines 258 = "BA", and two strings widen codes to 2 bits.
    var data = Pack([.. Literal('A'), .. Literal('B'), .. String(257, 1), .. String(258, 2), .. String(256, 2)]);
    Assert.That(Decode(data), Is.EqualTo("AB" + "AB" + "BA"));
  }

  [Test, Category("Boundary")]
  public void GivenTheStringBeingDefined_WhenDecoded_ThenItIsThePreviousStringPlusItsFirstByte() {
    var data = Pack([.. Literal('A'), .. String(257, 1), .. String(256, 1)]);
    Assert.That(Decode(data), Is.EqualTo("AAA"));
  }

  [Test, Category("Boundary")]
  public void GivenOnlyTheEndCode_WhenDecoded_ThenNothingComesOut() =>
    Assert.That(ArcCrushed.Decode(Pack(String(256, 1))), Is.Empty);

  [Test, Category("Exception")]
  public void GivenAStringCodeBeforeAnyStringExists_WhenDecoded_ThenItIsRefused() {
    var data = Pack([.. String(257, 1), .. Literal('A')]);
    Assert.That(() => ArcCrushed.Decode(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenOutputBeyondTheLimit_WhenDecoded_ThenItIsRefused() {
    var data = Pack([.. Literal('A'), .. Literal('B'), .. String(256, 1)]);
    Assert.That(() => ArcCrushed.Decode(data, maxOutput: 1), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenDataWithoutTheEndCode_WhenDecoded_ThenItIsRefused() {
    var data = Pack([.. Literal('A'), .. Literal('B')]);
    Assert.That(() => ArcCrushed.Decode(data), Throws.InstanceOf<InvalidDataException>());
  }

  private static byte[] LoadPak251() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ArcVectors.{Pak251}")
      ?? throw new InvalidOperationException($"missing embedded vector {Pak251}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(Pak251Sha256), "not the pinned pak251.exe");
    return bytes;
  }

  /// <summary>Every member with its bytes, or the reason it was refused.</summary>
  private static List<(ArcEntry Entry, byte[]? Data, string? Error)> ReadAll(byte[] archive) {
    var result = new List<(ArcEntry, byte[]?, string?)>();
    using var reader = new ArcReader(new MemoryStream(archive));
    while (reader.GetNextEntry() is { } e) {
      try {
        result.Add((e, reader.ReadEntryData(), null));
      } catch (InvalidDataException ex) {
        result.Add((e, null, ex.Message));
      }
    }

    return result;
  }

  [Test, Category("ThemVsUs")]
  public void GivenNoGatesPak251Distribution_WhenItsCrushedMemberIsRead_ThenItPassesItsStoredCrc() {
    var exe = LoadPak251();
    var members = ReadAll(exe[Pak251ArchiveOffset..]);
    Assert.That(members.Select(m => (m.Entry.FileName, (int)m.Entry.Method)), Is.EqualTo(new[] {
      ("READ.ME", 10), ("PAK.EXE", 11), ("PAK.DOC", 11), ("PAKINST.EXE", 11), ("ORDER.FRM", 11),
      ("UPDATE.FRM", 11), ("SITE.FRM", 11), ("STDCFG.TXT", 11), ("ALTCFG.TXT", 11),
    }));
    foreach (var m in members) TestContext.Out.WriteLine($"{m.Entry.FileName} method {m.Entry.Method}: {m.Error ?? "ok"}");

    // ReadEntryData checks the CRC-16 PAK stored and throws on a mismatch.
    var readMe = members[0];
    Assert.That(readMe.Error, Is.Null);
    Assert.That(readMe.Data, Has.Length.EqualTo(953));
    Assert.That(Encoding.ASCII.GetString(readMe.Data!), Does.Contain("The following files are contained in the archive PAK251.EXE:"));
  }

  [Test, Category("ExternalInterop")]
  public void GivenNoGatesPak251Archive_WhenUnarExtractsIt_ThenTheMembersItExtractsEqualOurs() {
    var exe = LoadPak251();
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_pak_crush_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      var archive = exe[Pak251ArchiveOffset..];
      var path = Path.Combine(tmp, "pak251.pak");
      File.WriteAllBytes(path, archive);
      var outDir = Path.Combine(tmp, "out");
      // unar refuses some Distilled members (wrong checksum) and then exits non-zero; the rest are written.
      var r = FsInteropToolbox.RunWsl($"unar -q -f -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      TestContext.Out.WriteLine($"unar exit {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");

      var compared = new List<string>();
      foreach (var (entry, data, error) in ReadAll(archive)) {
        var theirs = Path.Combine(outDir, entry.FileName);
        TestContext.Out.WriteLine($"{entry.FileName} method {entry.Method}: ours {error ?? "ok"}, unar {(File.Exists(theirs) ? "wrote it" : "none")}");
        // unar still writes a member whose checksum it rejects; those bytes are not its answer.
        var unarRejected = (r.StdOut + r.StdErr).Split('\n').Any(l => l.Contains(entry.FileName, StringComparison.Ordinal) && l.Contains("Failed", StringComparison.Ordinal));
        if (data == null || !File.Exists(theirs) || unarRejected) continue;
        Assert.That(data, Is.EqualTo(File.ReadAllBytes(theirs)), $"{entry.FileName} (method {entry.Method})");
        compared.Add(entry.FileName);
      }

      Assert.That(compared, Does.Contain("READ.ME"), "the Crushed member was not compared");
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
