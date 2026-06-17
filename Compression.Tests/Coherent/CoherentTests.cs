using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Coherent;

[TestFixture]
public class CoherentTests {

  // Minimal Coherent image in the genuine on-disk format the Linux sysv driver
  // mounts: 512-byte blocks, PDP-11 middle-endian, no numeric magic. The
  // coh_super_block (recognised by s_fname="noname"/s_fpack="nopack") sits at
  // file offset 0 with a duplicate at 512; the inode table starts at block 2
  // (offset 1024).
  //   Block 0 + 1  coh_super_block (s_isize, s_fsize, s_fname, s_fpack)
  //   Block 2      inode table — inode 2 = root, inode 3 = file
  //   Block 4      root directory zone
  //   Block 5      file data zone
  private static byte[] BuildMinimalCoherent() {
    var image = new byte[8 * 512];

    // s_isize (first data zone) = 4; s_fname/s_fpack strings in both sb copies.
    foreach (var b in new[] { 0, 512 }) {
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(b + 0x000, 2), 4);
      "noname"u8.CopyTo(image.AsSpan(b + 0x1E4, 6));
      "nopack"u8.CopyTo(image.AsSpan(b + 0x1EA, 6));
    }

    // Inode table at offset 1024; inode N at ilist + (N-1)*64.
    var ilist = 1024;
    var ino2 = ilist + (2 - 1) * 64; // root
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 0, 2), 0x41ED);
    WritePdp32(image.AsSpan(ino2 + 8), 48);
    Write24Pdp(image.AsSpan(ino2 + 12), 4); // root dir zone = block 4

    var ino3 = ilist + (3 - 1) * 64; // file
    var content = "Coherent says hi\n"u8.ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino3 + 0, 2), 0x81A4);
    WritePdp32(image.AsSpan(ino3 + 8), (uint)content.Length);
    Write24Pdp(image.AsSpan(ino3 + 12), 5); // file data zone = block 5

    // Root directory at block 4 = offset 2048.
    var rootDir = 4 * 512;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 0, 2), 2);
    image[rootDir + 2] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 16, 2), 2);
    image[rootDir + 18] = (byte)'.';
    image[rootDir + 19] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 32, 2), 3);
    Encoding.ASCII.GetBytes("greet").CopyTo(image.AsSpan(rootDir + 34, 14));

    // File data at block 5 = offset 2560.
    content.CopyTo(image.AsSpan(5 * 512));
    return image;
  }

  // PDP-11 3-byte zone: block B → disk [(B>>16), B&0xFF, (B>>8)&0xFF].
  private static void Write24Pdp(Span<byte> dest, uint val) {
    dest[0] = (byte)((val >> 16) & 0xFF);
    dest[1] = (byte)(val & 0xFF);
    dest[2] = (byte)((val >> 8) & 0xFF);
  }

  // PDP-11 middle-endian 32-bit: high half first, each half little-endian.
  private static void WritePdp32(Span<byte> dest, uint val) {
    dest[0] = (byte)((val >> 16) & 0xFF);
    dest[1] = (byte)((val >> 24) & 0xFF);
    dest[2] = (byte)(val & 0xFF);
    dest[3] = (byte)((val >> 8) & 0xFF);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Coherent.CoherentFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Coherent"));
    Assert.That(d.Extensions, Does.Contain(".coh"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(484));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalCoherent();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Coherent.CoherentReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.VolumeName, Is.EqualTo("noname"));
    Assert.That(r.BlockSize, Is.EqualTo(512));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("greet"));
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Coherent says hi\n"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalCoherent();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Coherent.CoherentFormatDescriptor();
    using var s = d.OpenEntry(ms, "greet", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(17));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(17));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalCoherent();
    img[484] ^= 0xFF; // corrupt the s_fname recognition string
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Coherent.CoherentReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalCoherent();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Coherent.CoherentFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("greet"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "greet", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Coherent says hi\n"));
  }
}
