#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Fat;
using FileSystem.Umsdos;

namespace Compression.Tests.Umsdos;

[TestFixture]
public sealed class UmsdosTests {

  private static byte[] Record(string name, ushort mode, byte flags = 0, uint mtime = 1_000_000_000) {
    var size = (36 + name.Length + 63) / 64 * 64;
    var r = new byte[size];
    r[0] = (byte)name.Length;
    r[1] = flags;
    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 500);
    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 100);
    BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), mtime);
    BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(22), mode);
    Encoding.Latin1.GetBytes(name).CopyTo(r, 36);
    return r;
  }

  // Records 0..5: a valid DOS name, a long name (mangled to record 1), a symlink, a hidden
  // record, a device with no DOS file, and a directory without an EMD of its own.
  private static byte[] Volume() {
    var emd = new MemoryStream();
    emd.Write(Record("README", 0x81A4));
    emd.Write(Record("a-long-file-name.txt", 0x81A4));
    emd.Write(Record("link", 0xA1FF));
    emd.Write(Record(".hidden", 0x81A4, flags: 1));
    emd.Write(Record("ttyS0", 0x21B6));
    emd.Write(Record("sub", 0x41ED));
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LINUX/" + UmsdosReader.EmdName, emd.ToArray()),
      ArchiveInputInfo.InMemory("LINUX/README", "read me\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("LINUX/A-LONG-F.{_1", "long\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("LINUX/LINK", "README"u8.ToArray()),
      ArchiveInputInfo.InMemory("LINUX/_HIDDEN.{_3", "secret"u8.ToArray()),
      ArchiveInputInfo.InMemory("LINUX/SUB/PLAIN.TXT", "plain"u8.ToArray()),
    };
    using var ms = new MemoryStream();
    new FatFormatDescriptor().Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test]
  public void GivenAnEmd_WhenRead_ThenLinuxNamesModesAndLinksReplaceTheDosNames() {
    using var r = new UmsdosReader(new MemoryStream(Volume()));
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(byName.Keys, Is.SupersetOf(new[] { "LINUX", "LINUX/README", "LINUX/a-long-file-name.txt", "LINUX/link", "LINUX/sub" }));
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["LINUX/a-long-file-name.txt"])), Is.EqualTo("long\n"));
    Assert.That(byName["LINUX/link"].IsSymlink, Is.True);
    Assert.That(byName["LINUX/link"].LinkTarget, Is.EqualTo("README"));
    Assert.That(byName["LINUX/README"].Uid, Is.EqualTo(500));
    Assert.That(byName["LINUX/README"].Mode, Is.EqualTo(0x81A4));
    Assert.That(byName["LINUX/README"].LastModified, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_000_000_000).UtcDateTime));
    Assert.That(byName.Keys.Any(k => k.Contains("hidden", StringComparison.OrdinalIgnoreCase)), Is.False, "hidden records are not listed");
    Assert.That(byName.ContainsKey("LINUX/ttyS0"), Is.False, "devices carry no data and are not listed");
    Assert.That(byName.Keys.Any(k => k.EndsWith(UmsdosReader.EmdName, StringComparison.OrdinalIgnoreCase)), Is.False);
    Assert.That(byName.Keys, Does.Contain("LINUX/sub/PLAIN.TXT"), "a directory without an EMD keeps its DOS names");
  }

  [TestCase(0, "{__")]
  [TestCase(4, "{_4")]
  [TestCase(31, "{_v")]
  [TestCase(32, "{1_")]
  [TestCase(1023, "{vv")]
  [TestCase(1024, "}__")]
  [TestCase(9215, "@vv")]
  public void GivenARecordNumber_WhenMangled_ThenTheExtensionMatchesUmsdos(int index, string expected)
    => Assert.That(UmsdosReader.MangledExtension(index), Is.EqualTo(expected));

  [Test]
  public void GivenAPlainFatVolume_WhenReadAsUmsdos_ThenItIsRejected() {
    using var ms = new MemoryStream();
    new FatFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("A.TXT", "a"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.Throws<InvalidDataException>(() => _ = new UmsdosReader(ms));
  }

  [Test]
  public void GivenAnEmdCutMidRecord_WhenParsed_ThenTheBrokenRecordIsDropped() {
    var rec = Record("a-very-long-name-that-needs-two-records.txt", 0x81A4);
    Assert.That(rec.Length, Is.EqualTo(128));
    Assert.That(UmsdosReader.ParseEmd(rec.AsSpan(0, 70)), Is.Empty);
    Assert.That(UmsdosReader.ParseEmd(rec), Has.Count.EqualTo(1));
  }

  /// <summary>
  /// Slackware 8.1's ZipSlack (slackware-8.1/zipslack/zipslack.zip, a UMSDOS tree with
  /// 767 --linux-.--- files) copied onto a FAT32 image by mtools without VFAT names. Its
  /// own package logs (/var/log/packages) name every file the packages installed; each
  /// must be in the Linux view unless ZipSlack pruned it (device nodes under dev/rd, ida,
  /// cciss and ataraid, most of usr/doc and usr/info) and files the install scripts in
  /// /var/log/scripts move, .new and -incoming files among them. Not checked in (100 MB of GPL software): set
  /// <c>CWB_UMSDOS_IMAGE</c> to run it.
  /// </summary>
  [Test, Category("ThirdParty")]
  public void GivenZipSlack81_WhenRead_ThenEveryFileItsPackageLogsNameIsThere() {
    var path = Environment.GetEnvironmentVariable("CWB_UMSDOS_IMAGE");
    if (path is null || !File.Exists(path)) Assert.Ignore("set CWB_UMSDOS_IMAGE to the ZipSlack 8.1 FAT image");
    using var fs = File.OpenRead(path);
    using var r = new UmsdosReader(fs);
    Assert.That(r.EmdDirectories, Is.GreaterThan(700));
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    // The DOS root has no EMD, so the ZipSlack directory keeps its DOS spelling.
    var root = r.Entries.Single(e => e.IsDirectory && e.Name.Equals("LINUX", StringComparison.OrdinalIgnoreCase)).Name + "/";
    Assert.That(byName[root + "boot/boot-menu.b"].Size, Is.EqualTo(7964));
    Assert.That(byName[root + "boot/boot.b"].LinkTarget, Is.EqualTo("boot-menu.b"));

    string[] pruned = ["dev/rd/", "dev/ida/", "dev/cciss/", "dev/ataraid/", "usr/doc/", "usr/info/"];
    // Files the packages' install scripts (/var/log/scripts) move away after unpacking.
    var moved = new HashSet<string>(StringComparer.Ordinal);
    foreach (var script in r.Entries.Where(e => e.Name.StartsWith(root + "var/log/scripts/", StringComparison.Ordinal) && !e.IsDirectory))
      foreach (var line in Encoding.Latin1.GetString(r.Extract(script)).Split('\n')) {
        var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 3 && words[0] == "mv") moved.Add(words[1]);
      }
    var listed = 0;
    var missing = new List<string>();
    foreach (var log in r.Entries.Where(e => e.Name.StartsWith(root + "var/log/packages/", StringComparison.Ordinal) && !e.IsDirectory)) {
      var lines = Encoding.Latin1.GetString(r.Extract(log)).Split('\n');
      var start = Array.IndexOf(lines, "FILE LIST:");
      if (start < 0) continue;
      foreach (var raw in lines.Skip(start + 1)) {
        var line = raw.Trim();
        if (line.Length == 0 || line == "./" || line.StartsWith("install/", StringComparison.Ordinal)) continue;
        ++listed;
        var p = root + line.TrimEnd('/');
        if (byName.ContainsKey(p)) continue;
        if (pruned.Any(x => line.StartsWith(x, StringComparison.Ordinal)) || moved.Contains(line)
            || line.EndsWith(".new", StringComparison.Ordinal) || line.Contains("incoming", StringComparison.Ordinal)) continue;
        if (line.StartsWith("dev/", StringComparison.Ordinal)) continue; // device nodes are not listed
        missing.Add(line);
      }
    }
    Assert.That(listed, Is.GreaterThan(15_000));
    Assert.That(missing, Is.Empty, string.Join(", ", missing.Take(30)));
  }
}
