#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.MachO;

namespace Compression.Tests.MachO;

[TestFixture]
public class MachOTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new MachOFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("MachO"));
    Assert.That(d.Extensions, Contains.Item(".macho"));
    Assert.That(d.Extensions, Contains.Item(".dylib"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    // Fat magic (both 32/64) and single-slice magic (both endiannesses) must all be listed.
    Assert.That(d.MagicSignatures.Count, Is.EqualTo(8));
  }

  [Test, Category("HappyPath")]
  public void Fat_TwoSlices_ProducesTwoEntries() {
    // Build a minimal fat binary: FatMagic, nfat=2, two 32-bit fat_arch records pointing at
    // two embedded single-slice bodies (one x86_64, one arm64). The slice bodies themselves
    // are just zero-filled regions past the last load command — the reader doesn't re-parse
    // the inner slices in fat mode, it just carves bytes by offset.
    const int sliceBytes = 256;
    const int headerEnd = 8 + 2 * 20; // fat header + 2 × fat_arch
    var totalLen = headerEnd + 2 * sliceBytes;
    var buf = new byte[totalLen];
    // Fat magic (big-endian) + nfat_arch = 2.
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), 0xCAFEBABEu);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), 2u);
    // fat_arch #0: cputype=x86_64 (0x01000007)
    BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(8), 0x01000007);
    BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(12), 3);        // cpusubtype
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16), (uint)headerEnd);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20), sliceBytes);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24), 12);      // align
    // fat_arch #1: cputype=arm64 (0x0100000C)
    BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(28), 0x0100000C);
    BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(32), 0);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(36), (uint)(headerEnd + sliceBytes));
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(40), sliceBytes);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(44), 14);

    // Slice body markers — first byte of each slice uniquely tagged so we can verify carving.
    buf[headerEnd] = 0xAA;
    buf[headerEnd + sliceBytes] = 0xBB;

    using var ms = new MemoryStream(buf);
    var entries = new MachOFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("slice_x86_64.macho"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(sliceBytes));
    Assert.That(entries[1].Name, Is.EqualTo("slice_arm64.macho"));

    // Extraction: verify the slice bytes we planted come back out.
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_macho_" + Guid.NewGuid().ToString("N"));
    try {
      ms.Position = 0;
      new MachOFormatDescriptor().Extract(ms, outDir, null, null);
      var sliceX86 = File.ReadAllBytes(Path.Combine(outDir, "slice_x86_64.macho"));
      var sliceArm = File.ReadAllBytes(Path.Combine(outDir, "slice_arm64.macho"));
      Assert.That(sliceX86[0], Is.EqualTo(0xAA));
      Assert.That(sliceArm[0], Is.EqualTo(0xBB));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void SingleSlice_WithSegments_ExposesSegmentEntries() {
    // Build a minimal 64-bit Mach-O with one LC_SEGMENT_64 whose file payload lives
    // at the end of the buffer. Header: magic(4) cputype(4) cpusubtype(4) filetype(4)
    // ncmds(4) sizeofcmds(4) flags(4) reserved(4) = 32 bytes.
    // LC_SEGMENT_64 size is at least 72 bytes (cmd(4) cmdsize(4) segname(16) vmaddr(8)
    // vmsize(8) fileoff(8) filesize(8) maxprot(4) initprot(4) nsects(4) flags(4)).
    const int segCmdSize = 72;
    const int headerSize = 32;
    const int payloadLen = 64;
    var totalLen = headerSize + segCmdSize + payloadLen;
    var buf = new byte[totalLen];

    // mach_header_64: magic=FEEDFACF (LE), rest dummy.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 0xFEEDFACFu);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), 1);              // ncmds
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), segCmdSize);     // sizeofcmds

    // LC_SEGMENT_64 = 0x19
    var segOff = headerSize;
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(segOff), 0x19u);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(segOff + 4), segCmdSize);
    // segname: __TEXT padded to 16 bytes.
    Encoding.ASCII.GetBytes("__TEXT").CopyTo(buf.AsSpan(segOff + 8, 16));
    // fileoff at +40, filesize at +48
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(segOff + 40), (ulong)(headerSize + segCmdSize));
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(segOff + 48), payloadLen);

    // Payload marker.
    buf[headerSize + segCmdSize] = 0xCC;
    buf[^1] = 0xDD;

    using var ms = new MemoryStream(buf);
    var entries = new MachOFormatDescriptor().List(ms, null);
    Assert.That(entries, Is.Not.Empty);
    Assert.That(entries[0].Name, Is.EqualTo("segments/__TEXT.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(payloadLen));
  }
}
