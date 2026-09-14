using System.Buffers.Binary;
using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

[TestFixture]
public class GpfsRawCorrelationTests {
  private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

  [Test]
  public void ReadInodeReplicas_UsesManifestSectorGeometryAndRestoresStreams() {
    var manifest = GpfsEvidenceManifest.Parse(BuildManifest());
    var first = new byte[4096];
    var second = new byte[4096];
    byte[] replicaA = [1, 2, 3, 4, 5, 6, 7, 8];
    byte[] replicaB = [8, 7, 6, 5, 4, 3, 2, 1];
    replicaA.CopyTo(first, 2 * 16);
    replicaB.CopyTo(second, 3 * 16);
    using var firstStream = new MemoryStream(first, writable: false) { Position = 11 };
    using var secondStream = new MemoryStream(second, writable: false) { Position = 13 };
    var images = new Dictionary<int, Stream> {
      [1] = firstStream,
      [2] = secondStream,
    };
    var inode = CreateInode([new GpfsDiskAddress(1, 2), new GpfsDiskAddress(2, 3)]);

    var replicas = GpfsRawCorrelation.ReadInodeReplicas(manifest, inode, images);

    Assert.Multiple(() => {
      Assert.That(replicas, Has.Count.EqualTo(2));
      Assert.That(replicas[0].NsdName, Is.EqualTo("nsd1"));
      Assert.That(replicas[0].Bytes, Is.EqualTo(replicaA));
      Assert.That(replicas[1].NsdName, Is.EqualTo("nsd2"));
      Assert.That(replicas[1].Bytes, Is.EqualTo(replicaB));
      Assert.That(firstStream.Position, Is.EqualTo(11));
      Assert.That(secondStream.Position, Is.EqualTo(13));
    });
  }

  [Test]
  public void GetByteOffset_RejectsOverflowAndTruncation() {
    Assert.Multiple(() => {
      Assert.Throws<InvalidDataException>(() =>
        GpfsRawCorrelation.GetByteOffset(new GpfsDiskAddress(1, long.MaxValue), 4096, 4096, long.MaxValue));
      Assert.Throws<InvalidDataException>(() =>
        GpfsRawCorrelation.GetByteOffset(new GpfsDiskAddress(1, 8), 512, 1, 4096));
    });
  }

  [Test]
  public void FindUInt32Candidates_ReportsEndianAndOffsetWithoutChoosingOne() {
    const uint checksum = 0x609701E6;
    var bytes = new byte[20];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), checksum);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(11), checksum);

    var candidates = GpfsRawCorrelation.FindUInt32Candidates(bytes, checksum);

    Assert.That(candidates, Does.Contain(new GpfsUInt32FieldCandidate(2, GpfsByteOrder.LittleEndian)));
    Assert.That(candidates, Does.Contain(new GpfsUInt32FieldCandidate(11, GpfsByteOrder.BigEndian)));
  }

  [Test]
  public void IntersectAddressCandidates_RejectsCoincidentalEncodingFromFirstCapture() {
    var first = new byte[48];
    var second = new byte[48];
    var firstAddress = new GpfsDiskAddress(7, 0x0102030405060708);
    var secondAddress = new GpfsDiskAddress(9, 0x0112233445566778);
    WriteAddress(first.AsSpan(5), firstAddress);
    WriteAddress(second.AsSpan(5), secondAddress);

    // A plausible-looking first-capture duplicate at another offset must not
    // survive unless the second independently changed address occupies it too.
    WriteAddress(first.AsSpan(27), firstAddress);

    var candidates = GpfsRawCorrelation.IntersectAddressCandidates(first, firstAddress, second, secondAddress);

    Assert.That(candidates, Does.Contain(new GpfsAddressFieldCandidate(
      5, 2, 8, GpfsByteOrder.LittleEndian, SectorFirst: false)));
    Assert.That(candidates.Any(static x => x.Offset == 27), Is.False);
  }

  [Test]
  public void FindWordVectorCandidates_ConstrainsMmfsckxWordsByEndianAndOffset() {
    ulong[] expected = [0x0001FFFFF8000000UL, 0UL];
    var bytes = new byte[40];
    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(9), expected[0]);
    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(17), expected[1]);

    var candidates = GpfsRawCorrelation.FindWordVectorCandidates(bytes, expected);

    Assert.That(candidates, Does.Contain(new GpfsWordVectorCandidate(9, GpfsByteOrder.BigEndian, 2)));
  }

  [Test]
  public void InferBitmapOrder_UsesMultipleKnownAllocationTransitions() {
    GpfsBitmapTransitionObservation[] observations = [
      new(100, new GpfsBitTransition(20, 0x04)),
      new(101, new GpfsBitTransition(20, 0x08)),
      new(102, new GpfsBitTransition(20, 0x10)),
    ];

    var candidates = GpfsRawCorrelation.InferBitmapOrder(observations);

    Assert.That(candidates, Is.EqualTo(new[] { new GpfsBitmapOrderCandidate(LsbFirst: true, BaseBit: 62) }));
  }

  [Test]
  public void InferBitmapOrder_DoesNotPretendOneObservationEstablishesBitDirection() {
    GpfsBitmapTransitionObservation[] observations = [
      new(100, new GpfsBitTransition(20, 0x04)),
    ];

    Assert.That(GpfsRawCorrelation.InferBitmapOrder(observations), Is.Empty);
  }

  private static GpfsInodeOracle CreateInode(IReadOnlyList<GpfsDiskAddress> addresses)
    => new(
      InodeNumber: 64,
      SnapshotId: 0,
      IndexInBlock: 64,
      InodeBlock: 0,
      PhysicalAddresses: addresses,
      InodeSize: 8,
      AddressSlots: 330,
      IndirectionLevel: "DIRECT",
      Status: "FILE",
      ObjectVersion: 0,
      Generation: 1,
      LinkCount: 1,
      BlockSizeCode: 0,
      LastBlockSubblocks: 0,
      Checksum: 0x609701E6,
      ChecksumValid: true,
      FileSize: 0,
      FullBlocks: 0,
      CurrentMetadataReplicas: addresses.Count,
      MaxMetadataReplicas: addresses.Count,
      CurrentDataReplicas: 1,
      MaxDataReplicas: 2,
      DataPoolIndex: 0);

  private static void WriteAddress(Span<byte> destination, GpfsDiskAddress address) {
    BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)address.DiskId));
    BinaryPrimitives.WriteInt64LittleEndian(destination[2..], address.Sector);
  }

  private static string BuildManifest()
    => string.Join('\n', new[] {
      "meta\tcorpus-id\ta",
      "meta\tcapture-id\t000-anchor",
      "meta\toperation\tanchor",
      "meta\tstorage-scale-version\t6.0.1.0",
      "meta\tformat-version\t39.00",
      "meta\tfilesystem-uid\tuid-a",
      "meta\tcapture-state\tunmounted-clean",
      $"nsd\tnsd1\t1\t4096\t16\t{Sha}\traw/nsd1.img",
      $"nsd\tnsd2\t2\t4096\t16\t{Sha}\traw/nsd2.img",
      $"artifact\tmmfsckx\t{Sha}\toracle/mmfsckx.txt",
      $"artifact\ttsdbfs\t{Sha}\toracle/tsdbfs.txt",
      $"artifact\tmmfileid\t{Sha}\toracle/mmfileid.txt",
      $"artifact\tmmgetlocation\t{Sha}\toracle/mmgetlocation.txt",
      $"artifact\tmmlsdisk\t{Sha}\toracle/mmlsdisk.txt",
      $"artifact\tmmlsnsd\t{Sha}\toracle/mmlsnsd.txt",
    });
}
