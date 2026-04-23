using System.Buffers.Binary;
using FileFormat.Hdf4;

namespace Compression.Tests.Hdf4;

[TestFixture]
public class Hdf4Tests {

  /// <summary>
  /// Builds a minimal HDF4 file: 4-byte magic, single DDH block with the
  /// supplied DD entries, followed by each entry's payload bytes at the offset
  /// recorded in the DD. All big-endian per HDF4 convention.
  /// </summary>
  private static byte[] BuildHdf4(params (ushort Tag, ushort Ref, byte[] Payload)[] entries) {
    using var ms = new MemoryStream();
    // magic
    ms.Write(Hdf4Reader.Magic);
    // DDH: num_dds u16 BE, next_offset u32 BE. num_dds = entries.Length, next = 0 (end of chain).
    Span<byte> ddh = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16BigEndian(ddh[..2], (ushort)entries.Length);
    BinaryPrimitives.WriteUInt32BigEndian(ddh[2..], 0);
    ms.Write(ddh);

    // Reserve 12 bytes per DD; payloads go at end. Offsets patched as we lay out.
    var ddArea = 12 * entries.Length;
    var payloadStart = 4 + 6 + ddArea;
    var currentOffset = payloadStart;

    // Plan payload offsets/sizes.
    var offsets = new uint[entries.Length];
    var lengths = new uint[entries.Length];
    for (var i = 0; i < entries.Length; i++) {
      offsets[i] = (uint)currentOffset;
      lengths[i] = (uint)entries[i].Payload.Length;
      currentOffset += entries[i].Payload.Length;
    }

    // Write DDs.
    Span<byte> dd = stackalloc byte[12];
    for (var i = 0; i < entries.Length; i++) {
      BinaryPrimitives.WriteUInt16BigEndian(dd[..2], entries[i].Tag);
      BinaryPrimitives.WriteUInt16BigEndian(dd[2..4], entries[i].Ref);
      BinaryPrimitives.WriteUInt32BigEndian(dd[4..8], offsets[i]);
      BinaryPrimitives.WriteUInt32BigEndian(dd[8..12], lengths[i]);
      ms.Write(dd);
    }

    // Write payloads.
    foreach (var (_, _, payload) in entries) ms.Write(payload);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Hdf4Reader_ParsesDdAndSkipsTag0() {
    var payload1 = new byte[16];
    for (var i = 0; i < payload1.Length; i++) payload1[i] = (byte)i;
    var data = BuildHdf4(
      (702, 0x0001, payload1),            // SD (scientific data)
      (0,   0x0002, new byte[4]),         // NONE — should be filtered
      (731, 0x0003, new byte[8]));        // DFR8

    var file = Hdf4Reader.Read(data);
    Assert.Multiple(() => {
      Assert.That(file.MagicKind, Is.EqualTo("HDF4"));
      Assert.That(file.DataDescriptors, Has.Count.EqualTo(2));
      Assert.That(file.DataDescriptors[0].Tag, Is.EqualTo(702));
      Assert.That(file.DataDescriptors[0].Length, Is.EqualTo(16u));
      Assert.That(file.DataDescriptors[1].Tag, Is.EqualTo(731));
      Assert.That(file.TagHistogram.ContainsKey(702), Is.True);
      Assert.That(file.TagHistogram.ContainsKey(731), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void Hdf4Reader_BadMagic_Throws() {
    var buf = new byte[64];
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x00; buf[3] = 0x00;
    Assert.That(() => Hdf4Reader.Read(buf), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Hdf4Reader_LegacyMagic_Accepted() {
    // Minimal legacy header: 8-byte magic + empty DDH + 0 entries.
    using var ms = new MemoryStream();
    ms.Write(Hdf4Reader.LegacyMagic);
    Span<byte> ddh = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16BigEndian(ddh[..2], 0);
    BinaryPrimitives.WriteUInt32BigEndian(ddh[2..], 0);
    ms.Write(ddh);
    var file = Hdf4Reader.Read(ms.ToArray());
    Assert.That(file.MagicKind, Is.EqualTo("HDF-legacy"));
    Assert.That(file.DataDescriptors, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Hdf4Descriptor_ListEmitsMetadataAndOneEntryPerDd() {
    var payload1 = new byte[16];
    Array.Fill<byte>(payload1, 0x77);
    var payload2 = new byte[8];
    Array.Fill<byte>(payload2, 0x88);
    var data = BuildHdf4((702, 1, payload1), (731, 2, payload2));

    using var ms = new MemoryStream(data);
    var entries = new Hdf4FormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("tag_0702_ref_0001.bin"));
      Assert.That(names, Does.Contain("tag_0731_ref_0002.bin"));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Hdf4Descriptor_ExtractWritesPayloadBytes() {
    var payload = new byte[12];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(0x30 + i);
    var data = BuildHdf4((702, 1, payload));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new Hdf4FormatDescriptor().Extract(ms, tmp, null, null);
      var path = Path.Combine(tmp, "tag_0702_ref_0001.bin");
      Assert.That(File.Exists(path), Is.True);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(payload).AsCollection);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
