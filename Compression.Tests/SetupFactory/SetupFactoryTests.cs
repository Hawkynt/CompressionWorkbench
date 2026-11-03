using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.PkwareDcl;
using FileFormat.SetupFactory;

namespace Compression.Tests.SetupFactory;

/// <summary>
/// Setup Factory installers. Real setups dropped into <c>test-corpus/setupfactory/</c> (not
/// committed: third-party software; the Setup Factory 5.0, 5.0.1, 6.0.0.2, 7.0, 8.0, 9.0.1 and 9.1
/// setups on archive.org) must yield every file sfextract 0.0.8 extracts from them, byte for byte,
/// as <c>sfextract-manifest.tsv</c> records (made by <c>mkmanifest.py</c>); sfextract does not read
/// versions 5 and 6, whose files are checked against the CRC-32 their script records. Without the
/// corpus those cases are ignored.
/// </summary>
[TestFixture]
public sealed class SetupFactoryTests {
  private sealed record Row(string Installer, string Entry, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "SetupFactory", "sfextract-manifest.tsv"))
    .Split('\n')
    .Select(l => l.TrimEnd('\r'))
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t'))
    .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3]))
    .ToArray());

  private static string CorpusDir => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "setupfactory"));

  private static IEnumerable<TestCaseData> Installers() {
    if (!Directory.Exists(CorpusDir))
      yield break;
    foreach (var path in Directory.GetFiles(CorpusDir).Order(StringComparer.Ordinal))
      yield return new TestCaseData(path).SetName("sfextract agrees on " + Path.GetFileName(path));
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Installers))]
  [Category("ThirdPartyProof")]
  public void Given_ARealInstaller_When_Extracted_Then_EveryFileSfextractExtractsIsReproduced(string path) {
    var bytes = File.ReadAllBytes(path);
    var rows = Manifest.Value.Where(r => r.Installer == Hash(bytes)).ToArray();
    Assert.That(rows, Is.Not.Empty, "the installer is not in the manifest; run mkmanifest.py");
    Assert.That(Compression.Lib.FormatDetector.Detect(path), Is.EqualTo(Compression.Lib.FormatDetector.Format.SetupFactory), "detected");

    var reader = SetupFactoryReader.Open(bytes);
    // Every member decodes to its recorded size and CRC-32; for the versions sfextract does not
    // read, that is the proof.
    var got = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in reader.Entries)
      got[entry.Path] = reader.Extract(entry);
    Assert.That(reader.Entries.Count(e => e.Crc != 0 && e.Path.Contains('/')), Is.GreaterThan(0), "installed files with a recorded CRC-32");

    if (rows[0].Entry == "!")
      return;
    Assert.Multiple(() => {
      foreach (var row in rows) {
        Assert.That(got.TryGetValue(row.Entry, out var data), Is.True, $"{row.Entry} missing");
        if (data != null) {
          Assert.That(data.LongLength, Is.EqualTo(row.Size), row.Entry);
          Assert.That(Hash(data), Is.EqualTo(row.Sha256), row.Entry);
        }
      }
    });
    using var stream = new MemoryStream(bytes);
    Assert.That(new SetupFactoryFormatDescriptor().List(stream, null).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
      Is.EqualTo(rows.Length), "the listing names the files sfextract writes");
  }

  // ---- a version 5 installer built here ---------------------------------------------------

  private static byte[] Str(string s) => [(byte)s.Length, .. Encoding.ASCII.GetBytes(s)];

  /// <summary>
  /// A Setup Factory 5 installer: signature at 0x8000, one own file (the script, PKWARE DCL),
  /// one installed file (PKWARE DCL) whose CRC-32 the script records.
  /// </summary>
  private static byte[] BuildVersion5(byte[] content, bool corruptCrc = false) {
    var packed = PkwareDclEncoder.Compress(content);
    var crc = Crc32.Compute(content) ^ (corruptCrc ? 1u : 0u);
    var record = new List<byte>();
    record.AddRange(Str(@"C:\src\readme.txt"));
    record.AddRange(Str("readme.txt"));
    record.AddRange(Str(@"C:\src"));
    record.AddRange(Str(".txt"));
    record.Add(0);
    record.AddRange(BitConverter.GetBytes((uint)content.Length));
    record.AddRange(new byte[38]);
    record.AddRange(Str(@"%AppDir%\Docs"));
    record.AddRange(new byte[5]);
    record.AddRange([0, 0, 0]); // three empty shortcut strings
    record.Add(0);
    record.Add(0); // icon path
    record.AddRange(new byte[5]);
    record.Add(0); // package
    record.Add(0);
    record.Add(0); // font
    record.AddRange(new byte[3]);
    record.AddRange([1, 0, 0]); // compressed, attribute choice, forced attributes
    record.AddRange(new byte[15]);
    record.AddRange(BitConverter.GetBytes((uint)packed.Length));
    record.AddRange(BitConverter.GetBytes(crc));
    record.AddRange(new byte[39]);
    byte[] script = [1, 0, 0xFF, 0xFF, 3, 0, 9, 0, .. "CFileInfo"u8, .. record];
    var packedScript = PkwareDclEncoder.Compress(script);

    var installer = new List<byte>(new byte[0x8000]) { [0] = (byte)'M', [1] = (byte)'Z' };
    installer.AddRange([0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7]);
    installer.AddRange(BitConverter.GetBytes(1u));
    var name = new byte[16];
    Encoding.ASCII.GetBytes("irsetup.dat").CopyTo(name, 0);
    installer.AddRange(name);
    installer.AddRange(BitConverter.GetBytes((uint)packedScript.Length));
    installer.AddRange(BitConverter.GetBytes(Crc32.Compute(script)));
    installer.AddRange(packedScript);
    installer.AddRange(packed);
    return [.. installer];
  }

  private static readonly byte[] Readme = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Setup Factory test file. ", 120)));

  [Test]
  public void Given_AVersion5Installer_When_Opened_Then_TheScriptAndTheInstalledFileAreListedAndExtract() {
    var bytes = BuildVersion5(Readme);
    Assert.That(SetupFactoryReader.IsSetupFactory(new MemoryStream(bytes)), Is.True);
    var reader = SetupFactoryReader.Open(bytes);
    Assert.That(reader.Version, Is.EqualTo(5));
    Assert.That(reader.Entries.Select(e => e.Path), Is.EqualTo(new[] { "irsetup.dat", "%AppDir%/Docs/readme.txt" }));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(Readme));
  }

  [Test]
  public void Given_AFileWhoseCrcDoesNotMatch_When_Extracted_Then_ItIsRefused() {
    var reader = SetupFactoryReader.Open(BuildVersion5(Readme, corruptCrc: true));
    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries[1]));
  }

  [Test]
  public void Given_AnExecutableWithoutTheSignature_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var exe = new byte[0x20000];
    exe[0] = (byte)'M';
    exe[1] = (byte)'Z';
    Assert.That(SetupFactoryReader.IsSetupFactory(new MemoryStream(exe)), Is.False);
    var listed = new SetupFactoryFormatDescriptor().List(new MemoryStream(exe), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }

  [Test]
  public void Given_ATruncatedInstaller_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var bytes = BuildVersion5(Readme)[..(0x8000 + 8 + 4 + 16 + 8 + 10)];
    var listed = new SetupFactoryFormatDescriptor().List(new MemoryStream(bytes), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }
}
