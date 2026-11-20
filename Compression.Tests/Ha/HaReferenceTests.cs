using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Ha;
using FileFormat.Ha;

namespace Compression.Tests.Ha;

/// <summary>
/// HA archives the reference ha.exe (HA 0.999b) wrote from inputs of this repository, and the
/// oracle's verdict on ours. See ReferenceVectors/README.md for how each was made.
/// </summary>
[TestFixture]
public class HaReferenceTests {
  private const string NotesSha256 = "34f8dc9074f078f10df9d1a7e79eddaa415351481f8e538ebaa427781d085b95";
  private const string PatternSha256 = "2b59df4fd70470c34f8808d9032858041e79d55ea7ff2bf557ceb089540d80a3";

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"HaVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded HA vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  // The listings ha.exe printed (`ha lf`): name, method, original and compressed size, CRC-32.
  [TestCase("hsc.ha", "in/empty.txt|CPY|0|0|00000000;in/notes.txt|HSC|102334|31750|ADF5714B;in/sub|DIR|0|0|00000000;in/sub/pattern.bin|HSC|46714|34528|618A9C33;in/tiny.txt|CPY|1|1|E8B7BE43")]
  [TestCase("asc.ha", "in/empty.txt|CPY|0|0|00000000;in/notes.txt|ASC|102334|37556|ADF5714B;in/sub|DIR|0|0|00000000;in/sub/pattern.bin|ASC|46714|9452|618A9C33;in/tiny.txt|CPY|1|1|E8B7BE43")]
  [TestCase("cpy.ha", "in/tiny.txt|CPY|1|1|E8B7BE43;in/empty.txt|CPY|0|0|00000000")]
  [Category("ThirdPartyVector")]
  public void Given_HaExeArchive_When_Listed_Then_MatchesTheOracleListing(string vector, string expected) {
    using var r = new HaReader(new MemoryStream(Vector(vector)));
    var listing = string.Join(";", r.Entries.Select(e => $"{e.FileName}|{e.MethodName}|{e.OriginalSize}|{e.CompressedSize}|{e.Crc32:X8}"));
    Assert.That(listing, Is.EqualTo(expected));
    Assert.That(r.IsTruncated, Is.False);
    Assert.That(r.DeclaredCount, Is.EqualTo(r.Entries.Count));
  }

  [TestCase("hsc.ha")]
  [TestCase("asc.ha")]
  [Category("ThirdPartyVector")]
  public void Given_HaExeArchive_When_Extracted_Then_ContentMatchesTheInputs(string vector) {
    using var r = new HaReader(new MemoryStream(Vector(vector)));
    Assert.That(Sha256(r.Extract(r.Entries.Single(e => e.FileName == "in/notes.txt"))), Is.EqualTo(NotesSha256));
    Assert.That(Sha256(r.Extract(r.Entries.Single(e => e.FileName == "in/sub/pattern.bin"))), Is.EqualTo(PatternSha256));
    Assert.That(r.Extract(r.Entries.Single(e => e.FileName == "in/tiny.txt")), Is.EqualTo("a"u8.ToArray()));
    Assert.That(r.Extract(r.Entries.Single(e => e.FileName == "in/empty.txt")), Is.Empty);
  }

  // The encoder shares the decoder's model, so it must reproduce ha.exe's HSC streams exactly.
  [TestCase("in/notes.txt")]
  [TestCase("in/sub/pattern.bin")]
  [Category("ThirdPartyVector")]
  public void Given_HaExeHscMember_When_ReEncoded_Then_ByteIdenticalStream(string member) {
    var bytes = Vector("hsc.ha");
    using var r = new HaReader(new MemoryStream(bytes));
    var e = r.Entries.Single(x => x.FileName == member);
    var stored = bytes.AsSpan((int)e.DataOffset, (int)e.CompressedSize).ToArray();
    Assert.That(HaHsc.Encode(r.Extract(e)), Is.EqualTo(stored));
  }

  internal static void WriteOurs(Stream output) {
    var stamp = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"HA line {i % 37} of the probe, value {i * 7 % 101}\r\n")));
    using var w = new HaWriter(output, leaveOpen: true);
    w.AddDirectory("docs", stamp);
    w.AddFile("docs/text.txt", text, stamp);
    w.AddFile("raw.bin", [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))], stamp);
    w.AddFile("empty.txt", [], stamp);
    w.AddFile("tiny.txt", "a"u8, stamp);
  }

  // ours.ha is this writer's output; ha.exe listed, tested and extracted it (ha-listing-ours.txt).
  [Test, Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_BytesEqualTheFixtureHaExeAccepted() {
    using var ms = new MemoryStream();
    WriteOurs(ms);
    Assert.That(ms.ToArray(), Is.EqualTo(Vector("ours.ha")));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_OurFixture_When_Read_Then_ContentMatchesWhatHaExeExtracted() {
    using var r = new HaReader(new MemoryStream(Vector("ours.ha")));
    Assert.That(r.Entries.Select(e => $"{e.FileName}|{e.MethodName}"), Is.EqualTo(new[] {
      "docs|DIR", "docs/text.txt|HSC", "raw.bin|HSC", "empty.txt|CPY", "tiny.txt|CPY",
    }));
    Assert.That(Sha256(r.Extract(r.Entries[1])), Is.EqualTo("47fedfe3862208cc5c572eb8e5e75f7a4f639d4c596752e8cc5a44cdcde9a10c"));
    Assert.That(Sha256(r.Extract(r.Entries[2])), Is.EqualTo("7291514d2492fd7ff49e10ba7df95d19d31d199b89d74bcb62cebdee1bc1a498"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_HaExeAscArchive_When_DirectoryRecordRead_Then_DosAttributes() {
    using var r = new HaReader(new MemoryStream(Vector("asc.ha")));
    var dir = r.Entries.Single(e => e.IsDirectory);
    Assert.That(dir.MachineInfo, Is.EqualTo(new byte[] { 1, 0x10 }));
    Assert.That(r.Entries.Where(e => !e.IsDirectory).Select(e => e.MachineInfo[1]), Is.All.EqualTo(0x20));
  }

  // ── Unix special files (special.ha, written by the Unix ha with the `s` switch) ─────────────

  // `ha lf special.ha`: name, method, original and compressed size, CRC-32, and the mode it printed
  // as crw-r--r--, -rw-r--r--, lrwxrwxrwx, brw-rw----, frw-r-----, srwxr-xr-x, drwxr-xr-x, crw-------, lrwxrwxrwx.
  [Test, Category("ThirdPartyVector")]
  public void Given_UnixHaArchive_When_Listed_Then_MatchesTheOracleListing() {
    using var r = new HaReader(new MemoryStream(Vector("special.ha")));
    var listing = r.Entries.Select(e => $"{e.FileName}|{e.MethodName}|{e.OriginalSize}|{e.CompressedSize}|{e.Crc32:X8}|{e.UnixMode:X4}");
    Assert.That(listing, Is.EqualTo(new[] {
      "bigdev|SPC|8|8|00000000|21A4",
      "file.txt|CPY|14|14|C6D278BA|01A4", // a regular file carries no type bits
      "link|SPC|9|9|00000000|A1FF",
      "loopx|SPC|8|8|00000000|61B0",
      "pipe|SPC|0|0|00000000|11A0",
      "sock|SPC|0|0|00000000|C1ED",
      "sub|DIR|0|0|00000000|41ED",
      "tty0|SPC|8|8|00000000|2180",
      "sub/uplink|SPC|12|12|00000000|A1FF",
    }));
    Assert.That(r.IsTruncated, Is.False);
    Assert.That(r.DeclaredCount, Is.EqualTo(9));
  }

  // The Unix ha re-created bigdev (c 300,70000), loopx (b 7,200), tty0 (c 4,1) and pipe from this
  // archive; the links are the targets the sources pointed at.
  [TestCase("bigdev", HaSpecialKind.CharacterDevice, 300u, 70000u)]
  [TestCase("loopx", HaSpecialKind.BlockDevice, 7u, 200u)]
  [TestCase("tty0", HaSpecialKind.CharacterDevice, 4u, 1u)]
  [Category("ThirdPartyVector")]
  public void Given_UnixHaDevice_When_Read_Then_MajorAndMinor(string name, HaSpecialKind kind, uint major, uint minor) {
    using var r = new HaReader(new MemoryStream(Vector("special.ha")));
    var e = r.Entries.Single(x => x.FileName == name);
    var special = r.ReadSpecial(e);
    Assert.That(special.Kind, Is.EqualTo(kind));
    Assert.That((special.DeviceMajor, special.DeviceMinor), Is.EqualTo(((uint?)major, (uint?)minor)));
    Assert.That((e.UnixUserId, e.UnixGroupId), Is.EqualTo(((ushort?)0, (ushort?)0)));
    Assert.That(special.LinkTarget, Is.Null);
  }

  [TestCase("link", "file.txt")]
  [TestCase("sub/uplink", "../file.txt")]
  [Category("ThirdPartyVector")]
  public void Given_UnixHaLink_When_Read_Then_LinkText(string name, string target) {
    using var r = new HaReader(new MemoryStream(Vector("special.ha")));
    var e = r.Entries.Single(x => x.FileName == name);
    var special = r.ReadSpecial(e);
    Assert.That(special.Kind, Is.EqualTo(HaSpecialKind.SymbolicLink));
    Assert.That(special.LinkTarget, Is.EqualTo(target));
    Assert.That((e.UnixUserId, e.UnixGroupId), Is.EqualTo(((ushort?)1000, (ushort?)1000)));
    Assert.Throws<NotSupportedException>(() => r.Extract(e));
  }

  [TestCase("pipe", HaSpecialKind.Fifo)]
  [TestCase("sock", HaSpecialKind.Socket)]
  [Category("ThirdPartyVector")]
  public void Given_UnixHaFifoOrSocket_When_Read_Then_KindWithoutData(string name, HaSpecialKind kind) {
    using var r = new HaReader(new MemoryStream(Vector("special.ha")));
    var special = r.ReadSpecial(r.Entries.Single(x => x.FileName == name));
    Assert.That(special.Kind, Is.EqualTo(kind));
    Assert.That(special.Data, Is.Empty);
    Assert.That(special.DeviceNumber, Is.Null);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_UnixHaArchive_When_ListedByDescriptor_Then_LinksResolveAndKindsShown() {
    var listing = new HaFormatDescriptor().List(new MemoryStream(Vector("special.ha")), null).ToDictionary(e => e.Name);
    Assert.That(listing["link"], Has.Property("IsSymlink").True.And.Property("LinkTarget").EqualTo("file.txt").And.Property("TargetSize").EqualTo(14));
    Assert.That(listing["link"].OriginalSize, Is.EqualTo(8));
    Assert.That(listing["sub/uplink"], Has.Property("IsSymlink").True.And.Property("LinkTarget").EqualTo("../file.txt"));
    Assert.That(new[] { "bigdev", "loopx", "pipe", "sock", "tty0" }.Select(n => listing[n].Kind),
      Is.EqualTo(new[] { "char-device", "block-device", "fifo", "socket", "char-device" }));
    Assert.That(listing["file.txt"], Has.Property("Kind").Null.And.Property("IsSymlink").False);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_UnixHaArchive_When_ExtractedByDescriptor_Then_FilesAndDirectoriesOnly() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ha-special-" + Guid.NewGuid().ToString("N"));
    try {
      new HaFormatDescriptor().Extract(new MemoryStream(Vector("special.ha")), dir, null, null);
      var written = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/')).Order(StringComparer.Ordinal);
      Assert.That(written, Is.EqualTo(new[] { "file.txt", "sub" }));
      Assert.That(File.ReadAllText(Path.Combine(dir, "file.txt")), Is.EqualTo("hello special\n"));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_UnixHaArchive_When_FileRemoved_Then_SpecialRecordsCopiedByteForByte() {
    var original = Vector("special.ha");
    using var ms = new MemoryStream();
    ms.Write(original);
    Assert.That(HaModifier.Remove(ms, ["file.txt"]), Is.EqualTo(1));

    using var before = new HaReader(new MemoryStream(original));
    using var after = new HaReader(new MemoryStream(ms.ToArray()));
    static byte[] Record(byte[] archive, HaEntry e) => archive.AsSpan((int)e.RecordOffset, e.HeaderLength + (int)e.CompressedSize).ToArray();
    var edited = ms.ToArray();
    foreach (var e in before.Entries.Where(x => x.IsSpecial))
      Assert.That(Record(edited, after.Entries.Single(x => x.FileName == e.FileName)), Is.EqualTo(Record(original, e)), e.FileName);
    Assert.That(after.ReadSpecial(after.Entries.Single(x => x.FileName == "link")).LinkTarget, Is.EqualTo("file.txt"));
  }
}
