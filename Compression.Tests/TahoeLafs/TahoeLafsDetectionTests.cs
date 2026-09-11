using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

[TestFixture]
public class TahoeLafsDetectionTests {
  private const int ImmutableHeaderSize = 12;
  private const int ImmutableLeaseSize = 72;
  private const int MutableDataOffset = 468;
  private const int MutableLeaseSize = 92;

  private static ReadOnlySpan<byte> MutableV1Magic => [
    0x54, 0x61, 0x68, 0x6F, 0x65, 0x20, 0x6D, 0x75,
    0x74, 0x61, 0x62, 0x6C, 0x65, 0x20, 0x63, 0x6F,
    0x6E, 0x74, 0x61, 0x69, 0x6E, 0x65, 0x72, 0x20,
    0x76, 0x31, 0x0A, 0x75, 0x09, 0x44, 0x03, 0x8E,
  ];

  private static ReadOnlySpan<byte> MutableV2Magic => [
    0x54, 0x61, 0x68, 0x6F, 0x65, 0x20, 0x6D, 0x75,
    0x74, 0x61, 0x62, 0x6C, 0x65, 0x20, 0x63, 0x6F,
    0x6E, 0x74, 0x61, 0x69, 0x6E, 0x65, 0x72, 0x20,
    0x76, 0x32, 0x0A, 0xC3, 0x55, 0x21, 0x99, 0x25,
  ];

  private static byte[] BuildImmutable(uint version = 1, int payloadLen = 64, uint leaseCount = 1, uint? legacyDataLength = null) {
    var image = new byte[checked(ImmutableHeaderSize + payloadLen + (int)leaseCount * ImmutableLeaseSize)];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), version);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), legacyDataLength ?? (uint)payloadLen);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8, 4), leaseCount);
    for (var i = 0; i < payloadLen; ++i)
      image[ImmutableHeaderSize + i] = (byte)(i ^ 0x5A);

    var leaseOffset = ImmutableHeaderSize + payloadLen;
    for (uint i = 0; i < leaseCount; ++i) {
      var offset = leaseOffset + checked((int)i) * ImmutableLeaseSize;
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset, 4), i + 1);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 68, 4), 1_800_000_000u + i);
    }
    return image;
  }

  private static byte[] BuildMutable(uint version, int payloadLen = 64, int gap = 16, uint extraLeaseCount = 1, int trailing = 0) {
    var tailLength = checked(4 + (int)extraLeaseCount * MutableLeaseSize);
    var extraLeaseOffset = MutableDataOffset + payloadLen + gap;
    var image = new byte[checked(extraLeaseOffset + tailLength + trailing)];
    (version == 1 ? MutableV1Magic : MutableV2Magic).CopyTo(image);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(84, 8), (ulong)payloadLen);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(92, 8), (ulong)extraLeaseOffset);

    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100, 4), 1);
    for (var i = 0; i < payloadLen; ++i)
      image[MutableDataOffset + i] = (byte)(0xA5 ^ i);

    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(extraLeaseOffset, 4), extraLeaseCount);
    for (uint i = 0; i < extraLeaseCount; ++i) {
      var offset = extraLeaseOffset + 4 + checked((int)i) * MutableLeaseSize;
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset, 4), i + 2);
    }
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AndMagic() {
    var descriptor = new TahoeLafsFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("TahoeLafs"));
    Assert.That(descriptor.DisplayName, Does.Contain("Tahoe"));
    Assert.That(descriptor.Extensions, Does.Contain(".tahoe-share"));
    Assert.That(descriptor.Extensions, Does.Contain(".share"));
    Assert.That(descriptor.Extensions, Does.Contain(".tahoe-cap"));
    Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(5));
    Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x01 }));
    Assert.That(descriptor.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x02 }));
    Assert.That(descriptor.MagicSignatures[2].Bytes, Is.EqualTo(MutableV1Magic.ToArray()));
    Assert.That(descriptor.MagicSignatures[3].Bytes, Is.EqualTo(MutableV2Magic.ToArray()));
    Assert.That(descriptor.MagicSignatures[4].Bytes, Is.EqualTo(Encoding.ASCII.GetBytes(TahoeLafsConnection.Magic)));
    Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Read_ImmutableV1_UsesLeaseTailToFindPayload() {
    using var stream = new MemoryStream(BuildImmutable(version: 1, payloadLen: 128, leaseCount: 3, legacyDataLength: 7));
    using var reader = new TahoeLafsReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidHeader, Is.True);
      Assert.That(reader.ShareKind, Is.EqualTo(TahoeLafsShareKind.Immutable));
      Assert.That(reader.Version, Is.EqualTo(1u));
      Assert.That(reader.DataSize, Is.EqualTo(128));
      Assert.That(reader.HeaderDataSize, Is.EqualTo(7u));
      Assert.That(reader.LeaseCount, Is.EqualTo(3u));
    });

    Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("share.immutable.bin"));
  }

  [Test, Category("Regression")]
  public void Read_ImmutableV2_IsNotMisclassifiedAsMutable() {
    using var stream = new MemoryStream(BuildImmutable(version: 2, payloadLen: 32, leaseCount: 1));
    using var reader = new TahoeLafsReader(stream);

    Assert.That(reader.ShareKind, Is.EqualTo(TahoeLafsShareKind.Immutable));
    Assert.That(reader.Version, Is.EqualTo(2u));
    Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("share.immutable.bin"));
    Assert.That(reader.Entries.Select(e => e.Name), Does.Not.Contain("share.mutable.bin"));
  }

  [TestCase(1)]
  [TestCase(2)]
  [Category("HappyPath")]
  public void Read_MutableShare_ParsesDistinctContainerLayout(int version) {
    using var stream = new MemoryStream(BuildMutable((uint)version, payloadLen: 48, gap: 24, extraLeaseCount: 2, trailing: 11));
    using var reader = new TahoeLafsReader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ShareKind, Is.EqualTo(TahoeLafsShareKind.Mutable));
      Assert.That(reader.Version, Is.EqualTo((uint)version));
      Assert.That(reader.DataSize, Is.EqualTo(48));
      Assert.That(reader.HeaderDataSize, Is.Null);
      Assert.That(reader.LeaseCount, Is.EqualTo(3u));
      Assert.That(reader.LeaseOffset, Is.EqualTo(MutableDataOffset + 48 + 24));
      Assert.That(reader.FreeSpace, Is.EqualTo(35));
      Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("share.mutable.bin"));
    });
  }

  [Test, Category("HappyPath")]
  public void ConnectionDocument_IsDetectedByItsOwnMagic() {
    var connection = new TahoeLafsConnection(
      new Uri("http://127.0.0.1:3456/"),
      TahoeLafsCapability.Parse("URI:DIR2:write:fingerprint"));
    using var stream = new MemoryStream(connection.Serialize());

    Assert.That(TahoeLafsConnection.TryRead(stream, out var parsed), Is.True);
    Assert.That(parsed!.RootCapability.CanWrite, Is.True);
  }

  [Test, Category("Sad")]
  public void Read_ImmutableLeaseTablePastEnd_Throws() {
    var image = BuildImmutable(payloadLen: 32, leaseCount: 0);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8, 4), 1);
    using var stream = new MemoryStream(image);
    Assert.Throws<InvalidDataException>(() => _ = new TahoeLafsReader(stream));
  }

  [Test, Category("Sad")]
  public void Read_MutableExtraLeaseOffsetInsideData_Throws() {
    var image = BuildMutable(1, payloadLen: 32, gap: 8);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(92, 8), MutableDataOffset + 16);
    using var stream = new MemoryStream(image);
    Assert.Throws<InvalidDataException>(() => _ = new TahoeLafsReader(stream));
  }

  [Test, Category("Sad")]
  public void Read_UnknownHeader_Throws() {
    using var stream = new MemoryStream(new byte[64]);
    Assert.Throws<InvalidDataException>(() => _ = new TahoeLafsReader(stream));
  }
}
