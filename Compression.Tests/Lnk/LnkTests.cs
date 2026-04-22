#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Lnk;

namespace Compression.Tests.Lnk;

[TestFixture]
public class LnkTests {

  // Header bytes: HeaderSize=0x4C, CLSID, LinkFlags, FileAttributes, times, FileSize,
  // IconIndex, ShowCommand, HotKey, Reserved.
  private static readonly byte[] LinkClsid = [
    0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
  ];

  private const uint HasName            = 1u << 2;
  private const uint HasRelativePath    = 1u << 3;
  private const uint HasWorkingDir      = 1u << 4;
  private const uint HasArguments       = 1u << 5;
  private const uint IsUnicode          = 1u << 7;

  private static byte[] BuildHeader(uint flags, uint fileAttr = 0x20, uint fileSize = 1234,
                                    int showCmd = 1, long writeTime = 0) {
    var h = new byte[76];
    // HeaderSize
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0, 4), 0x0000004C);
    // LinkCLSID
    Array.Copy(LinkClsid, 0, h, 4, 16);
    // LinkFlags
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20, 4), flags);
    // FileAttributes
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24, 4), fileAttr);
    // CreationTime/AccessTime/WriteTime
    BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(28, 8), writeTime);
    BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(36, 8), writeTime);
    BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(44, 8), writeTime);
    // FileSize
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(52, 4), fileSize);
    // IconIndex = 0
    // ShowCommand
    BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(60, 4), showCmd);
    // HotKey = 0, Reserved = 0
    return h;
  }

  /// <summary>
  /// Builds a minimal .lnk fixture with Unicode string data for Name,
  /// RelativePath, WorkingDir, Arguments and a terminator block.
  /// </summary>
  private static byte[] MakeUnicodeLnk() {
    using var ms = new MemoryStream();
    var flags = HasName | HasRelativePath | HasWorkingDir | HasArguments | IsUnicode;
    ms.Write(BuildHeader(flags, fileAttr: 0x20, fileSize: 4096, showCmd: 1,
      writeTime: DateTime.UtcNow.ToFileTimeUtc()));

    WriteUnicodeString(ms, "My Shortcut");
    WriteUnicodeString(ms, @"..\target.exe");
    WriteUnicodeString(ms, @"C:\Work");
    WriteUnicodeString(ms, "--flag=1");

    // Terminal extra-data block with size < 4.
    var term = new byte[4];
    ms.Write(term);
    return ms.ToArray();
  }

  private static void WriteUnicodeString(Stream ms, string s) {
    Span<byte> cc = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(cc, (ushort)s.Length);
    ms.Write(cc);
    ms.Write(Encoding.Unicode.GetBytes(s));
  }

  /// <summary>
  /// Builds a minimal .lnk with an extra-data block of signature 0xA0000002.
  /// </summary>
  private static byte[] MakeLnkWithExtraBlock() {
    using var ms = new MemoryStream();
    ms.Write(BuildHeader(IsUnicode));
    // ExtraData: BlockSize=12, Signature=0xA0000002, Payload=4 bytes.
    var block = new byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), 12);
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4, 4), 0xA0000002);
    block[8] = 0xDE; block[9] = 0xAD; block[10] = 0xBE; block[11] = 0xEF;
    ms.Write(block);
    // Terminator.
    ms.Write(new byte[4]);
    return ms.ToArray();
  }

  [Test]
  public void UnicodeStrings_ListsCanonicalEntries() {
    var data = MakeUnicodeLnk();
    using var ms = new MemoryStream(data);
    var entries = new LnkFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.lnk"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names, Contains.Item("header.bin"));
    Assert.That(names, Contains.Item("strings/name.txt"));
    Assert.That(names, Contains.Item("strings/relative_path.txt"));
    Assert.That(names, Contains.Item("strings/working_dir.txt"));
    Assert.That(names, Contains.Item("strings/arguments.txt"));
  }

  [Test]
  public void UnicodeStrings_ExtractDecodesUtf16() {
    var data = MakeUnicodeLnk();
    var tmp = Path.Combine(Path.GetTempPath(), "lnk_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new LnkFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllText(Path.Combine(tmp, "strings/name.txt")), Is.EqualTo("My Shortcut"));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "strings/relative_path.txt")), Is.EqualTo(@"..\target.exe"));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "strings/working_dir.txt")), Is.EqualTo(@"C:\Work"));
      Assert.That(File.ReadAllText(Path.Combine(tmp, "strings/arguments.txt")), Is.EqualTo("--flag=1"));

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("link_flags_hex=0x"));
      Assert.That(ini, Does.Contain("show_command=1"));
      Assert.That(ini, Does.Contain("file_size_target=4096"));
      Assert.That(ini, Does.Contain("name_if_set=My Shortcut"));

      // Header is exactly 76 bytes.
      var header = File.ReadAllBytes(Path.Combine(tmp, "header.bin"));
      Assert.That(header.Length, Is.EqualTo(76));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void ExtraDataBlock_IsSurfacedByHexSignature() {
    var data = MakeLnkWithExtraBlock();
    using var ms = new MemoryStream(data);
    var entries = new LnkFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "extra_data/A0000002.bin"), Is.True);
  }

  [Test]
  public void Descriptor_BasicProperties() {
    var d = new LnkFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Lnk"));
    Assert.That(d.Extensions, Contains.Item(".lnk"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures.Count, Is.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes.Length, Is.EqualTo(20));
  }

  [Test]
  public void List_DoesNotThrowOnTruncated() {
    using var ms = new MemoryStream(new byte[] { 0x4C });
    Assert.DoesNotThrow(() => new LnkFormatDescriptor().List(ms, null));
  }
}
