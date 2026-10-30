using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.ChromiumPak;

namespace Compression.Tests.ChromiumPak;

/// <summary>
/// Chromium resource packs. Oracle: grit's <c>data_pack.py</c> (Chromium, BSD-3-Clause), whose reader and
/// writers are copied into <c>ReferenceVectors/grit_oracle.py</c>. The <c>grit-*.pak</c> fixtures are its
/// output and <c>grit-listing.txt</c> its reading of them. Packs of an installed Chrome, when present, are
/// checked too. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class ChromiumPakTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ChromiumPakVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded PAK vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly byte[] Png = [.. Convert.FromHexString("89504e470d0a1a0a"), .. Enumerable.Range(0, 512).Select(i => (byte)i)];

  /// <summary>The resources grit_oracle.py's <c>make</c> packed into each fixture.</summary>
  private static Dictionary<ushort, byte[]> GritInputs(string vector) => vector switch {
    "grit-v5-utf8.pak" => new() {
      [101] = "hello pak"u8.ToArray(), [102] = Png, [103] = "hello pak"u8.ToArray(), [200] = [],
      [4096] = Encoding.UTF8.GetBytes("grüße"), [65535] = "last"u8.ToArray(),
    },
    "grit-v5-binary.pak" => new() { [1] = Png, [2] = [0, 1, 2] },
    "grit-v5-empty.pak" => [],
    "grit-v4-utf8.pak" => new() { [7] = "seven"u8.ToArray(), [8] = [], [300] = Png },
    _ => throw new ArgumentException(vector),
  };

  /// <summary>grit's listing: <c># file version=V encoding=E resources=N aliases=A</c>, then <c>file id length sha256 aliasOf|-</c>.</summary>
  private static void AssertMatchesGrit(byte[] pack, string listing, string file) {
    var lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' ')).Where(l => l[0] == file || (l[0] == "#" && l[1] == file)).ToList();
    var header = lines.Single(l => l[0] == "#");
    var (version, encoding, resources) = FileFormat.ChromiumPak.ChromiumPak.Read(pack);
    Assert.That($"version={version}", Is.EqualTo(header[2]));
    Assert.That($"encoding={(int)encoding}", Is.EqualTo(header[3]));
    Assert.That($"resources={resources.Count}", Is.EqualTo(header[4]));
    Assert.That($"aliases={resources.Count(r => r.AliasOf.HasValue)}", Is.EqualTo(header[5]));
    var rows = lines.Where(l => l[0] != "#").ToList();
    Assert.That(resources.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)), Is.EqualTo(rows.Select(l => l[1])));
    for (var i = 0; i < rows.Count; ++i) {
      var r = resources[i];
      Assert.That(r.Data.Length.ToString(CultureInfo.InvariantCulture), Is.EqualTo(rows[i][2]), $"resource {r.Id}");
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(r.Data)), Is.EqualTo(rows[i][3]), $"resource {r.Id}");
      Assert.That(r.AliasOf?.ToString(CultureInfo.InvariantCulture) ?? "-", Is.EqualTo(rows[i][4]), $"resource {r.Id}");
    }
  }

  [TestCase("grit-v5-utf8.pak")]
  [TestCase("grit-v5-binary.pak")]
  [TestCase("grit-v5-empty.pak")]
  [TestCase("grit-v4-utf8.pak")]
  [Category("ThirdPartyVector")]
  public void Given_GritPack_When_Read_Then_MatchesGritListing(string vector)
    => AssertMatchesGrit(Vector(vector), Encoding.UTF8.GetString(Vector("grit-listing.txt")), vector);

  [TestCase("grit-v5-utf8.pak", ChromiumPakEncoding.Utf8, 5u)]
  [TestCase("grit-v5-binary.pak", ChromiumPakEncoding.Binary, 5u)]
  [TestCase("grit-v5-empty.pak", ChromiumPakEncoding.Utf8, 5u)]
  [TestCase("grit-v4-utf8.pak", ChromiumPakEncoding.Utf8, 4u)]
  [Category("ThirdPartyVector")]
  public void Given_GritInputs_When_WeWrite_Then_ByteIdenticalToGrit(string vector, ChromiumPakEncoding encoding, uint version) {
    using var ms = new MemoryStream();
    FileFormat.ChromiumPak.ChromiumPak.Write(ms, GritInputs(vector), encoding, version);
    Assert.That(ms.ToArray(), Is.EqualTo(Vector(vector)));
  }

  [Test, Category("HappyPath")]
  public void Given_PackWithAlias_When_Listed_Then_AliasNamesItsTarget() {
    var list = new ChromiumPakFormatDescriptor().List(new MemoryStream(Vector("grit-v5-utf8.pak")), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "101", "102", "103", "200", "4096", "65535" }));
    Assert.That(list.Single(e => e.Name == "103").Method, Is.EqualTo("alias of 101"));
    Assert.That(list.Single(e => e.Name == "102").OriginalSize, Is.EqualTo(Png.Length));
  }

  [TestCase("grit-v5-utf8.pak")]
  [TestCase("grit-v4-utf8.pak")]
  [Category("HappyPath")]
  public void Given_PakPath_When_Detected_Then_ChromiumPakNotQuakeOrUnreal(string vector) {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_cpak_{Guid.NewGuid():N}.pak");
    File.WriteAllBytes(path, Vector(vector));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("ChromiumPak"));
    } finally { File.Delete(path); }
  }

  [Test, Category("HappyPath")]
  public void Given_QuakePack_When_Detected_Then_StillPak() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_cpak_{Guid.NewGuid():N}.pak");
    var quake = new byte[12];
    "PACK"u8.CopyTo(quake);
    BitConverter.GetBytes(12).CopyTo(quake, 4);
    File.WriteAllBytes(path, quake);
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("Pak"));
    } finally { File.Delete(path); }
  }

  [Test, Category("RoundTrip")]
  public void Given_Resources_When_CreatedAndEdited_Then_GritOrderAndAliasesHold() {
    var d = new ChromiumPakFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("20.html", "<p>x</p>"u8.ToArray()), ArchiveInputInfo.InMemory("10", Png),
      ArchiveInputInfo.InMemory("30", Png)], new FormatCreateOptions());
    d.Add(ms, [ArchiveInputInfo.InMemory("5", "five"u8.ToArray())]);
    d.Remove(ms, ["20"]);
    var (version, encoding, resources) = FileFormat.ChromiumPak.ChromiumPak.Read(ms.ToArray());
    Assert.That((version, encoding), Is.EqualTo((5u, ChromiumPakEncoding.Utf8)));
    Assert.That(resources.Select(r => (r.Id, r.AliasOf)), Is.EqualTo(new (ushort, ushort?)[] { (5, null), (10, null), (30, 10) }));
    Assert.That(resources[2].Data, Is.EqualTo(Png));
  }

  [Test, Category("RoundTrip")]
  public void Given_Version4Pack_When_Edited_Then_StaysVersion4() {
    var d = new ChromiumPakFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(Vector("grit-v4-utf8.pak"));
    d.Remove(ms, ["8"]);
    var (version, _, resources) = FileFormat.ChromiumPak.ChromiumPak.Read(ms.ToArray());
    Assert.That(version, Is.EqualTo(4u));
    Assert.That(resources.Select(r => r.Id), Is.EqualTo(new ushort[] { 7, 300 }));
  }

  [Test, Category("EdgeCase")]
  public void Given_DuplicateIds_When_Created_Then_Refused() {
    Assert.Throws<ArgumentException>(() => new ChromiumPakFormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory("7", [1]), ArchiveInputInfo.InMemory("7.png", [2])], new FormatCreateOptions()));
  }

  [TestCase("readme.txt")]
  [TestCase("65536")]
  [TestCase("-1")]
  [Category("Boundary")]
  public void Given_NameThatIsNoResourceId_When_Created_Then_Refused(string name) {
    Assert.Throws<ArgumentException>(() => new ChromiumPakFormatDescriptor().Create(new MemoryStream(),
      [ArchiveInputInfo.InMemory(name, [1])], new FormatCreateOptions()));
  }

  [Test, Category("Boundary")]
  public void Given_HighestId_When_RoundTripped_Then_Kept() {
    using var ms = new MemoryStream();
    new ChromiumPakFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("65535", [9]), ArchiveInputInfo.InMemory("0", [8])], new FormatCreateOptions());
    var (_, _, resources) = FileFormat.ChromiumPak.ChromiumPak.Read(ms.ToArray());
    Assert.That(resources.Select(r => r.Id), Is.EqualTo(new ushort[] { 0, 65535 }));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownVersion_When_Read_Then_InvalidData() {
    var bytes = Vector("grit-v5-utf8.pak");
    bytes[0] = 6;
    Assert.Throws<InvalidDataException>(() => FileFormat.ChromiumPak.ChromiumPak.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_ResourceCountPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("grit-v5-utf8.pak");
    bytes[9] = 0x7F; // resource count 0x7F05
    Assert.Throws<InvalidDataException>(() => FileFormat.ChromiumPak.ChromiumPak.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_OffsetPastTheEnd_When_Read_Then_InvalidData() {
    var bytes = Vector("grit-v5-utf8.pak");
    bytes[12 + 5 * 6 + 5] = 0x10; // high byte of the terminating entry's offset
    Assert.Throws<InvalidDataException>(() => FileFormat.ChromiumPak.ChromiumPak.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_AliasToMissingEntry_When_Read_Then_InvalidData() {
    var bytes = Vector("grit-v5-utf8.pak");
    bytes[12 + 6 * 6 + 2] = 0x09; // alias 103 -> entry index 9 of 5
    Assert.Throws<InvalidDataException>(() => FileFormat.ChromiumPak.ChromiumPak.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedHeader_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => FileFormat.ChromiumPak.ChromiumPak.Read(Vector("grit-v5-utf8.pak")[..7]));

  // ── installed Chrome and grit as oracles ───────────────────────────────

  private static IEnumerable<string> InstalledChromePacks() {
    var roots = new List<string>();
    if (Environment.GetEnvironmentVariable("CWB_CHROME_DIR") is { Length: > 0 } env) roots.Add(env);
    if (OperatingSystem.IsWindows()) {
      roots.Add(@"D:\PortableApps\GoogleChromePortable64\App\Chrome-bin");
      roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application"));
    } else {
      roots.Add("/opt/google/chrome");
      roots.Add("/usr/lib/chromium");
    }
    foreach (var root in roots.Where(Directory.Exists)) {
      var pack = Directory.EnumerateFiles(root, "chrome_100_percent.pak", SearchOption.AllDirectories).FirstOrDefault();
      if (pack == null) continue;
      yield return pack;
      var locale = Directory.EnumerateFiles(Path.GetDirectoryName(pack)!, "en-US.pak", SearchOption.AllDirectories).FirstOrDefault();
      if (locale != null) yield return locale;
      yield break;
    }
  }

  [Test, Category("EndToEnd")]
  public void Given_InstalledChromePack_When_Rewritten_Then_ByteIdentical() {
    var packs = InstalledChromePacks().ToList();
    if (packs.Count == 0) Assert.Ignore("No installed Chrome found (set CWB_CHROME_DIR).");
    foreach (var path in packs) {
      var original = File.ReadAllBytes(path);
      var (version, encoding, resources) = FileFormat.ChromiumPak.ChromiumPak.Read(original);
      using var ms = new MemoryStream();
      FileFormat.ChromiumPak.ChromiumPak.Write(ms, resources.ToDictionary(r => r.Id, r => r.Data), encoding, version);
      Assert.That(ms.ToArray().AsSpan().SequenceEqual(original), Is.True, path);
    }
  }

  private static string? Python() {
    foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : ["python3", "python"])
      foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0))
        if (File.Exists(Path.Combine(dir.Trim(), name))) return Path.Combine(dir.Trim(), name);
    return null;
  }

  [Test, Category("EndToEnd")]
  public void Given_OurPack_When_ReadByGrit_Then_GritSeesTheSameResources() {
    var python = Python();
    if (python == null) Assert.Ignore("python not on PATH.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_cpak_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "ours.pak");
      using (var fs = File.Create(path))
        new ChromiumPakFormatDescriptor().Create(fs, [ArchiveInputInfo.InMemory("1", Png), ArchiveInputInfo.InMemory("2", "two"u8.ToArray()),
          ArchiveInputInfo.InMemory("3", Png), ArchiveInputInfo.InMemory("40000", [])], new FormatCreateOptions());
      var oracle = Path.Combine(dir, "grit_oracle.py");
      File.WriteAllBytes(oracle, Vector("grit_oracle.py"));
      var psi = new ProcessStartInfo(python, $"-I \"{oracle}\" list \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      using var p = Process.Start(psi)!;
      var listing = p.StandardOutput.ReadToEnd();
      var errors = p.StandardError.ReadToEnd();
      p.WaitForExit();
      Assert.That(p.ExitCode, Is.Zero, errors);
      AssertMatchesGrit(File.ReadAllBytes(path), listing, "ours.pak");
      Assert.That(listing, Does.Contain("ours.pak 3 520"));
    } finally { Directory.Delete(dir, true); }
  }
}
