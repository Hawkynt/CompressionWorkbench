#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Compression.Registry;
using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

[TestFixture]
public sealed class Qcow2MaintenanceTests {
  private const int ClusterSize = 65_536;
  private const ulong ClusterOffsetMask = 0x00FF_FFFF_FFFF_FE00UL;
  private const ulong RefcountOffsetMask = 0xFFFF_FFFF_FFFF_FE00UL;
  private const ulong CopiedFlag = 1UL << 63;
  private const ulong CompressedFlag = 1UL << 62;

  [Test, Category("RoundTrip")]
  public void Writer_SparseAndDenseModesPreserveGuestBytes() {
    var disk = new byte[4 * ClusterSize];
    new Random(1234).NextBytes(disk.AsSpan(2 * ClusterSize, ClusterSize));

    var sparse = Write(disk, sparse: true);
    var dense = Write(disk, sparse: false);

    Assert.That(Read(sparse), Is.EqualTo(disk));
    Assert.That(Read(dense), Is.EqualTo(disk));
    Assert.That(sparse.LongLength, Is.EqualTo(6L * ClusterSize));
    Assert.That(dense.LongLength, Is.EqualTo(9L * ClusterSize));
    Assert.That(sparse.Length, Is.LessThan(dense.Length));

    var l2Offset = GetFirstL2Offset(sparse);
    Assert.That(ReadBe64(sparse, l2Offset), Is.Zero,
      "all-zero guest clusters should be unallocated in sparse mode");
    Assert.That(ReadBe64(sparse, l2Offset + 2 * 8), Is.Not.Zero);
  }

  [Test, Category("RoundTrip")]
  public void Stream_WriteIntoSparseHoleAllocatesAndRefcountsCluster() {
    var disk = new byte[2 * ClusterSize];
    disk[17] = 0x41;
    var image = Write(disk, sparse: true);

    using var backing = Expandable(image);
    using (var guest = Qcow2Stream.TryOpen(backing) ?? throw new AssertionException("QCOW2 stream did not open")) {
      Assert.That(guest.CanWrite, Is.True);
      guest.Position = ClusterSize + 123;
      guest.Write([0xDE, 0xAD, 0xBE, 0xEF]);
      guest.Flush();
    }

    var updated = backing.ToArray();
    var roundTrip = Read(updated);
    Assert.That(roundTrip.AsSpan(ClusterSize + 123, 4).ToArray(), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));

    var l2Offset = GetFirstL2Offset(updated);
    var entry = ReadBe64(updated, l2Offset + 8);
    var hostOffset = checked((long)(entry & ClusterOffsetMask));
    Assert.That(entry & CopiedFlag, Is.Not.Zero);
    Assert.That(ReadRefcount(updated, hostOffset), Is.EqualTo(1));
  }

  [Test, Category("RoundTrip")]
  public void Stream_SharedDataClusterUsesCopyOnWrite() {
    var disk = new byte[ClusterSize];
    new Random(7).NextBytes(disk);
    var image = Write(disk, sparse: true);

    var l2Offset = GetFirstL2Offset(image);
    var oldEntry = ReadBe64(image, l2Offset);
    var oldHostOffset = checked((long)(oldEntry & ClusterOffsetMask));
    var oldByte = image[checked((int)oldHostOffset + 321)];
    WriteRefcount(image, oldHostOffset, 2);
    WriteBe64(image, l2Offset, oldEntry & ~CopiedFlag);

    using var backing = Expandable(image);
    using (var guest = Qcow2Stream.TryOpen(backing) ?? throw new AssertionException("QCOW2 stream did not open")) {
      guest.Position = 321;
      guest.WriteByte((byte)(oldByte ^ 0xFF));
      guest.Flush();
    }

    var updated = backing.ToArray();
    var newEntry = ReadBe64(updated, l2Offset);
    var newHostOffset = checked((long)(newEntry & ClusterOffsetMask));
    Assert.That(newHostOffset, Is.Not.EqualTo(oldHostOffset));
    Assert.That(updated[checked((int)oldHostOffset + 321)], Is.EqualTo(oldByte),
      "copy-on-write must not mutate the shared physical cluster");
    Assert.That(ReadRefcount(updated, oldHostOffset), Is.EqualTo(1));
    Assert.That(ReadRefcount(updated, newHostOffset), Is.EqualTo(1));
    Assert.That(Read(updated)[321], Is.EqualTo((byte)(oldByte ^ 0xFF)));
  }

  [Test, Category("RoundTrip")]
  public void Stream_MissingL2TableIsAllocatedOnFirstWrite() {
    var disk = new byte[ClusterSize];
    var image = Write(disk, sparse: true);
    var l1Offset = checked((int)ReadBe64(image, 40));
    var oldL2Offset = checked((long)(ReadBe64(image, l1Offset) & ClusterOffsetMask));
    WriteBe64(image, l1Offset, 0);
    WriteRefcount(image, oldL2Offset, 0);

    using var backing = Expandable(image);
    using (var guest = Qcow2Stream.TryOpen(backing) ?? throw new AssertionException("QCOW2 stream did not open")) {
      guest.Position = 99;
      guest.WriteByte(0x5A);
    }

    var updated = backing.ToArray();
    var newL1 = ReadBe64(updated, l1Offset);
    var newL2Offset = checked((long)(newL1 & ClusterOffsetMask));
    Assert.That(newL2Offset, Is.Not.Zero);
    Assert.That(newL2Offset, Is.Not.EqualTo(oldL2Offset));
    Assert.That(ReadRefcount(updated, newL2Offset), Is.EqualTo(1));
    Assert.That(Read(updated)[99], Is.EqualTo(0x5A));
  }

  [Test, Category("RoundTrip")]
  public void Reader_DecodesSpecCompressedDescriptorWithoutZlibHeader() {
    const int clusterBits = 12;
    const int clusterSize = 1 << clusterBits;
    var expected = new byte[clusterSize];
    for (var i = 0; i < expected.Length; ++i)
      expected[i] = (byte)(i * 31);

    byte[] compressed;
    using (var compressedStream = new MemoryStream()) {
      using (var deflater = new DeflateStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
        deflater.Write(expected);
      compressed = compressedStream.ToArray();
    }

    const int l1Offset = clusterSize;
    const int l2Offset = 2 * clusterSize;
    var compressedOffset = 3 * clusterSize + 37;
    var firstSector = compressedOffset / 512;
    var lastSector = (compressedOffset + compressed.Length - 1) / 512;
    var additionalSectors = lastSector - firstSector;
    var sizeBits = clusterBits - 8;
    var offsetBits = 62 - sizeBits;
    var descriptor = (ulong)(uint)compressedOffset | ((ulong)(uint)additionalSectors << offsetBits) | CompressedFlag;

    var imageLength = (lastSector + 1) * 512;
    var image = new byte[imageLength];
    new byte[] { 0x51, 0x46, 0x49, 0xFB }.CopyTo(image, 0);
    WriteBe32(image, 4, 2);
    WriteBe32(image, 20, clusterBits);
    WriteBe64(image, 24, clusterSize);
    WriteBe32(image, 36, 1);
    WriteBe64(image, 40, l1Offset);
    WriteBe64(image, l1Offset, (ulong)l2Offset | CopiedFlag);
    WriteBe64(image, l2Offset, descriptor);
    compressed.CopyTo(image, compressedOffset);

    Assert.That(Read(image), Is.EqualTo(expected));
    using var backing = new MemoryStream(image, writable: false);
    using var guest = Qcow2Stream.TryOpen(backing) ?? throw new AssertionException("compressed QCOW2 stream did not open");
    var streamed = new byte[clusterSize];
    guest.ReadExactly(streamed);
    Assert.That(streamed, Is.EqualTo(expected));
  }

  [Test, Category("RoundTrip")]
  public void Reader_V3ZeroClusterIgnoresPreallocatedHostBytes() {
    var disk = new byte[ClusterSize];
    disk.AsSpan().Fill(0xCC);
    var image = Write(disk, sparse: false);
    WriteBe32(image, 4, 3);
    WriteBe32(image, 96, 4);
    WriteBe32(image, 100, 104);

    var l2Offset = GetFirstL2Offset(image);
    var entry = ReadBe64(image, l2Offset);
    WriteBe64(image, l2Offset, entry | 1UL);

    Assert.That(Read(image), Is.EqualTo(new byte[ClusterSize]));
  }

  [Test, Category("RoundTrip")]
  public void LayoutAndWipeUseHostRefcountsNotGuestOffsets() {
    var disk = new byte[ClusterSize];
    disk[123] = 0xA5;
    var original = Write(disk, sparse: true);

    using var image = Expandable(original);
    var freeOffset = image.Length;
    image.SetLength(image.Length + ClusterSize);
    image.Position = freeOffset;
    image.Write(Enumerable.Repeat((byte)0xD7, ClusterSize).ToArray());

    var descriptor = new Qcow2FormatDescriptor();
    var layout = descriptor.EnumerateLayout(image).ToList();
    Assert.That(layout.Any(e => e.Kind == DefragBlockKind.Free
                                && e.Offset <= freeOffset
                                && e.Offset + e.Length >= freeOffset + ClusterSize), Is.True);

    image.Position = 0;
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(ClusterSize));

    var bytes = image.ToArray();
    Assert.That(bytes.AsSpan(checked((int)freeOffset), ClusterSize).ToArray(), Is.EqualTo(new byte[ClusterSize]));
    Assert.That(Read(bytes), Is.EqualTo(disk));
  }

  [Test, Category("RoundTrip")]
  public void Shrink_RewritesDenseZeroClustersSparseAndPreservesGuestDisk() {
    var disk = new byte[5 * ClusterSize];
    new Random(77).NextBytes(disk.AsSpan(2 * ClusterSize, ClusterSize));
    var dense = Write(disk, sparse: false);

    using var source = new MemoryStream(dense, writable: false);
    using var target = new MemoryStream();
    ((IArchiveShrinkable)new Qcow2FormatDescriptor()).Shrink(source, target);

    var compact = target.ToArray();
    Assert.That(compact.Length, Is.LessThan(dense.Length));
    Assert.That(Read(compact), Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesMaintenanceInterfaces() {
    var descriptor = new Qcow2FormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IPartitionEditable>());
    });
  }

  [Test, Category("EdgeCase")]
  public void BackingFileProfileIsRejectedInsteadOfReadingHolesAsZero() {
    var image = Write(new byte[ClusterSize], sparse: true);
    WriteBe64(image, 8, 128);
    WriteBe32(image, 16, 4);

    using var stream = new MemoryStream(image, writable: false);
    Assert.That(Qcow2Stream.TryOpen(stream), Is.Null);
    stream.Position = 0;
    Assert.That(() => new Qcow2Reader(stream), Throws.TypeOf<NotSupportedException>());
  }

  [Test, Category("ExternalFsInterop")]
  public void QemuImg_CheckAndRawConvertAcceptWriterOutput() {
    if (!CanRun("qemu-img", "--version"))
      Assert.Ignore("qemu-img is not installed");

    var disk = new byte[3 * ClusterSize];
    new Random(90210).NextBytes(disk.AsSpan(ClusterSize, ClusterSize));
    var temp = Path.Combine(Path.GetTempPath(), $"cwb-qcow2-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try {
      var qcow = Path.Combine(temp, "test.qcow2");
      var raw = Path.Combine(temp, "test.raw");
      File.WriteAllBytes(qcow, Write(disk, sparse: true));

      var check = Run("qemu-img", $"check \"{qcow}\"");
      Assert.That(check.ExitCode, Is.EqualTo(0), $"qemu-img check failed:\n{check.StdOut}\n{check.StdErr}");

      var convert = Run("qemu-img", $"convert -O raw \"{qcow}\" \"{raw}\"");
      Assert.That(convert.ExitCode, Is.EqualTo(0), $"qemu-img convert failed:\n{convert.StdOut}\n{convert.StdErr}");
      Assert.That(File.ReadAllBytes(raw), Is.EqualTo(disk));
    } finally {
      try { Directory.Delete(temp, recursive: true); } catch { }
    }
  }

  private static byte[] Write(byte[] disk, bool sparse) {
    var writer = new Qcow2Writer();
    writer.SetDiskImage(disk);
    using var stream = new MemoryStream();
    writer.WriteTo(stream, sparse);
    return stream.ToArray();
  }

  private static byte[] Read(byte[] image) {
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Qcow2Reader(stream);
    return reader.ExtractDisk();
  }

  private static MemoryStream Expandable(byte[] data) {
    var stream = new MemoryStream(capacity: data.Length + 8 * ClusterSize);
    stream.Write(data);
    stream.Position = 0;
    return stream;
  }

  private static int GetFirstL2Offset(byte[] image) {
    var l1Offset = checked((int)ReadBe64(image, 40));
    return checked((int)(ReadBe64(image, l1Offset) & ClusterOffsetMask));
  }

  private static ushort ReadRefcount(byte[] image, long hostOffset) {
    var clusterBits = checked((int)ReadBe32(image, 20));
    var clusterSize = 1 << clusterBits;
    var refcountTableOffset = checked((long)ReadBe64(image, 48));
    var hostCluster = hostOffset / clusterSize;
    var entriesPerBlock = clusterSize / 2;
    var tableIndex = hostCluster / entriesPerBlock;
    var blockIndex = hostCluster % entriesPerBlock;
    var blockOffset = checked((long)(ReadBe64(image, checked((int)(refcountTableOffset + tableIndex * 8))) & RefcountOffsetMask));
    return BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(checked((int)(blockOffset + blockIndex * 2)), 2));
  }

  private static void WriteRefcount(byte[] image, long hostOffset, ushort value) {
    var clusterBits = checked((int)ReadBe32(image, 20));
    var clusterSize = 1 << clusterBits;
    var refcountTableOffset = checked((long)ReadBe64(image, 48));
    var hostCluster = hostOffset / clusterSize;
    var entriesPerBlock = clusterSize / 2;
    var tableIndex = hostCluster / entriesPerBlock;
    var blockIndex = hostCluster % entriesPerBlock;
    var blockOffset = checked((long)(ReadBe64(image, checked((int)(refcountTableOffset + tableIndex * 8))) & RefcountOffsetMask));
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(checked((int)(blockOffset + blockIndex * 2)), 2), value);
  }

  private static ulong ReadBe64(byte[] data, int offset)
    => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

  private static void WriteBe64(byte[] data, int offset, ulong value)
    => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), value);

  private static uint ReadBe32(byte[] data, int offset)
    => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

  private static void WriteBe32(byte[] data, int offset, int value)
    => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), checked((uint)value));

  private static void WriteBe64(byte[] data, int offset, int value)
    => WriteBe64(data, offset, checked((ulong)value));

  private static bool CanRun(string executable, string arguments) {
    try {
      var result = Run(executable, arguments);
      return result.ExitCode == 0;
    } catch {
      return false;
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string executable, string arguments) {
    using var process = Process.Start(new ProcessStartInfo {
      FileName = executable,
      Arguments = arguments,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    }) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
  }
}
