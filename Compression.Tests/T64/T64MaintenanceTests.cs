using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.T64;

namespace Compression.Tests.T64;

[TestFixture]
public class T64MaintenanceTests {

  [Test, Category("Compatibility")]
  public void Reader_Conv64BrokenEndAddress_UsesPhysicalPayloadExtent() {
    var payload = "HELLO"u8.ToArray();
    var writer = new T64Writer();
    writer.AddFile("BROKEN", 0x0801, payload);
    var image = writer.Build();

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(64 + 4), 0xC3C6);

    using var stream = new MemoryStream(image);
    using var reader = new T64Reader(stream);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("EdgeCase")]
  public void Writer_EntryEndingAt10000_RoundTripsFullPayload() {
    var payload = Enumerable.Range(0, 0x4000).Select(static i => (byte)i).ToArray();
    var writer = new T64Writer();
    writer.AddFile("TOPMEM", 0xC000, payload);

    using var stream = new MemoryStream(writer.Build());
    using var reader = new T64Reader(stream);
    var entry = reader.Entries.Single();

    Assert.That(entry.EndAddress, Is.Zero, "exclusive $10000 end must wrap in the 16-bit field");
    Assert.That(entry.Size, Is.EqualTo(payload.Length));
    Assert.That(reader.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_DirectoryPastEof_Throws() {
    var image = new byte[64];
    "C64S tape image file"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(32), 0x0100);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(34), 1);

    using var stream = new MemoryStream(image);
    Assert.Throws<InvalidDataException>(() => _ = new T64Reader(stream));
  }

  [Test, Category("ErrorHandling")]
  public void Add_InvalidReplacement_DoesNotDeleteExistingEntry() {
    var oldPayload = "keep-me"u8.ToArray();
    var writer = new T64Writer();
    writer.AddFile("TARGET", oldPayload);
    using var stream = Writable(writer.Build());

    Assert.Throws<InvalidOperationException>(() =>
      T64InPlaceModifier.AddFile(stream, "TARGET", new byte[0x10000], 0x0801));

    using var reader = new T64Reader(stream);
    Assert.That(reader.Entries.Select(static e => e.Name), Is.EqualTo(new[] { "TARGET" }));
    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(oldPayload));
  }

  [Test, Category("RoundTrip")]
  public void BlockMover_ForwardOverlap_IsMemmoveSafeAndDoesNotZeroDestination() {
    using var stream = Writable([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

    new T64BlockMover().MoveExtent(stream, 0, 2, 6, zeroSource: true);

    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0, 0, 1, 2, 3, 4, 5, 6, 9, 10 }));
  }

  [Test, Category("RoundTrip")]
  public void Defragment_MovesPayloadToDirectoryEnd_AndPreservesMetadata() {
    var (stream, payload) = BuildGappedImage();
    using (stream) {
      var descriptor = new T64FormatDescriptor();
      descriptor.Defragment(stream);

      Assert.That(stream.Length, Is.EqualTo(64 + 2 * 32 + payload.Length));
      using var reader = new T64Reader(stream);
      var entry = reader.Entries.Single();
      Assert.Multiple(() => {
        Assert.That(entry.DataOffset, Is.EqualTo(128));
        Assert.That(entry.StartAddress, Is.EqualTo(0x2000));
        Assert.That(entry.FileType, Is.EqualTo(0x81));
        Assert.That(reader.Version, Is.EqualTo(0x0101));
        Assert.That(reader.TapeName, Is.EqualTo("GAPPED TAPE"));
        Assert.That(reader.Extract(entry), Is.EqualTo(payload));
      });
    }
  }

  [Test, Category("RoundTrip")]
  public void WipeUnusedSpace_ZerosFreeDirectorySlotAndPayloadGaps() {
    var (stream, payload) = BuildGappedImage();
    using (stream) {
      var descriptor = new T64FormatDescriptor();
      var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(stream);
      var raw = stream.ToArray();

      Assert.Multiple(() => {
        Assert.That(wiped, Is.EqualTo(32 + 4 + 3));
        Assert.That(raw.AsSpan(96, 32).ToArray(), Is.All.EqualTo(0));
        Assert.That(raw.AsSpan(128, 4).ToArray(), Is.All.EqualTo(0));
        Assert.That(raw.AsSpan(132, payload.Length).ToArray(), Is.EqualTo(payload));
        Assert.That(raw.AsSpan(132 + payload.Length, 3).ToArray(), Is.All.EqualTo(0));
      });
    }
  }

  [Test, Category("RoundTrip")]
  public void Shrink_TightPacksAndPreservesT64Metadata() {
    var (input, payload) = BuildGappedImage();
    using (input)
    using (var output = new MemoryStream()) {
      var descriptor = new T64FormatDescriptor();
      ((IArchiveShrinkable)descriptor).Shrink(input, output);

      Assert.That(output.Length, Is.EqualTo(64 + 32 + payload.Length));
      using var reader = new T64Reader(output);
      var entry = reader.Entries.Single();
      Assert.Multiple(() => {
        Assert.That(reader.DirectoryEntryCount, Is.EqualTo(1));
        Assert.That(reader.Version, Is.EqualTo(0x0101));
        Assert.That(reader.TapeName, Is.EqualTo("GAPPED TAPE"));
        Assert.That(entry.StartAddress, Is.EqualTo(0x2000));
        Assert.That(entry.FileType, Is.EqualTo(0x81));
        Assert.That(reader.Extract(entry), Is.EqualTo(payload));
      });
    }
  }

  [Test, Category("RoundTrip")]
  public void Purge_LeavesValidCanonicalEmptyImage() {
    var (stream, _) = BuildGappedImage();
    using (stream) {
      var descriptor = new T64FormatDescriptor();
      ((IArchivePurgeable)descriptor).Purge(stream);

      Assert.That(stream.Length, Is.EqualTo(64));
      using var reader = new T64Reader(stream);
      Assert.Multiple(() => {
        Assert.That(reader.Entries, Is.Empty);
        Assert.That(reader.DirectoryEntryCount, Is.Zero);
        Assert.That(reader.UsedEntryCount, Is.Zero);
        Assert.That(reader.Version, Is.EqualTo(0x0101));
        Assert.That(reader.TapeName, Is.EqualTo("GAPPED TAPE"));
      });
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesSupportedMaintenanceCapabilities() {
    var descriptor = new T64FormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>(),
        "T64 has no selectable filesystem/block geometry to re-layout.");
    });
  }

  private static (MemoryStream Stream, byte[] Payload) BuildGappedImage() {
    var payload = "PAYLOAD"u8.ToArray();
    const int payloadOffset = 132; // 64-byte header + 2 slots + 4 dead bytes
    var image = new byte[payloadOffset + 7 + 3];

    "C64S tape image file"u8.CopyTo(image);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(32), 0x0101);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(34), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(36), 1);
    "GAPPED TAPE"u8.CopyTo(image.AsSpan(40));
    image.AsSpan(51, 13).Fill(0x20);

    image[64] = 1;
    image[65] = 0x81;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(66), 0x2000);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(68), (ushort)(0x2000 + payload.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(72), payloadOffset);
    "GAPTEST"u8.CopyTo(image.AsSpan(80));
    image.AsSpan(87, 9).Fill(0x20);

    // Free directory slot containing stale metadata, then dead payload gap/tail.
    image.AsSpan(96, 32).Fill(0xA5);
    image[96] = 0;
    image.AsSpan(128, 4).Fill(0xD1);
    payload.CopyTo(image, payloadOffset);
    image.AsSpan(payloadOffset + payload.Length, 3).Fill(0xD2);

    return (Writable(image), payload);
  }

  private static MemoryStream Writable(byte[] bytes) {
    var result = new MemoryStream();
    result.Write(bytes);
    result.Position = 0;
    return result;
  }
}
