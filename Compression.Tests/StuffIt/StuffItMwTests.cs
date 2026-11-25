using System.Security.Cryptography;
using System.Text;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// StuffIt method 8 (MW). Hand-built code streams pin the decoder's rules (phrase joining, width
/// growth, run reset, end code); two archives StuffIt 1.x wrote (ObsoleteMadness/StuffIt-Go
/// <c>samples/</c>, revision 915dc49) pin it on real data: every fork must pass the CRC-16 StuffIt
/// stored, and The Unarchiver's unar (XADMaster) must extract the same bytes. Those archives hold an
/// Apple document, so they stay out of the repository: set <c>CWB_STUFFIT_MW_DIR</c> to a folder
/// holding them.
/// </summary>
[TestFixture]
public class StuffItMwTests {

  /// <summary>The two StuffIt-Go samples whose "About System 7.5" forks are both method 8.</summary>
  private static readonly Dictionary<string, string> RealSamples = new() {
    ["v1-huffman-optimal.sit"] = "d048628a6058943e71451bd2b3ee41d61085d24dfde8ea140e3b865e31a9bbc3",
    ["v1-optimal-with-comment.sit"] = "3802a09b94e47224ab037c77b078b109b8bc37a849374ed74b04349234f45e19",
  };

  /// <summary>Packs codes least-significant bit first, each with its own width.</summary>
  private static byte[] Pack(params (int Code, int Bits)[] codes) {
    var bytes = new List<byte>();
    int acc = 0, n = 0;
    foreach (var (code, bits) in codes) {
      acc |= code << n;
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

  private static string Decode(byte[] stream, int length) => Encoding.Latin1.GetString(StuffItMwDecoder.Decode(stream, length));

  [Test, Category("HappyPath")]
  public void GivenLiteralCodes_WhenDecoded_ThenTheBytesComeOut() {
    var stream = Pack(('A', 9), ('B', 9), ('C', 9));
    Assert.That(Decode(stream, 3), Is.EqualTo("ABC"));
  }

  [Test, Category("HappyPath")]
  public void GivenAnEntryCode_WhenDecoded_ThenItJoinsThePreviousAndTheCurrentPhrase() {
    // 'a' (kept as dict[255]), 'b' makes entry 256 = "a"+"b", then 256 itself, then 257 = "b"+"ab".
    var stream = Pack(('a', 9), ('b', 9), (256, 9), (257, 9));
    Assert.That(Decode(stream, 2 + 2 + 3), Is.EqualTo("ab" + "ab" + "bab"));
  }

  [Test, Category("HappyPath")]
  public void GivenTheNextFreeCode_WhenDecoded_ThenTheRunRestartsWithAnEmptyDictionary() {
    // After 'a','b' the next free entry is 257: reading 257 resets; 'x','y',256 then means "xy".
    var stream = Pack(('a', 9), ('b', 9), (257, 9), ('x', 9), ('y', 9), (256, 9));
    Assert.That(Decode(stream, 6), Is.EqualTo("abxyxy"));
  }

  [Test, Category("Boundary")]
  public void GivenTheDictionaryReachesFiveHundredTwelve_WhenDecoded_ThenCodesWidenToTenBits() {
    // 1 first code + 256 codes fill entries 256..511; the 257th following code is read with 10 bits.
    var codes = new List<(int, int)> { ('a', 9) };
    for (var i = 0; i < 256; ++i) codes.Add(('a' + i % 26, 9));
    codes.Add((300, 10)); // entry 300 = phrase(dict[299]) + phrase(dict[300]): two literals
    var expected = new StringBuilder("a");
    for (var i = 0; i < 256; ++i) expected.Append((char)('a' + i % 26));
    expected.Append((char)('a' + 43 % 26)).Append((char)('a' + 44 % 26));

    Assert.That(Decode(Pack([.. codes]), expected.Length), Is.EqualTo(expected.ToString()));
  }

  [Test, Category("Boundary")]
  public void GivenAnEmptyFork_WhenDecoded_ThenNothingIsRead() =>
    Assert.That(StuffItMwDecoder.Decode([], 0), Is.Empty);

  [Test, Category("Exception")]
  public void GivenAnEndCodeBeforeTheDeclaredLength_WhenDecoded_ThenItIsRefused() {
    var stream = Pack(('a', 9), ('b', 9), (400, 9));
    Assert.That(() => StuffItMwDecoder.Decode(stream, 5), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenATruncatedFork_WhenDecoded_ThenItIsRefused() {
    var stream = Pack(('a', 9), ('b', 9));
    Assert.That(() => StuffItMwDecoder.Decode(stream, 1000), Throws.InstanceOf<InvalidDataException>());
  }

  private static string? SampleDir() {
    var dir = Environment.GetEnvironmentVariable("CWB_STUFFIT_MW_DIR");
    return string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? null : dir;
  }

  private static byte[] LoadSample(string dir, string name) {
    var bytes = File.ReadAllBytes(Path.Combine(dir, name));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(RealSamples[name]), $"{name} is not the pinned sample");
    return bytes;
  }

  [TestCase("v1-huffman-optimal.sit")]
  [TestCase("v1-optimal-with-comment.sit")]
  [Category("ThemVsUs")]
  public void GivenAnArchiveStuffIt1Wrote_WhenItsMwForksAreExtracted_ThenTheirStoredCrcsMatch(string name) {
    if (SampleDir() is not { } dir) {
      Assert.Ignore("CWB_STUFFIT_MW_DIR is not set.");
      return;
    }

    using var reader = new StuffItReader(new MemoryStream(LoadSample(dir, name)));
    var entry = reader.Entries.Single(e => e.FileName == "About System 7.5");
    Assert.That((entry.DataMethod, entry.ResourceMethod), Is.EqualTo((8, 8)));
    // Extract verifies each fork's CRC-16 and throws on a mismatch.
    Assert.That(reader.Extract(entry), Has.Length.EqualTo(entry.DataForkSize));
    Assert.That(reader.ExtractResourceFork(entry), Has.Length.EqualTo(entry.ResourceForkSize));
  }

  [Test, Category("ExternalInterop")]
  public void GivenTheRealMwArchives_WhenUnarExtractsThem_ThenTheirDataForksEqualOurs() {
    if (SampleDir() is not { } dir) {
      Assert.Ignore("CWB_STUFFIT_MW_DIR is not set.");
      return;
    }

    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sit_mw_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      foreach (var name in RealSamples.Keys) {
        var path = Path.Combine(tmp, name);
        File.WriteAllBytes(path, LoadSample(dir, name));
        var outDir = path + ".out";
        var r = FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
        Assert.That(r.ExitCode, Is.EqualTo(0), $"unar failed on {name}:\n{r.StdOut}\n{r.StdErr}");

        using var reader = new StuffItReader(File.OpenRead(path));
        var entry = reader.Entries.Single(e => e.FileName == "About System 7.5");
        var theirs = Path.Combine(outDir, entry.FileName);
        Assert.That(File.Exists(theirs), $"unar wrote no data fork for {name}");
        Assert.That(reader.Extract(entry), Is.EqualTo(File.ReadAllBytes(theirs)), name);
      }
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
