using System.Buffers.Binary;
using System.Text;
using FileFormat.UefiFv;

namespace Compression.Tests.UefiFv;

[TestFixture]
public class UefiFvTests {

  // Builds a minimal FV with one FFS file of the requested type and contents.
  private static byte[] BuildFv(byte ffsType, byte[] ffsContents, Guid? ffsGuid = null) {
    const int headerLen = 72; // 56-byte fixed header + 8-byte BlockMap entry + 8-byte {0,0} terminator
    var fileGuid = ffsGuid ?? Guid.Parse("DEADBEEF-DEAD-BEEF-DEAD-BEEFDEADBEEF");
    const int ffsHeaderLen = 24;
    var ffsSize = ffsHeaderLen + ffsContents.Length;
    var fvLength = headerLen + ffsSize;
    fvLength = (fvLength + 7) & ~7; // round up to 8
    var buf = new byte[fvLength];

    // ── FV header ────────────────────────────────────────────────
    // ZeroVector already zero.
    var fsGuid = Guid.Parse("8C8CE578-8A3D-4F1C-9935-896185C32DD3"); // EFI_FIRMWARE_FILE_SYSTEM2_GUID
    fsGuid.TryWriteBytes(buf.AsSpan(16, 16));
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(32), (ulong)fvLength);
    Encoding.ASCII.GetBytes("_FVH").CopyTo(buf, 40);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(44), 0x0004FEFFu); // attributes
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(48), (ushort)headerLen);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(50), 0); // checksum
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(52), 0); // ext header offset
    buf[54] = 0;
    buf[55] = 2; // revision
    // BlockMap entry: 1 block of fvLength bytes, then {0,0} terminator.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(56), 1u);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60), (uint)fvLength);
    // {0,0} terminator at 64..72 is already zero.

    // ── FFS file ─────────────────────────────────────────────────
    fileGuid.TryWriteBytes(buf.AsSpan(headerLen, 16));
    // IntegrityCheck 2 bytes at +16, left zero for test purposes.
    buf[headerLen + 18] = ffsType;
    buf[headerLen + 19] = 0x00; // attributes
    buf[headerLen + 20] = (byte)(ffsSize & 0xFF);
    buf[headerLen + 21] = (byte)((ffsSize >> 8) & 0xFF);
    buf[headerLen + 22] = (byte)((ffsSize >> 16) & 0xFF);
    buf[headerLen + 23] = 0xF8; // state byte (EFI_FILE_DATA_VALID bits set)
    ffsContents.CopyTo(buf, headerLen + ffsHeaderLen);

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesFvHeaderAndOneFile() {
    var contents = Encoding.ASCII.GetBytes("hello ffs");
    var data = BuildFv(0x07 /* EFI_FV_FILETYPE_DRIVER */, contents);

    var fv = UefiFvReader.Read(data);
    Assert.Multiple(() => {
      Assert.That(fv.Header.Revision, Is.EqualTo((byte)2));
      Assert.That(fv.Header.HeaderLength, Is.EqualTo((ushort)72));
      Assert.That(fv.Header.BlockMap, Has.Count.EqualTo(1));
      Assert.That(fv.Files, Has.Count.EqualTo(1));
      Assert.That(fv.Files[0].Type, Is.EqualTo((byte)0x07));
      Assert.That(fv.Files[0].Contents, Is.EqualTo(contents).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_EmitsGuidNamedEntry() {
    var contents = new byte[32];
    for (var i = 0; i < contents.Length; i++) contents[i] = (byte)i;
    var guid = Guid.Parse("11223344-5566-7788-99AA-BBCCDDEEFF00");
    var data = BuildFv(0x06 /* PEIM */, contents, guid);

    using var ms = new MemoryStream(data);
    var names = new UefiFvFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names.Any(n => n.Contains("PEIM") && n.EndsWith(".bin", StringComparison.Ordinal)), Is.True,
      $"Expected a *_PEIM.bin entry, got: {string.Join(", ", names)}");
  }

  [Test, Category("HappyPath")]
  public void FileTypeName_DecodesAllDefinedValues() {
    Assert.Multiple(() => {
      Assert.That(UefiFvReader.FileTypeName(0x01), Is.EqualTo("EFI_FV_FILETYPE_RAW"));
      Assert.That(UefiFvReader.FileTypeName(0x07), Is.EqualTo("EFI_FV_FILETYPE_DRIVER"));
      Assert.That(UefiFvReader.FileTypeName(0x0B), Is.EqualTo("EFI_FV_FILETYPE_FIRMWARE_VOLUME_IMAGE"));
      Assert.That(UefiFvReader.FileTypeName(0xF0), Is.EqualTo("EFI_FV_FILETYPE_FFS_PAD"));
      Assert.That(UefiFvReader.ShortTypeTag(0x07), Is.EqualTo("DRIVER"));
    });
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsMissingSignature() {
    var data = new byte[128];
    Assert.That(() => UefiFvReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void FindFirst_ReturnsNullWhenNoSignature() {
    var data = new byte[256];
    Assert.That(UefiFvReader.FindFirst(data), Is.Null);
  }
}
