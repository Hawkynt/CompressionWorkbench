using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using FileFormat.WiseInstaller;

namespace Compression.Tests.WiseInstaller;

/// <summary>
/// Wise installers. Real setups dropped into <c>test-corpus/wise/</c> (not committed: third-party
/// software; the Wise Installation System 5.0, 6.0, 7.01, 8.1, 8.12, 9.01 and 9.02 setups on
/// archive.org) must yield every file WiseUnpacker 3.0.0 extracts from them, byte for byte, as
/// <c>wiseunpacker-manifest.tsv</c> records (made by <c>mkmanifest.py</c>); without the corpus those
/// cases are ignored. Small installers built here pin the layout rules of <c>docs/WISE-ON-DISK.md</c>.
/// </summary>
[TestFixture]
public sealed class WiseInstallerTests {
  private sealed record Row(string Installer, string Entry, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "WiseInstaller", "wiseunpacker-manifest.tsv"))
    .Split('\n')
    .Select(l => l.TrimEnd('\r'))
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t'))
    .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3]))
    .ToArray());

  private static string CorpusDir => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "wise"));

  private static IEnumerable<TestCaseData> Installers() {
    if (!Directory.Exists(CorpusDir))
      yield break;
    foreach (var path in Directory.GetFiles(CorpusDir).Order(StringComparer.Ordinal))
      yield return new TestCaseData(path).SetName("WiseUnpacker agrees on " + Path.GetFileName(path));
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Installers))]
  [Category("ThirdPartyProof")]
  public void Given_ARealInstaller_When_Extracted_Then_EveryFileWiseUnpackerExtractsIsReproduced(string path) {
    var bytes = File.ReadAllBytes(path);
    var rows = Manifest.Value.Where(r => r.Installer == Hash(bytes)).ToArray();
    Assert.That(rows, Is.Not.Empty, "the installer is not in the manifest; run mkmanifest.py");
    Assert.That(Compression.Lib.FormatDetector.Detect(path), Is.EqualTo(Compression.Lib.FormatDetector.Format.WiseInstaller), "detected");

    var reader = WiseReader.Open(bytes);
    // Every listed stream extracts: each is checked against the CRC-32 it carries (and the size and
    // CRC-32 the script records), so for the setups WiseUnpacker cannot finish this is the proof.
    var got = reader.Entries.ToDictionary(e => e.Path, reader.Extract, StringComparer.OrdinalIgnoreCase);
    Assert.Multiple(() => {
      foreach (var row in rows.Where(r => r.Entry != "!")) {
        // Files WiseUnpacker assembles from Edit-INI operations are not stored in the installer.
        if (row.Entry.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !got.ContainsKey(row.Entry))
          continue;
        // WiseUnpacker 3.0.0 keeps a '/' of a dialog name as a directory separator; it is replaced here.
        var entry = row.Entry.StartsWith("CustomDialogSet_", StringComparison.Ordinal) ? row.Entry.Replace('/', '_') : row.Entry;
        Assert.That(got.TryGetValue(entry, out var data), Is.True, $"{row.Entry} missing");
        if (data != null) {
          Assert.That(data.LongLength, Is.EqualTo(row.Size), row.Entry);
          Assert.That(Hash(data), Is.EqualTo(row.Sha256), row.Entry);
        }
      }
    });
    using var stream = new MemoryStream(bytes);
    Assert.That(new WiseInstallerFormatDescriptor().List(stream, null).Select(e => e.Name), Is.EquivalentTo(got.Keys));
  }

  // ---- installers built here --------------------------------------------------------------

  private sealed record Install(string Destination, byte[] Content, bool Corrupt = false);

  /// <summary>
  /// A 6.0-style installer (header without the bitmap ending): a 512-byte stub, the header, the
  /// script, the runtime DLL in slot 1, the final support file, then one stream per install record.
  /// </summary>
  private static byte[] BuildInstaller(IReadOnlyList<Install> installs, uint flags = 0, bool truncateLast = false) {
    static byte[] Stream(byte[] content) {
      var deflated = DeflateCompressor.Compress(content);
      var crc = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(content));
      return [.. deflated, .. crc];
    }

    var data = new List<byte>();
    var records = new List<byte>();
    var ansi = Encoding.Latin1;
    foreach (var install in installs) {
      var start = (uint)data.Count;
      var stream = Stream(install.Content);
      if (install.Corrupt)
        stream[^1] ^= 0xFF;
      data.AddRange(stream);
      var record = new byte[43];
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(3), start);
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(7), (uint)data.Count);
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(11), (ushort)((2001 - 1980) << 9 | 6 << 5 | 15)); // 2001-06-15
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(13), (ushort)(12 << 11 | 30 << 5)); // 12:30:00
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(15), (uint)install.Content.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(39), Crc32.Compute(install.Content));
      records.AddRange(record);
      records.AddRange(ansi.GetBytes(install.Destination + "\0" + "a file\0" + "\0"));
    }

    byte[] scriptBytes = [.. "settings and messages of the script\0"u8, .. records, 0x03, 0x00];
    var script = Stream(scriptBytes);
    var scriptSize = scriptBytes.Length;
    var dll = Stream(Encoding.ASCII.GetBytes(new string('D', 3000)));
    var final = Stream(Encoding.ASCII.GetBytes("final support file"));

    var header = new byte[1 + 4 + 12 + 8 + 8 + 9 * 4 + 12];
    var p = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p), flags);
    p += 4 + 12 + 8;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p), (uint)scriptSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 4), (uint)script.Length);
    p += 8;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p), (uint)dll.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 4), 0x80020002); // a slot value that frames no stream
    p += 9 * 4;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p), (uint)final.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 4), 18);

    var stub = new byte[512];
    stub[0] = (byte)'M';
    stub[1] = (byte)'Z';
    byte[] all = [.. stub, .. header, .. script, .. dll, .. final, .. data];
    return truncateLast ? all[..^3] : all;
  }

  private static readonly byte[] Readme = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Read me first. ", 200)));
  private static readonly byte[] Tool = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251))];

  [Test]
  public void Given_AnInstaller_When_Listed_Then_HeaderStreamsAndInstalledFilesAreNamedAsTheDocumentSays() {
    var reader = WiseReader.Open(BuildInstaller([new(@"%MAINDIR%\Docs\readme.txt", Readme), new(@"%SYS32%\%TOOL%", Tool)]));
    Assert.That(reader.Entries.Select(e => e.Path), Is.EqualTo(new[] {
      "WiseScript.bin", "WISE0001.DLL", "FILE00XX.DAT", "MAINDIR/Docs/readme.txt", "SYS32/TOOL",
    }));
    var readme = reader.Entries.Single(e => e.Path == "MAINDIR/Docs/readme.txt");
    Assert.That(reader.Extract(readme), Is.EqualTo(Readme));
    Assert.That(readme.Modified, Is.EqualTo(new DateTime(2001, 6, 15, 12, 30, 0)));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Path == "SYS32/TOOL")), Is.EqualTo(Tool));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Path == "FILE00XX.DAT")), Is.EqualTo("final support file"u8.ToArray()));
  }

  [Test]
  public void Given_TwoRecordsForOneNameWithDifferentStreams_When_Listed_Then_TheLaterKeepsTheName() {
    var reader = WiseReader.Open(BuildInstaller([new(@"%MAINDIR%\app.exe", Readme), new(@"%MAINDIR%\app.exe", Tool)]));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Path == "MAINDIR/app.exe")), Is.EqualTo(Tool));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Path == "MAINDIR/app.exe~1")), Is.EqualTo(Readme));
  }

  [Test]
  public void Given_AStreamWhoseCrcDoesNotMatch_When_Listed_Then_ItIsNotHandedOut() {
    var reader = WiseReader.Open(BuildInstaller([new(@"%MAINDIR%\good.txt", Readme), new(@"%MAINDIR%\bad.bin", Tool, Corrupt: true)]));
    Assert.That(reader.Entries.Select(e => e.Path), Does.Contain("MAINDIR/good.txt").And.Not.Contain("MAINDIR/bad.bin"));
    Assert.That(reader.Entries.Where(e => e.Path.StartsWith("DATA/", StringComparison.Ordinal)), Is.Empty);
  }

  [Test]
  public void Given_AStreamThatLiesBeyondTheFile_When_Listed_Then_ItIsCountedNotListed() {
    var bytes = BuildInstaller([new(@"%MAINDIR%\first.txt", Readme), new(@"%MAINDIR%\second.bin", Tool)], truncateLast: true);
    var reader = WiseReader.Open(bytes);
    Assert.That(reader.UnavailableRecords, Is.EqualTo(1));
    var names = new WiseInstallerFormatDescriptor().List(new MemoryStream(bytes), null).Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("MAINDIR/first.txt").And.Contain("metadata.ini").And.Not.Contain("MAINDIR/second.bin"));
  }

  [Test]
  public void Given_PkzipWrappedStreams_When_Opened_Then_TheyAreRefused() {
    var bytes = BuildInstaller([new(@"%MAINDIR%\a.txt", Readme)], flags: 0x100);
    var header = 512 + 1 + 4 + 12 + 8 + 8 + 9 * 4 + 12;
    bytes[header] = (byte)'P';
    bytes[header + 1] = (byte)'K';
    Assert.Throws<NotSupportedException>(() => WiseReader.Open(bytes));
    var names = new WiseInstallerFormatDescriptor().List(new MemoryStream(bytes), null).Select(e => e.Name);
    Assert.That(names, Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }

  [Test]
  public void Given_AnExecutableWithoutTheOverlay_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var exe = new byte[64 * 1024];
    new Random(7).NextBytes(exe);
    exe[0] = (byte)'M';
    exe[1] = (byte)'Z';
    Assert.That(WiseReader.IsWise(new MemoryStream(exe)), Is.False);
    var listed = new WiseInstallerFormatDescriptor().List(new MemoryStream(exe), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }

  [Test]
  public void Given_AnInstaller_When_Detected_Then_IsWiseSeesIt() =>
    Assert.That(WiseReader.IsWise(new MemoryStream(BuildInstaller([new(@"%MAINDIR%\a.txt", Readme)]))), Is.True);
}
