#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Ubifs;

namespace Compression.Tests.Ubifs;

/// <summary>
/// Tests the real UBIFS file reader. Builds synthetic images containing
/// (root inode, file inode, dentry, data node) tuples and verifies path
/// reconstruction, extraction, and bounded OpenEntry behaviour.
/// </summary>
[TestFixture]
public class UbifsReaderTests {

  private const uint Magic = 0x06101831;

  // Node types
  private const byte NtInode = 0;
  private const byte NtData = 1;
  private const byte NtDent = 2;

  // Compression types
  private const ushort ComprNone = 0;
  private const ushort ComprZlib = 2;

  private static void WriteCommonHeader(Span<byte> buf, byte type, uint totLen, ulong sqnum) {
    BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(4, 4), 0xDEADBEEFu); // crc
    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(8, 8), sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(16, 4), totLen);
    buf[20] = type;
    buf[21] = 0; buf[22] = 0; buf[23] = 0;
  }

  /// <summary>
  /// Inode node: common(24) + key(16, inum at +0) + creat_sqnum(8) + size(8)
  /// + 3*time(24) + 3*time_nsec(12) + nlink(4) + uid(4) + gid(4) + mode(4) + ...
  /// Mode offset = 24 + 16 + 8 + 8 + 3*8 + 3*4 + 3*4 = 100.
  /// </summary>
  private static byte[] BuildInodeNode(uint inum, ulong size, uint mode, ulong sqnum) {
    var len = 100 + 4 + 24; // mode at +100, plus padding to keep len sane
    var buf = new byte[len];
    WriteCommonHeader(buf.AsSpan(0, 24), NtInode, (uint)len, sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), inum); // key inum
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(48, 8), size);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(100, 4), mode);
    return buf;
  }

  /// <summary>
  /// Data node: common(24) + key(16: inum + (KeyTypeData=1 in top 3 bits | blockIdx in low 29 bits))
  /// + size(4) + compr_type(2) + compr_size(2) + payload.
  /// </summary>
  private static byte[] BuildDataNode(uint inum, uint blockIdx, byte[] payload, ushort comprType, uint uncompressedSize, ulong sqnum) {
    var len = 24 + 16 + 4 + 2 + 2 + payload.Length;
    var buf = new byte[len];
    WriteCommonHeader(buf.AsSpan(0, 24), NtData, (uint)len, sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), inum);
    var keyHi = (1u << 29) | (blockIdx & 0x1FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), keyHi);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), uncompressedSize);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(44, 2), comprType);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(46, 2), (ushort)payload.Length);
    payload.CopyTo(buf.AsSpan(48));
    return buf;
  }

  /// <summary>
  /// Dentry node: common(24) + key(16, parent inum at +0) + child_inum(8)
  /// + pad(1) + type(1) + nlen(2) + name(nlen).
  /// </summary>
  private static byte[] BuildDentNode(uint parentInum, uint childInum, byte dtType, string name, ulong sqnum) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var len = 24 + 16 + 8 + 4 + nameBytes.Length;
    var buf = new byte[len];
    WriteCommonHeader(buf.AsSpan(0, 24), NtDent, (uint)len, sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), parentInum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), childInum);
    buf[49] = dtType; // type at offset 49 (after parent key + child inum + pad)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(50, 2), (ushort)nameBytes.Length);
    nameBytes.CopyTo(buf.AsSpan(52));
    return buf;
  }

  private static byte[] Zlib(byte[] data) {
    using var output = new MemoryStream();
    using (var zls = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
      zls.Write(data, 0, data.Length);
    return output.ToArray();
  }

  /// <summary>
  /// Build a minimal UBIFS image with one regular file "hello.txt" containing
  /// the given content, compressed with zlib.
  /// </summary>
  private static byte[] BuildMinimalImage(byte[] content, bool zlib = true) {
    // Layout: root inode (1, S_IFDIR) + file inode (42, S_IFREG, size) + dentry (parent 1, child 42) + data node (inum 42 block 0)
    const uint ModeDir = 0x4000;
    const uint ModeFile = 0x8000;
    const byte DtReg = 1;

    var rootIno = BuildInodeNode(inum: 1, size: 0, mode: ModeDir, sqnum: 1);
    var fileIno = BuildInodeNode(inum: 42, size: (ulong)content.Length, mode: ModeFile, sqnum: 2);
    var dent = BuildDentNode(parentInum: 1, childInum: 42, dtType: DtReg, name: "hello.txt", sqnum: 3);

    byte[] payload;
    ushort comprType;
    if (zlib) {
      payload = Zlib(content);
      comprType = ComprZlib;
    } else {
      payload = content;
      comprType = ComprNone;
    }
    var data = BuildDataNode(inum: 42, blockIdx: 0, payload, comprType, (uint)content.Length, sqnum: 4);

    var total = rootIno.Length + fileIno.Length + dent.Length + data.Length;
    var img = new byte[total];
    var pos = 0;
    foreach (var part in new[] { rootIno, fileIno, dent, data }) {
      part.CopyTo(img.AsSpan(pos));
      pos += part.Length;
    }
    return img;
  }

  [Test, Category("HappyPath")]
  public void Reader_StoredData_ExtractsFile() {
    var content = "Hello UBIFS!"u8.ToArray();
    var img = BuildMinimalImage(content, zlib: false);
    var r = new UbifsFileReader(img);
    Assert.That(r.ParseOk, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Entries[0].IsDirectory, Is.False);

    var bytes = r.Extract(r.Entries[0]);
    Assert.That(bytes, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Reader_ZlibData_InflatesAndExtracts() {
    var content = Encoding.UTF8.GetBytes(new string('A', 500) + " end");
    var img = BuildMinimalImage(content, zlib: true);
    var r = new UbifsFileReader(img);
    Assert.That(r.ParseOk, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var bytes = r.Extract(r.Entries[0]);
    Assert.That(bytes, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_IncludesFile() {
    var content = "List me!"u8.ToArray();
    var img = BuildMinimalImage(content, zlib: false);
    var d = new UbifsFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.ubifs"));
    Assert.That(names, Does.Contain("hello.txt"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_WritesFile() {
    var content = "Extract me!"u8.ToArray();
    var img = BuildMinimalImage(content, zlib: false);
    var d = new UbifsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ubifs_ex_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, outDir, null, null);
      var helloPath = Path.Combine(outDir, "hello.txt");
      Assert.That(File.Exists(helloPath), Is.True);
      Assert.That(File.ReadAllBytes(helloPath), Is.EqualTo(content));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream_ReadPastSizeReturnsZero() {
    var content = "OpenEntry yo"u8.ToArray();
    var img = BuildMinimalImage(content, zlib: true);
    var d = new UbifsFormatDescriptor();
    using var ms = new MemoryStream(img);

    using var s = d.OpenEntry(ms, "hello.txt", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>(), "OpenEntry must return BoundedEntryStream");
    Assert.That(s.Length, Is.EqualTo(content.Length));

    var buf = new byte[128];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(content.Length));
    Assert.That(buf.AsSpan(0, n).ToArray(), Is.EqualTo(content));

    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past LogicalSize returns 0 (EOF)");
  }

  [Test, Category("Sad")]
  public void OpenEntry_UnknownName_ReturnsEmptyBoundedStream() {
    var content = "x"u8.ToArray();
    var img = BuildMinimalImage(content, zlib: false);
    var d = new UbifsFormatDescriptor();
    using var ms = new MemoryStream(img);
    using var s = d.OpenEntry(ms, "no-such-file", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(0));
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesRwScope() {
    var d = new UbifsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "UBIFS write path emits superblock + master + linear log of inode/dentry/data nodes.");
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "UBIFS R/W path appends fresh INO/DENT/DATA nodes at the journal head (committed nodes byte-identical).");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }
}
