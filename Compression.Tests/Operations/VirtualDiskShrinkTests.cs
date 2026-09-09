using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Vdi;
using FileFormat.Vhd;
using FileFormat.Vhdx;
using FileFormat.Vmdk;

namespace Compression.Tests.Operations;

[TestFixture]
public sealed class VirtualDiskShrinkTests {
  [Test, Category("RoundTrip")]
  public void Vdi_Shrink_RemovesTrailingJunkAndPreservesGuestDisk() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x11);
    using var canonical = new MemoryStream();
    using (var writer = new VdiWriter(canonical, leaveOpen: true, virtualSize: guest.LongLength))
      writer.Write(guest);

    using var bloated = WithTrailingJunk(canonical.ToArray(), 512 * 1024);
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)new VdiFormatDescriptor()).Shrink(bloated, shrunk);

    Assert.That(shrunk.Length, Is.LessThan(bloated.Length));
    shrunk.Position = 0;
    using var reader = new VdiReader(shrunk);
    Assert.That(reader.ExtractDisk(), Is.EqualTo(guest));
  }

  // VDI_UNALLOCATED (0xFFFFFFFF) reads as zeros, exactly like VDI_DISCARDED
  // (0xFFFFFFFE). This is not a guess: `qemu-img create -f vdi` writes
  // 0xFFFFFFFF into every entry of a fresh block map, and `qemu-img convert
  // -O raw` turns that image into an all-zero disk. A maintenance path that
  // refused images containing the sentinel would refuse ordinary qemu output.
  [Test, Category("RoundTrip"), Category("Corruption")]
  public void Vdi_UnallocatedBlocks_ReadAsZeros_AndDoNotBlockShrink() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x12);
    using var canonical = new MemoryStream();
    using (var writer = new VdiWriter(canonical, leaveOpen: true, virtualSize: guest.LongLength))
      writer.Write(guest);

    var source = canonical.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(512 + 4, 4), 0xFFFF_FFFFu);
    using (var reader = new VdiReader(new MemoryStream(source, writable: false))) {
      Assert.That(reader.HasUndefinedBlocks, Is.True);
      Assert.That(reader.ExtractDisk(), Is.EqualTo(guest),
        "the block was already zero, so either sentinel must decode to the same guest disk");
    }

    using var input = new MemoryStream(source, writable: false);
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)new VdiFormatDescriptor()).Shrink(input, shrunk);

    shrunk.Position = 0;
    using var shrunkReader = new VdiReader(shrunk);
    Assert.That(shrunkReader.ExtractDisk(), Is.EqualTo(guest),
      "shrinking an image containing VDI_UNALLOCATED must preserve the guest disk");
  }

  [Test, Category("RoundTrip")]
  public void Vhd_Shrink_ConvertsFixedContainerToSparseDynamicWithoutChangingGuestDisk() {
    var guest = MostlyZeroDisk(8 * 1024 * 1024, 0x22);
    var writer = new VhdWriter();
    writer.SetDiskData(guest);
    using var fixedImage = new MemoryStream(writer.Build(), writable: false);
    using var shrunk = new MemoryStream();

    ((IArchiveShrinkable)new VhdFormatDescriptor()).Shrink(fixedImage, shrunk);

    Assert.That(shrunk.Length, Is.LessThan(fixedImage.Length));
    shrunk.Position = 0;
    using var reader = new VhdReader(shrunk);
    var diskEntry = reader.Entries.Single(e => !e.IsDirectory);
    Assert.That(reader.Extract(diskEntry), Is.EqualTo(guest));
  }

  [Test, Category("RoundTrip"), Category("Corruption")]
  public void Vhd_Reader_HonorsDynamicSectorBitmapInsteadOfStalePayloadBytes() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x23);
    var writer = new VhdWriter();
    writer.SetDiskData(guest);
    var image = writer.BuildDynamic();

    const int batOffset = 1536;
    var firstBlockSector = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(batOffset, 4));
    Assert.That(firstBlockSector, Is.Not.EqualTo(0xFFFF_FFFFu));
    var bitmapOffset = checked((int)firstBlockSector * 512);
    image[bitmapOffset] &= 0x7F; // sector 0 is the MSB of bitmap byte 0

    using var reader = new VhdReader(new MemoryStream(image, writable: false));
    var diskEntry = reader.Entries.Single(e => !e.IsDirectory);
    var extracted = reader.Extract(diskEntry);

    Assert.Multiple(() => {
      Assert.That(extracted.AsSpan(0, 512).ToArray(), Is.All.Zero,
        "a cleared bitmap bit is sparse even when stale bytes remain in the allocated block body");
      Assert.That(extracted.AsSpan(512).ToArray(), Is.EqualTo(guest.AsSpan(512).ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void Vhd_Reader_RejectsDifferencingDiskWithoutParentResolution() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x24);
    var writer = new VhdWriter();
    writer.SetDiskData(guest);
    var image = writer.BuildDynamic();
    SetVhdDiskType(image.AsSpan(0, 512), 4);
    SetVhdDiskType(image.AsSpan(image.Length - 512, 512), 4);

    Assert.That(
      () => new VhdReader(new MemoryStream(image, writable: false)),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("parent-chain"));
  }

  [Test, Category("RoundTrip"), Category("Corruption")]
  public void Vhd_Reader_RejectsTruncatedFixedGuestPayload() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x25);
    var writer = new VhdWriter();
    writer.SetDiskData(guest);
    var image = writer.Build();

    var truncated = new byte[image.Length - 512];
    image.AsSpan(0, guest.Length - 512).CopyTo(truncated);
    image.AsSpan(image.Length - 512, 512).CopyTo(truncated.AsSpan(truncated.Length - 512));

    Assert.That(
      () => new VhdReader(new MemoryStream(truncated, writable: false)),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("truncated"));
  }

  [Test, Category("RoundTrip")]
  public void Vmdk_Shrink_RemovesTrailingJunkAndPreservesGuestDisk() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x33);
    var writer = new VmdkWriter();
    writer.SetDiskData(guest);
    using var bloated = WithTrailingJunk(writer.Build(), 512 * 1024);
    using var shrunk = new MemoryStream();

    ((IArchiveShrinkable)new VmdkFormatDescriptor()).Shrink(bloated, shrunk);

    Assert.That(shrunk.Length, Is.LessThan(bloated.Length));
    shrunk.Position = 0;
    using var reader = new VmdkReader(shrunk);
    var diskEntry = reader.Entries.Single(e => !e.IsDirectory);
    Assert.That(reader.Extract(diskEntry), Is.EqualTo(guest));
  }

  [Test, Category("RoundTrip")]
  public void Vmdk_Reader_UsesRedundantGrainDirectoryWhenHeaderSelectsIt() {
    var guest = MostlyZeroDisk(2 * 1024 * 1024, 0x34);
    var writer = new VmdkWriter();
    writer.SetDiskData(guest);
    var image = writer.Build();

    var flags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8, 4));
    Assert.That(flags & 0x02u, Is.Not.Zero);
    var redundantDirectorySector = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(48, 8));
    var primaryDirectorySector = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(56, 8));
    var redundantTableSector = BinaryPrimitives.ReadUInt32LittleEndian(
      image.AsSpan(checked((int)redundantDirectorySector * 512), 4));
    var primaryTableSector = BinaryPrimitives.ReadUInt32LittleEndian(
      image.AsSpan(checked((int)primaryDirectorySector * 512), 4));
    Assert.That(redundantTableSector, Is.Not.EqualTo(primaryTableSector));

    BinaryPrimitives.WriteUInt32LittleEndian(
      image.AsSpan(checked((int)primaryTableSector * 512), 4), 0);

    using var reader = new VmdkReader(new MemoryStream(image, writable: false));
    var diskEntry = reader.Entries.Single(e => !e.IsDirectory);
    Assert.That(reader.Extract(diskEntry), Is.EqualTo(guest),
      "the redundant-grain-table flag makes the secondary directory authoritative");
  }

  [Test, Category("RoundTrip")]
  public void Vhdx_WriterUsesZeroBatState_AndShrinkPreservesGuestDisk() {
    var guest = MostlyZeroDisk(17 * 1024 * 1024, 0x44);
    var writer = new VhdxWriter();
    writer.SetDiskData(guest);
    var canonicalBytes = writer.Build();

    var batOffset = GetVhdxBatOffset(canonicalBytes);
    var secondPayloadEntry = BinaryPrimitives.ReadUInt64LittleEndian(canonicalBytes.AsSpan(batOffset + 8, 8));
    Assert.That(secondPayloadEntry & 0x07, Is.EqualTo(2),
      "an all-zero VHDX payload block must use PAYLOAD_BLOCK_ZERO, not NOT_PRESENT");

    using var bloated = WithTrailingJunk(canonicalBytes, 1024 * 1024);
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)new VhdxFormatDescriptor()).Shrink(bloated, shrunk);

    Assert.That(shrunk.Length, Is.LessThan(bloated.Length));
    Assert.That(ReadVhdxGuest(shrunk), Is.EqualTo(guest));
  }

  [Test, Category("RoundTrip"), Category("Corruption")]
  public void Vhdx_Shrink_DoesNotCanonicalizeAmbiguousPayloadStates() {
    var guest = MostlyZeroDisk(17 * 1024 * 1024, 0x45);
    var writer = new VhdxWriter();
    writer.SetDiskData(guest);
    var source = writer.Build();
    var batOffset = GetVhdxBatOffset(source);
    BinaryPrimitives.WriteUInt64LittleEndian(source.AsSpan(batOffset + 8, 8), 0); // PAYLOAD_BLOCK_NOT_PRESENT

    using (var guestStream = VhdxStream.TryOpen(new MemoryStream(source, writable: false))) {
      Assert.That(guestStream, Is.Not.Null);
      Assert.That(guestStream!.HasAmbiguousPayloadBlocks, Is.True);
    }

    using var input = new MemoryStream(source, writable: false);
    using var shrunk = new MemoryStream();
    ((IArchiveShrinkable)new VhdxFormatDescriptor()).Shrink(input, shrunk);

    Assert.That(shrunk.ToArray(), Is.EqualTo(source),
      "PAYLOAD_BLOCK_NOT_PRESENT may expose arbitrary historical data and cannot be canonicalized as zero");
  }

  [Test, Category("RoundTrip")]
  public void Vhdx_Defragment_RawFallbackDoesNotConvertUnknownGuestDiskToFat() {
    var guest = MostlyZeroDisk(1024 * 1024, 0x55);
    var writer = new VhdxWriter();
    writer.SetDiskData(guest);
    using var image = new MemoryStream(writer.Build(), writable: true);

    ((IArchiveDefragmentable)new VhdxFormatDescriptor()).Defragment(
      image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(ReadVhdxGuest(image), Is.EqualTo(guest));
  }

  private static byte[] MostlyZeroDisk(int size, byte seed) {
    var data = new byte[size];
    var patternLength = Math.Min(4096, size);
    for (var i = 0; i < patternLength; ++i)
      data[i] = (byte)(seed + i * 17);
    return data;
  }

  private static MemoryStream WithTrailingJunk(byte[] canonical, int junkLength) {
    var result = new MemoryStream(capacity: canonical.Length + junkLength);
    result.Write(canonical);
    var junk = new byte[Math.Min(4096, junkLength)];
    Array.Fill(junk, (byte)0xA5);
    for (var remaining = junkLength; remaining > 0;) {
      var take = Math.Min(junk.Length, remaining);
      result.Write(junk.AsSpan(0, take));
      remaining -= take;
    }
    result.Position = 0;
    return result;
  }

  private static void SetVhdDiskType(Span<byte> footer, uint diskType) {
    BinaryPrimitives.WriteUInt32BigEndian(footer[60..64], diskType);
    footer[64..68].Clear();
    uint sum = 0;
    foreach (var value in footer)
      sum += value;
    BinaryPrimitives.WriteUInt32BigEndian(footer[64..68], ~sum);
  }

  private static int GetVhdxBatOffset(byte[] image)
    => checked((int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x30000 + 16 + 16, 8)));

  private static byte[] ReadVhdxGuest(Stream image) {
    image.Position = 0;
    using var guest = VhdxStream.TryOpen(image)
      ?? throw new AssertionException("VHDX stream did not reopen after maintenance.");
    var bytes = new byte[checked((int)guest.Length)];
    guest.Position = 0;
    guest.ReadExactly(bytes);
    return bytes;
  }
}
