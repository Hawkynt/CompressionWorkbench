using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Compression.Core.Checksums;
using FileFormat.DiskDoubler;

namespace Compression.Tests.DiskDoubler;

/// <summary>
/// DDAR and DDA2 archives: built ones read by our reader and by The Unarchiver, and the real
/// archives of the dexvert sample set compared with lsar's listing.
/// </summary>
[TestFixture]
public sealed class DiskDoublerArchiveTests {
  private static readonly byte[] Text = DiskDoublerMethodTests.Payload(4000, 21);
  private static readonly byte[] Raw = DiskDoublerMethodTests.Payload(700, 22);
  private static readonly byte[] Inner = DiskDoublerMethodTests.Payload(2500, 23);

  private static byte[] Header(string magic, int size) {
    var header = new byte[size];
    Encoding.ASCII.GetBytes(magic).CopyTo(header, 0);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(size - 2), Crc16Ccitt.Compute(header.AsSpan(0, size - 2)));
    return header;
  }

  private static byte[] DdarEntry(string name, bool isDir = false, bool endDir = false, uint dataSize = 0, uint rsrcSize = 0, ushort finderFlags = 0) {
    var e = new byte[124];
    "DDAR"u8.CopyTo(e);
    e[8] = (byte)name.Length;
    Encoding.ASCII.GetBytes(name).CopyTo(e, 9);
    e[72] = (byte)(isDir ? 1 : 0);
    e[73] = (byte)(endDir ? 1 : 0);
    BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(74), dataSize);
    BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(78), rsrcSize);
    "TEXTttxt"u8.CopyTo(e.AsSpan(90));
    BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(98), finderFlags);
    return e;
  }

  /// <summary>DDAR: a stored file, a folder holding a method-2 file, and a method-7 file after it.</summary>
  internal static byte[] Ddar() => [
    .. Header("DDAR", 78),
    .. DdarEntry("raw.txt", dataSize: (uint)Raw.Length, finderFlags: 0x20), .. Raw,
    .. DdarEntry("Folder", isDir: true),
    .. DdarEntry("text.txt"), .. DiskDoublerMethodTests.Splay(Text, 256, countByte: false),
    .. DdarEntry("Folder", endDir: true),
    .. DdarEntry("lzs.txt"), .. DiskDoublerMethodTests.Lzs(Inner),
  ];

  private static byte[] Dda2Entry(string name, int level, bool isDir, byte[] body) {
    var e = new byte[46 + (isDir ? 16 : 10)];
    "DDA2"u8.CopyTo(e);
    BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(4), (ushort)(isDir ? 0x8000 : 0));
    e[6] = (byte)name.Length;
    Encoding.ASCII.GetBytes(name).CopyTo(e, 7);
    BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(38), (uint)(level + 2));
    BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(42), (uint)(e.Length + body.Length));
    return [.. e, .. body];
  }

  /// <summary>DDA2: the top folder (level 0, skipped), a subfolder with a method-5 file, and a method-0 file at the top.</summary>
  internal static byte[] Dda2() {
    byte[] end = [.. "DDA2"u8, 0xBB, 0xBB];
    return [
      .. Header("DDA2", 62),
      .. Dda2Entry("Top", -1, true, []),
      .. Dda2Entry("Sub", 0, true, []),
      .. Dda2Entry("text.txt", 1, false, DiskDoublerMethodTests.Splay(Text, 16, countByte: true)),
      .. Dda2Entry("lzs.txt", 0, false, DiskDoublerMethodTests.Lzs(Inner)),
      .. end,
    ];
  }

  private static readonly Dictionary<string, byte[]> Expected = new() {
    ["raw.txt"] = Raw, ["Folder/text.txt"] = Text, ["lzs.txt"] = Inner, ["Sub/text.txt"] = Text,
  };

  [Test]
  public void GivenADdarArchive_WhenRead_ThenFoldersAndFilesAreRestored() {
    using var r = new DiskDoublerReader(new MemoryStream(Ddar()));
    Assert.That(r.Entries.Select(e => (e.Name, e.IsDirectory)),
      Is.EqualTo(new[] { ("raw.txt", false), ("Folder", true), ("Folder/text.txt", false), ("lzs.txt", false) }));
    foreach (var e in r.Entries.Where(e => !e.IsDirectory))
      Assert.That(r.Extract(e), Is.EqualTo(Expected[e.Name]), e.Name);
    Assert.That(r.UnreadTailOffset, Is.Null);
  }

  [Test]
  public void GivenADda2Archive_WhenRead_ThenLevelsBecomeFolders() {
    using var r = new DiskDoublerReader(new MemoryStream(Dda2()));
    Assert.That(r.Entries.Select(e => (e.Name, e.IsDirectory)),
      Is.EqualTo(new[] { ("Sub", true), ("Sub/text.txt", false), ("lzs.txt", false) }));
    foreach (var e in r.Entries.Where(e => !e.IsDirectory))
      Assert.That(r.Extract(e), Is.EqualTo(Expected[e.Name]), e.Name);
  }

  [Test]
  public void GivenADdarArchiveWithAnUnknownTail_WhenRead_ThenTheEntriesBeforeItStand() {
    byte[] archive = [.. Ddar(), .. "DDF3DDAP"u8, .. new byte[40]];
    using var r = new DiskDoublerReader(new MemoryStream(archive));
    Assert.That(r.Entries, Has.Count.EqualTo(4));
    Assert.That(r.UnreadTailOffset, Is.EqualTo(Ddar().Length));
  }

  [Test]
  public void GivenADdarArchiveWithABadHeaderCrc_WhenRead_ThenItIsRefused() {
    var archive = Ddar();
    archive[10] ^= 1;
    Assert.Throws<InvalidDataException>(() => _ = new DiskDoublerReader(new MemoryStream(archive)));
  }

  [Test]
  public void GivenADda2EntryShorterThanItsHeader_WhenRead_ThenItIsRefused() {
    var archive = Dda2();
    BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(62 + 42), 10);
    Assert.Throws<InvalidDataException>(() => _ = new DiskDoublerReader(new MemoryStream(archive)));
  }

  [Test]
  public void GivenAnArchive_WhenTheDescriptorExtractsIt_ThenTheTreeIsWritten() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ddar-" + Guid.NewGuid().ToString("N"));
    try {
      new DiskDoublerFormatDescriptor().Extract(new MemoryStream(Ddar()), dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "Folder", "text.txt")), Is.EqualTo(Text));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "raw.txt")), Is.EqualTo(Raw));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  private static void RequireUnar() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("unar") || !FsInteropToolbox.WslHasTool("lsar"))
      Assert.Ignore("unar is not installed: `sudo apt install -y unar`.");
  }

  [TestCase("ddar")]
  [TestCase("dda2")]
  [Category("ExternalInterop")]
  public void GivenABuiltArchive_WhenUnarExtractsIt_ThenEveryFileMatches(string kind) {
    RequireUnar();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ddaro-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "archive.sea");
      File.WriteAllBytes(path, kind == "ddar" ? Ddar() : Dda2());
      var outDir = Path.Combine(dir, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -D -o {FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(path)}");
      TestContext.Out.WriteLine(run.StdOut + run.StdErr);
      Assert.That(run.ExitCode, Is.Zero, "unar refused the archive");
      using var r = new DiskDoublerReader(new MemoryStream(File.ReadAllBytes(path)));
      foreach (var e in r.Entries.Where(e => !e.IsDirectory)) {
        var theirs = Path.Combine(outDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(theirs), Is.True, e.Name);
        Assert.That(SHA256.HashData(File.ReadAllBytes(theirs)), Is.EqualTo(SHA256.HashData(r.Extract(e))), e.Name);
      }
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  /// <summary>
  /// The DDAR and DDA2 archives of the dexvert sample set (test-corpus/diskdoubler/, not committed):
  /// every entry lsar lists — name, folder or fork, size — is ours too, in order, and every fork we
  /// decode passes its checksum.
  /// </summary>
  [Test]
  [Category("ExternalInterop")]
  public void RealArchives_InTestCorpus_ListLikeLsar() {
    var corpus = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "diskdoubler"));
    if (!Directory.Exists(corpus))
      Assert.Ignore($"No real DiskDoubler files under {corpus}.");
    RequireUnar();
    var checkedArchives = 0;
    foreach (var file in Directory.EnumerateFiles(corpus).Order()) {
      var head = new byte[200];
      using (var fs = File.OpenRead(file))
        head = head[..fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
      bool IsArchive(int at) => head.Length >= at + 4 && (head.AsSpan(at, 4).SequenceEqual("DDAR"u8) || head.AsSpan(at, 4).SequenceEqual("DDA2"u8));
      if (!IsArchive(0) && !IsArchive(128))
        continue;

      var lsar = FsInteropToolbox.RunWsl($"lsar -j {FsInteropToolbox.WinToWsl(file)}");
      using var json = JsonDocument.Parse(lsar.StdOut.Replace("\0", "\\u0000"));
      var theirs = json.RootElement.GetProperty("lsarContents").EnumerateArray().Select(e => (
        Name: e.GetProperty("XADFileName").GetString()!,
        IsDir: e.TryGetProperty("XADIsDirectory", out var d) && d.GetInt32() == 1,
        IsRsrc: e.TryGetProperty("XADIsResourceFork", out var rf) && rf.GetInt32() == 1,
        Size: e.TryGetProperty("XADFileSize", out var s) ? s.GetInt64() : 0)).ToArray();

      using var r = new DiskDoublerReader(File.OpenRead(file));
      var ours = r.Entries.Select(e => (
        Name: e.IsDataFork || e.IsDirectory ? e.Name : e.Name[..^".rsrc".Length],
        IsDir: e.IsDirectory, IsRsrc: !e.IsDirectory && !e.IsDataFork, Size: e.IsDirectory ? 0 : e.OriginalSize)).ToArray();
      var top = theirs.Length > 0 && theirs[0].IsDir && ours.Length > 0 && !ours[0].IsDir ? theirs[0].Name + "/" : "";
      // The WSL console hands non-ASCII names over in a code page of its own: compare their ASCII letters.
      static string Ascii(string s) => new(s.Where(c => char.IsAsciiLetterOrDigit(c) || " ./-_".Contains(c)).ToArray());
      Assert.That(ours.Select(o => (Ascii(top + o.Name), o.IsDir, o.IsRsrc, o.Size)).Take(theirs.Length).ToArray(),
        Is.EqualTo(theirs.Select(t => (Ascii(t.Name), t.IsDir, t.IsRsrc, t.Size)).ToArray()), Path.GetFileName(file));
      foreach (var e in r.Entries.Where(e => !e.IsDirectory && e.IsSupported))
        Assert.DoesNotThrow(() => r.Extract(e), e.Name);
      ++checkedArchives;
    }
    if (checkedArchives == 0)
      Assert.Ignore("No DDAR or DDA2 archive in the corpus.");
  }
}
