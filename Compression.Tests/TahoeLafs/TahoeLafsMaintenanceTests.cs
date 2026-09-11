#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

[TestFixture]
public class TahoeLafsMaintenanceTests {
  private const int MutableDataOffset = 468;
  private const int MutableLeaseSize = 92;

  private static ReadOnlySpan<byte> MutableV1Magic => [
    0x54, 0x61, 0x68, 0x6F, 0x65, 0x20, 0x6D, 0x75,
    0x74, 0x61, 0x62, 0x6C, 0x65, 0x20, 0x63, 0x6F,
    0x6E, 0x74, 0x61, 0x69, 0x6E, 0x65, 0x72, 0x20,
    0x76, 0x31, 0x0A, 0x75, 0x09, 0x44, 0x03, 0x8E,
  ];

  private static byte[] BuildMutable(int payloadLen, int gap, uint extraLeaseCount, int trailing) {
    var extraLeaseOffset = MutableDataOffset + payloadLen + gap;
    var leaseTailLength = checked(4 + (int)extraLeaseCount * MutableLeaseSize);
    var image = new byte[checked(extraLeaseOffset + leaseTailLength + trailing)];
    MutableV1Magic.CopyTo(image);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(84, 8), (ulong)payloadLen);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(92, 8), (ulong)extraLeaseOffset);

    // Keep one fixed lease active; fill all lease records with distinctive bytes
    // so packing tests catch accidental mutation, not merely lost owner numbers.
    image.AsSpan(100, MutableLeaseSize).Fill(0x31);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100, 4), 1u);
    for (var i = 0; i < payloadLen; ++i)
      image[MutableDataOffset + i] = (byte)(0x80 + i);

    image.AsSpan(MutableDataOffset + payloadLen, gap).Fill(0xCC);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(extraLeaseOffset, 4), extraLeaseCount);
    for (uint i = 0; i < extraLeaseCount; ++i) {
      var offset = extraLeaseOffset + 4 + checked((int)i) * MutableLeaseSize;
      image.AsSpan(offset, MutableLeaseSize).Fill((byte)(0x41 + i));
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset, 4), i + 2);
    }
    image.AsSpan(extraLeaseOffset + leaseTailLength, trailing).Fill(0xDD);
    return image;
  }

  private static byte[] BuildImmutable(int payloadLen = 32, uint leaseCount = 1) {
    const int leaseSize = 72;
    var image = new byte[checked(12 + payloadLen + (int)leaseCount * leaseSize)];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 2u);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), (uint)payloadLen);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8, 4), leaseCount);
    image.AsSpan(12, payloadLen).Fill(0xA7);
    for (uint i = 0; i < leaseCount; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(12 + payloadLen + checked((int)i) * leaseSize, 4), i + 1);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Layout_MarksOnlyMutableGrowthGapsAsFree() {
    const int payloadLen = 32;
    const int gap = 64;
    const uint extraLeases = 2;
    const int trailing = 24;
    var image = BuildMutable(payloadLen, gap, extraLeases, trailing);
    var descriptor = new TahoeLafsFormatDescriptor();

    using var stream = new MemoryStream(image, writable: false);
    var extents = descriptor.EnumerateLayout(stream).ToList();
    var extraOffset = MutableDataOffset + payloadLen + gap;
    var usedEnd = extraOffset + 4 + extraLeases * MutableLeaseSize;

    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.Free).ToArray(), Is.EqualTo(new[] {
      new DefragBlockInfo(MutableDataOffset + payloadLen, gap, DefragBlockKind.Free),
      new DefragBlockInfo(usedEnd, trailing, DefragBlockKind.Free),
    }));
    Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used && e.FileName == "share.mutable.bin"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Wipe_ZeroesOnlyProvenMutableFreeSpace() {
    const int payloadLen = 32;
    const int gap = 64;
    const uint extraLeases = 2;
    const int trailing = 24;
    var original = BuildMutable(payloadLen, gap, extraLeases, trailing);
    var extraOffset = MutableDataOffset + payloadLen + gap;
    var leaseTailLength = 4 + (int)extraLeases * MutableLeaseSize;
    var originalPayload = original.AsSpan(MutableDataOffset, payloadLen).ToArray();
    var originalLeases = original.AsSpan(extraOffset, leaseTailLength).ToArray();
    var descriptor = new TahoeLafsFormatDescriptor();

    using var stream = new MemoryStream(original.ToArray(), writable: true);
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(stream);
    var result = stream.ToArray();

    Assert.Multiple(() => {
      Assert.That(wiped, Is.EqualTo(gap + trailing));
      Assert.That(result.AsSpan(MutableDataOffset, payloadLen).ToArray(), Is.EqualTo(originalPayload));
      Assert.That(result.AsSpan(extraOffset, leaseTailLength).ToArray(), Is.EqualTo(originalLeases));
      Assert.That(result.AsSpan(MutableDataOffset + payloadLen, gap).ToArray(), Is.All.EqualTo(0));
      Assert.That(result.AsSpan(extraOffset + leaseTailLength, trailing).ToArray(), Is.All.EqualTo(0));
    });
  }

  [Test, Category("RoundTrip")]
  public void Shrink_PacksMutableLeaseTailAndRemovesUnusedCapacity() {
    const int payloadLen = 32;
    const int gap = 64;
    const uint extraLeases = 2;
    const int trailing = 24;
    var original = BuildMutable(payloadLen, gap, extraLeases, trailing);
    var oldExtraOffset = MutableDataOffset + payloadLen + gap;
    var leaseTailLength = 4 + (int)extraLeases * MutableLeaseSize;
    var originalPayload = original.AsSpan(MutableDataOffset, payloadLen).ToArray();
    var originalLeases = original.AsSpan(oldExtraOffset, leaseTailLength).ToArray();
    var descriptor = new TahoeLafsFormatDescriptor();

    using var input = new MemoryStream(original, writable: false);
    using var output = new MemoryStream();
    descriptor.Shrink(input, output);
    var result = output.ToArray();

    Assert.That(result.Length, Is.EqualTo(MutableDataOffset + payloadLen + leaseTailLength));
    Assert.That(result.AsSpan(MutableDataOffset, payloadLen).ToArray(), Is.EqualTo(originalPayload));
    Assert.That(result.AsSpan(MutableDataOffset + payloadLen, leaseTailLength).ToArray(), Is.EqualTo(originalLeases));

    using var parsed = new TahoeLafsReader(new MemoryStream(result, writable: false));
    Assert.Multiple(() => {
      Assert.That(parsed.ShareKind, Is.EqualTo(TahoeLafsShareKind.Mutable));
      Assert.That(parsed.LeaseOffset, Is.EqualTo(MutableDataOffset + payloadLen));
      Assert.That(parsed.FreeSpace, Is.Zero);
    });
  }

  [Test, Category("RoundTrip")]
  public void Defrag_PacksMutableLeaseTailWithoutChangingPhysicalLength() {
    const int payloadLen = 32;
    const int gap = 64;
    const uint extraLeases = 2;
    const int trailing = 24;
    var original = BuildMutable(payloadLen, gap, extraLeases, trailing);
    var oldExtraOffset = MutableDataOffset + payloadLen + gap;
    var leaseTailLength = 4 + (int)extraLeases * MutableLeaseSize;
    var originalPayload = original.AsSpan(MutableDataOffset, payloadLen).ToArray();
    var originalLeases = original.AsSpan(oldExtraOffset, leaseTailLength).ToArray();
    var descriptor = new TahoeLafsFormatDescriptor();

    using var image = new MemoryStream(original.ToArray(), writable: true);
    descriptor.Defragment(image);
    var result = image.ToArray();
    var packedEnd = MutableDataOffset + payloadLen + leaseTailLength;

    Assert.Multiple(() => {
      Assert.That(result, Has.Length.EqualTo(original.Length));
      Assert.That(result.AsSpan(MutableDataOffset, payloadLen).ToArray(), Is.EqualTo(originalPayload));
      Assert.That(result.AsSpan(MutableDataOffset + payloadLen, leaseTailLength).ToArray(), Is.EqualTo(originalLeases));
      Assert.That(result.AsSpan(packedEnd).ToArray(), Is.All.EqualTo(0));
    });

    using var parsed = new TahoeLafsReader(new MemoryStream(result, writable: false));
    Assert.That(parsed.LeaseOffset, Is.EqualTo(MutableDataOffset + payloadLen));
  }

  [Test, Category("RoundTrip")]
  public void ImmutableMaintenance_IsByteExactNoOp() {
    var original = BuildImmutable(payloadLen: 48, leaseCount: 2);
    var descriptor = new TahoeLafsFormatDescriptor();

    using var shrinkInput = new MemoryStream(original, writable: false);
    using var shrinkOutput = new MemoryStream();
    descriptor.Shrink(shrinkInput, shrinkOutput);
    Assert.That(shrinkOutput.ToArray(), Is.EqualTo(original));

    using var defragImage = new MemoryStream(original.ToArray(), writable: true);
    descriptor.Defragment(defragImage);
    Assert.That(defragImage.ToArray(), Is.EqualTo(original));

    using var wipeImage = new MemoryStream(original.ToArray(), writable: true);
    Assert.That(((IWipeEmpty)descriptor).WipeUnusedSpace(wipeImage), Is.Zero);
    Assert.That(wipeImage.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Sad")]
  public void Defrag_RejectsFilesystemSpecificPlacementModes() {
    var imageBytes = BuildMutable(payloadLen: 16, gap: 8, extraLeaseCount: 0, trailing: 0);
    var descriptor = new TahoeLafsFormatDescriptor();
    using var image = new MemoryStream(imageBytes, writable: true);

    Assert.Throws<NotSupportedException>(() => descriptor.Defragment(image, new DefragOptions {
      Mode = DefragMode.ConsolidateAtEnd,
    }));
  }
}
