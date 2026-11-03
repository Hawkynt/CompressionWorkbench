using System.Security.Cryptography;
using Compression.Core.Dictionary.Ace;
using FileFormat.Ace;

namespace Compression.Tests.Ace;

/// <summary>
/// The ACE 2.0 writer (compression type 2): round trips through our reader, the filter each kind of
/// data gets, and the reference extractor — unace 2.5 (Marcel Lemke, the format's author) in WSL —
/// testing and extracting what we wrote.
/// </summary>
[TestFixture]
public sealed class AceBlockedWriterTests {
  private static readonly DateTime FixtureTime = new(2024, 5, 6, 7, 8, 10);

  internal static byte[] Text(int lines, int seed) {
    var r = new Random(seed);
    string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
    return System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n",
      Enumerable.Range(0, lines).Select(_ => string.Join(' ', Enumerable.Range(0, 9).Select(_ => words[r.Next(words.Length)])))));
  }

  internal static byte[] X86(int size, int seed) {
    var r = new Random(seed);
    var code = new List<byte>();
    var targets = Enumerable.Range(0, 40).Select(_ => r.Next(0, size)).ToArray();
    while (code.Count < size) {
      if (r.Next(4) == 0) {
        code.Add(0xE8);
        code.AddRange(BitConverter.GetBytes(targets[r.Next(targets.Length)] - code.Count - 4));
      } else
        code.AddRange([0x8B, 0x45, (byte)r.Next(16), 0x89, 0xC1]);
    }
    return [.. code.Take(size)];
  }

  internal static byte[] Table(int records) {
    var data = new byte[records * 8];
    for (var i = 0; i < records; ++i) {
      BitConverter.TryWriteBytes(data.AsSpan(8 * i), 100000 + 13 * i);
      BitConverter.TryWriteBytes(data.AsSpan(8 * i + 4), (short)(i % 7));
      BitConverter.TryWriteBytes(data.AsSpan(8 * i + 6), (ushort)0xBEEF);
    }
    return data;
  }

  internal static byte[] Random(int size, int seed) {
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
  }

  internal static (string Name, byte[] Data)[] Members() => [
    ("text.txt", Text(3000, 1)),
    ("code.exe", X86(60000, 2)),
    ("table.bin", Table(9000)),
    ("rand.bin", Random(20000, 3)),
    ("tiny.txt", "ACE 2.0"u8.ToArray()),
    ("empty.bin", []),
  ];

  internal static byte[] Archive(bool solid, string? password = null) {
    var w = new AceWriter(dictionaryBits: 16, password: password, solid: solid, ace20: true) { CreatedTime = FixtureTime };
    w.AddDirectory("dir", FixtureTime);
    foreach (var (name, data) in Members())
      w.AddFile("dir/" + name, data, FixtureTime);
    return w.ToArray();
  }

  [TestCase(false, null)]
  [TestCase(true, null)]
  [TestCase(true, "secret")]
  public void GivenAnAce20Archive_WhenReadBack_ThenEveryMemberIsRestored(bool solid, string? password) {
    var archive = Archive(solid, password);
    using var r = new AceReader(new MemoryStream(archive), password: password);
    Assert.That(r.ExtractVersion, Is.EqualTo(20));
    foreach (var (name, data) in Members()) {
      var entry = r.Entries.Single(e => e.FileName == "dir/" + name);
      if (data.Length > 0)
        Assert.That(entry.CompressionType, Is.EqualTo(Compression.Core.Dictionary.Ace.AceConstants.CompAce20), name);
      Assert.That(r.ExtractEntry(entry), Is.EqualTo(data), name);
    }
  }

  [Test]
  public void GivenEachKindOfData_WhenCompressed_ThenTheMatchingFilterIsChosen() {
    var c = new AceBlockedCompressor(16);
    c.Compress(X86(60000, 2));
    Assert.That(c.LastMode, Is.EqualTo("exe"));
    c.Compress(Table(9000));
    Assert.That(c.LastMode, Does.StartWith("delta:"));
    c.Compress(Text(3000, 1));
    Assert.That(c.LastMode, Is.EqualTo("lz77"));
  }

  [Test]
  public void GivenTheFilters_WhenCompressed_ThenAce20IsSmallerThanAce10OnCodeAndTables() {
    foreach (var data in new[] { X86(60000, 2), Table(9000) }) {
      var ace10 = new AceCompressor(16).Compress(data).Length;
      var ace20 = new AceBlockedCompressor(16).Compress(data).Length;
      Assert.That(ace20, Is.LessThan(ace10));
    }
  }

  [TestCase(130_000)]
  [TestCase(131_071)]
  [TestCase(300_001)]
  public void GivenDeltaDataAcrossSeveralBlocks_WhenRoundTripped_ThenItIsRestored(int size) {
    var data = Table(size / 8 + 1).AsSpan(0, size).ToArray();
    var w = new AceWriter(dictionaryBits: 17, ace20: true);
    w.AddFile("t.bin", data);
    using var r = new AceReader(new MemoryStream(w.ToArray()));
    Assert.That(r.ExtractEntry(r.Entries[0]), Is.EqualTo(data));
  }

  [Test]
  public void GivenX86WithACallInTheLastBytes_WhenRoundTripped_ThenItIsRestored() {
    var data = X86(5000, 9);
    data[^3] = 0xE8;
    data[^5] = 0xE9;
    var w = new AceWriter(ace20: true);
    w.AddFile("c.exe", data);
    using var r = new AceReader(new MemoryStream(w.ToArray()));
    Assert.That(r.ExtractEntry(r.Entries[0]), Is.EqualTo(data));
  }

  [TestCase(false, null)]
  [TestCase(true, null)]
  [TestCase(true, "secret")]
  [Category("ExternalInterop")]
  public void GivenOurAce20Archive_WhenUnaceTestsAndExtractsIt_ThenEveryMemberMatches(bool solid, string? password) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("unace"))
      Assert.Ignore("unace 2.5 not installed: `sudo apt-get install -y unace-nonfree`.");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ace20-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.ace");
      File.WriteAllBytes(path, Archive(solid, password));
      var pw = password == null ? "" : "-p" + password;
      var test = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(dir)} && timeout 120 unace t -y {pw} ours.ace");
      TestContext.Out.WriteLine(test.StdOut + test.StdErr);
      Assert.That(test.ExitCode, Is.Zero, "unace t refused our archive");
      // Extract on the Linux side: on a mounted Windows drive unace cannot set directory times and reports that as an error.
      var extract = FsInteropToolbox.RunWsl(
        $"d=$(mktemp -d) && cp {FsInteropToolbox.WinToWsl(path)} $d/ && cd $d && timeout 120 unace x -y {pw} ours.ace >/dev/null && sha256sum dir/*; r=$?; rm -rf $d; exit $r");
      TestContext.Out.WriteLine(extract.StdOut + extract.StdErr);
      Assert.That(extract.ExitCode, Is.Zero, "unace x refused our archive");
      foreach (var (name, data) in Members())
        Assert.That(extract.StdOut, Does.Contain(Convert.ToHexStringLower(SHA256.HashData(data)) + "  dir/" + name), name);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
