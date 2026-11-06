using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Lzma;
using FileFormat.Install4j;

namespace Compression.Tests.Install4j;

/// <summary>
/// install4j installers. Real setups dropped into <c>test-corpus/install4j/</c> (not committed:
/// ej-technologies' own exe4j 10.0.1 and install4j 5.1.15, 8.0.11 and 11.0.4 installers, which
/// install4j built) must yield the archives <c>liblzma-manifest.tsv</c> records: unpacked by
/// liblzma and checked member by member against their CRC-32 by Python's zipfile
/// (<c>mkmanifest.py</c>). Without the corpus those cases are ignored.
/// </summary>
[TestFixture]
public sealed class Install4jTests {
  private sealed record Row(string Installer, string Archive, long Size, string Sha256, string Members);

  private static readonly Lazy<Row[]> Manifest = new(() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Install4j", "liblzma-manifest.tsv"))
    .Split('\n')
    .Select(l => l.TrimEnd('\r'))
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t'))
    .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3], f[4]))
    .ToArray());

  private static string CorpusDir => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "install4j"));

  private static IEnumerable<TestCaseData> Installers() {
    if (!Directory.Exists(CorpusDir))
      yield break;
    foreach (var path in Directory.GetFiles(CorpusDir).Order(StringComparer.Ordinal))
      yield return new TestCaseData(path).SetName("liblzma agrees on " + Path.GetFileName(path));
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Installers))]
  [Category("ThirdPartyProof")]
  public void Given_ARealInstaller_When_Extracted_Then_TheArchivesAreThoseLiblzmaUnpacks(string path) {
    var bytes = File.ReadAllBytes(path);
    var rows = Manifest.Value.Where(r => r.Installer == Hash(bytes)).ToArray();
    Assert.That(rows, Is.Not.Empty, "the installer is not in the manifest; run mkmanifest.py");
    Assert.That(Compression.Lib.FormatDetector.Detect(path), Is.EqualTo(Compression.Lib.FormatDetector.Format.Install4j), "detected");

    var reader = Install4jReader.Open(bytes);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EqualTo(rows.Select(r => r.Archive)));
    foreach (var row in rows) {
      var data = reader.Extract(reader.Entries.Single(e => e.Name == row.Archive));
      Assert.That(data.LongLength, Is.EqualTo(row.Size), row.Archive);
      Assert.That(Hash(data), Is.EqualTo(row.Sha256), row.Archive);
    }
  }

  // ---- installers built here ----------------------------------------------------------------

  /// <summary>A minimal PE whose overlay is the install4j layout: launcher block, table, archives.</summary>
  private static byte[] Build(IReadOnlyList<(string Name, byte[] Stored)> archives, byte[]? launcher = null) {
    var pe = new byte[0x400];
    pe[0] = (byte)'M';
    pe[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), 0x80);
    "PE\0\0"u8.CopyTo(pe.AsSpan(0x80));
    BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x86), 1); // one section
    BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x94), 0xE0); // optional header size
    BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x98), 0x10B);
    var section = 0x98 + 0xE0;
    BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 16), 0x200); // raw size
    BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 20), 0x200); // raw pointer

    var overlay = new List<byte>([0xD5, 0x13, 0xE4, 0xE8]);
    overlay.AddRange(launcher ?? [1, 0, 0, 0, 9, 9, 9, 9]);
    overlay.AddRange([0xE8, 0xE4, 0x13, 0xD5]);
    var count = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(count, (uint)archives.Count);
    overlay.AddRange(count);
    foreach (var (name, stored) in archives) {
      var head = new byte[2 + name.Length + 8];
      BinaryPrimitives.WriteUInt16BigEndian(head, (ushort)name.Length);
      System.Text.Encoding.ASCII.GetBytes(name).CopyTo(head, 2);
      BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(2 + name.Length + 4), (uint)stored.Length);
      overlay.AddRange(head);
    }
    foreach (var (_, stored) in archives)
      overlay.AddRange(stored);
    return [.. pe, .. overlay];
  }

  private static byte[] Lzma(byte[] content) {
    using var output = new MemoryStream();
    var encoder = new LzmaEncoder();
    output.Write(encoder.Properties);
    var size = new byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(size, content.Length);
    output.Write(size);
    encoder.Encode(output, content, writeEndMarker: false);
    return output.ToArray();
  }

  private static readonly byte[] Zip = [.. "PK\x03\x04"u8, .. Enumerable.Range(0, 500).Select(i => (byte)(i * 13))];

  [Test]
  public void Given_StoredAndLzmaArchives_When_Opened_Then_BothUnpackAndTheLauncherBlockIsListed() {
    var reader = Install4jReader.Open(Build([("0.dat", Lzma(Zip)), ("1.dat", Zip)]));
    Assert.That(reader.Entries.Select(e => e.Name), Is.EqualTo(new[] { "launcher.dat", "0.dat", "1.dat" }));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(Zip));
    Assert.That(reader.Extract(reader.Entries[2]), Is.EqualTo(Zip));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(new byte[] { 1, 0, 0, 0, 9, 9, 9, 9 }));
  }

  [Test]
  public void Given_TheEndMarkerInsideTheLauncherBlock_When_Opened_Then_TheConsistentTableIsTaken() {
    byte[] launcher = [1, 0, 0, 0, 0xE8, 0xE4, 0x13, 0xD5, 0, 0, 0, 7, 0x41, 0x42];
    var reader = Install4jReader.Open(Build([("0.dat", Zip)], launcher));
    Assert.That(reader.Entries.Select(e => e.Name), Is.EqualTo(new[] { "launcher.dat", "0.dat" }));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(launcher));
  }

  [Test]
  public void Given_AnExecutableWithoutTheMarker_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var exe = Build([("0.dat", Zip)]);
    exe[0x400] = 0; // the overlay starts where the one section ends
    Assert.That(Install4jReader.IsInstall4j(new MemoryStream(exe)), Is.False);
    var listed = new Install4jFormatDescriptor().List(new MemoryStream(exe), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }

  [Test]
  public void Given_ATruncatedArchive_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var exe = Build([("0.dat", Zip)])[..^100];
    var listed = new Install4jFormatDescriptor().List(new MemoryStream(exe), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }
}
