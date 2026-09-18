using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Xfs;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class XfsFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeXfsSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Xfs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasExtentMap, Is.True);
    Assert.That(coverage.HasBlockMover, Is.True);
  }

  [Test]
  public void NativeSessionUsesInodeIdentityAndReadsOnlyRequestedRange() {
    var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i * 17 + 11)).ToArray();
    var writer = new XfsWriter();
    writer.AddFile("a/b.bin", payload);
    var image = writer.BuildImageBytes();

    using var backing = new MemoryStream(image, writable: false);
    using var stream = new CountingStream(backing);
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);
    Assert.That(profile.Capabilities & FilesystemDriverCapabilities.SparseFiles,
      Is.EqualTo(FilesystemDriverCapabilities.SparseFiles));

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var a = session.Lookup(session.RootNodeId, "a");
    Assert.That(a, Is.Not.Null);
    var file = session.Lookup(a!.Value, "b.bin");
    Assert.That(file, Is.Not.Null);

    var stat = session.Stat(file!.Value);
    Assert.That(stat.NodeId.Value, Is.GreaterThan(0));
    Assert.That(stat.Size, Is.EqualTo(payload.Length));

    var before = stream.BytesRead;
    using var handle = session.OpenFile(file.Value, FileAccess.Read);
    var slice = new byte[1537];
    var read = handle.Read(1_531_337, slice);
    var delta = stream.BytesRead - before;

    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(1_531_337, slice.Length).ToArray()));
    Assert.That(delta, Is.LessThan(64 * 1024),
      "a positional mounted read must not spool the complete 2 MiB file");
  }

  [Test]
  public void SparseInlineExtentSynthesizesHoleWithoutReadingDiskGarbage() {
    const int holeBlocks = 2;
    var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 31 + 5)).ToArray();
    var writer = new XfsWriter();
    writer.AddFile("sparse.bin", payload);
    var image = writer.BuildImageBytes();

    using (var patch = new MemoryStream(image, writable: true)) {
      var geometry = XfsDriverGeometry.Parse(patch);
      using var reader = new XfsReader(patch, leaveOpen: true);
      var entry = reader.Entries.Single(e => e.Name == "sparse.bin");
      var inodeOffset = InodeOffset(geometry, checked((ulong)entry.InodeNumber));
      var forkOffset = geometry.Version >= 5 ? 176 : 100;
      var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(checked((int)inodeOffset + forkOffset), 8));
      hi &= 0x8000_0000_0000_01FFUL; // preserve unwritten flag + startblock high bits
      hi |= (ulong)holeBlocks << 9;
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(checked((int)inodeOffset + forkOffset), 8), hi);
    }

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var file = session.Lookup(session.RootNodeId, "sparse.bin");
    Assert.That(file, Is.Not.Null);
    using var handle = session.OpenFile(file!.Value, FileAccess.Read);

    var blockSize = XfsDriverGeometry.Parse(stream).BlockSize;
    var probe = new byte[checked((int)blockSize * 3)];
    Assert.That(handle.Read(0, probe), Is.EqualTo(probe.Length));
    Assert.That(probe.AsSpan(0, checked((int)blockSize * holeBlocks)).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(probe.AsSpan(checked((int)blockSize * holeBlocks), checked((int)blockSize)).ToArray(),
      Is.EqualTo(payload.AsSpan(0, checked((int)blockSize)).ToArray()));
  }

  [Test]
  public void UnwrittenInlineExtentReadsAsZeroes() {
    var payload = Enumerable.Range(0, 32 * 1024).Select(i => (byte)(i * 13 + 9)).ToArray();
    var writer = new XfsWriter();
    writer.AddFile("unwritten.bin", payload);
    var image = writer.BuildImageBytes();

    using (var patch = new MemoryStream(image, writable: true)) {
      var geometry = XfsDriverGeometry.Parse(patch);
      using var reader = new XfsReader(patch, leaveOpen: true);
      var entry = reader.Entries.Single(e => e.Name == "unwritten.bin");
      var inodeOffset = InodeOffset(geometry, checked((ulong)entry.InodeNumber));
      var forkOffset = geometry.Version >= 5 ? 176 : 100;
      var hiOffset = checked((int)inodeOffset + forkOffset);
      var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(hiOffset, 8));
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(hiOffset, 8), hi | (1UL << 63));
    }

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var file = session.Lookup(session.RootNodeId, "unwritten.bin");
    Assert.That(file, Is.Not.Null);
    using var handle = session.OpenFile(file!.Value, FileAccess.Read);
    var result = new byte[payload.Length];
    Assert.That(handle.Read(0, result), Is.EqualTo(result.Length));
    Assert.That(result.AsSpan().ContainsAnyExcept((byte)0), Is.False,
      "XFS unwritten extents expose logical zeroes, not undefined backing-block contents");
  }

  [Test]
  public void ReadSpanningExtentBoundaryStitchesAdjacentExtents() {
    var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7 + 3)).ToArray();
    var (image, geometry, inode, extent) = BuildSingleExtentFile("split.bin", payload);
    var blockSize = checked((int)geometry.BlockSize);
    var half = extent.BlockCount / 2;

    // One contiguous run, described by two adjacent records instead of one.
    WriteExtentMap(image, geometry, inode, payload.Length,
      extent with { BlockCount = half },
      new BmbtRecord(half, extent.StartBlock + half, extent.BlockCount - half, false));

    using var handle = OpenSingleFile(image, "split.bin", out _);
    var boundary = checked((int)half * blockSize);
    var window = new byte[blockSize];
    var start = boundary - blockSize / 2;
    Assert.That(handle.Read(start, window), Is.EqualTo(window.Length));
    Assert.That(window, Is.EqualTo(payload.AsSpan(start, window.Length).ToArray()),
      "a read crossing an extent-record boundary must stitch both records seamlessly");
  }

  [Test]
  public void ReadInsideHoleReturnsZeroesWithoutTouchingTheImage() {
    var (image, blockSize, dataBlocks, holeBlocks, _) = BuildHoledFile();
    using var handle = OpenSingleFile(image, "holed.bin", out var counter);

    var probe = new byte[blockSize];
    var before = counter.BytesRead;
    var holeStart = dataBlocks / 2 * blockSize;
    Assert.That(handle.Read(holeStart + blockSize, probe), Is.EqualTo(probe.Length));

    Assert.That(probe.AsSpan().ContainsAnyExcept((byte)0), Is.False,
      "a read wholly inside a sparse hole must be zeroes, not the payload still living in the backing blocks");
    Assert.That(counter.BytesRead - before, Is.Zero,
      "a hole is synthesized, so it must not read the backing device at all");
    Assert.That(holeBlocks, Is.GreaterThan(2));
  }

  [Test]
  public void ReadStraddlingDataAndHoleZeroFillsOnlyTheHole() {
    var (image, blockSize, dataBlocks, holeBlocks, payload) = BuildHoledFile();
    using var handle = OpenSingleFile(image, "holed.bin", out _);

    var splitBlocks = dataBlocks / 2;
    var holeStart = splitBlocks * blockSize;
    var holeEnd = holeStart + holeBlocks * blockSize;

    // data -> hole
    var lead = new byte[blockSize];
    Assert.That(handle.Read(holeStart - blockSize / 2, lead), Is.EqualTo(lead.Length));
    Assert.That(lead.AsSpan(0, blockSize / 2).ToArray(),
      Is.EqualTo(payload.AsSpan(holeStart - blockSize / 2, blockSize / 2).ToArray()));
    Assert.That(lead.AsSpan(blockSize / 2).ContainsAnyExcept((byte)0), Is.False);

    // hole -> data; the tail extent keeps its logical position behind the hole
    var tail = new byte[blockSize];
    Assert.That(handle.Read(holeEnd - blockSize / 2, tail), Is.EqualTo(tail.Length));
    Assert.That(tail.AsSpan(0, blockSize / 2).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(tail.AsSpan(blockSize / 2).ToArray(),
      Is.EqualTo(payload.AsSpan(holeStart, blockSize / 2).ToArray()));
  }

  [Test]
  public void EmptyAndOutOfRangeReadsReturnNothing() {
    var payload = Enumerable.Range(0, 8 * 1024).Select(i => (byte)(i * 5 + 1)).ToArray();
    var writer = new XfsWriter();
    writer.AddFile("edge.bin", payload);
    using var handle = OpenSingleFile(writer.BuildImageBytes(), "edge.bin", out _);

    Assert.That(handle.Length, Is.EqualTo(payload.Length));
    Assert.That(handle.Read(0, Span<byte>.Empty), Is.Zero, "a zero-length read reads nothing");
    Assert.That(handle.Read(payload.Length, new byte[16]), Is.Zero, "a read starting at EOF reads nothing");
    Assert.That(handle.Read(payload.Length + 4096, new byte[16]), Is.Zero, "a read past EOF reads nothing");
    Assert.Throws<ArgumentOutOfRangeException>(() => handle.Read(-1, new byte[16]));

    // A read that starts inside the file but asks for more than remains is clamped.
    var overshoot = new byte[4096];
    var last = payload.Length - 100;
    Assert.That(handle.Read(last, overshoot), Is.EqualTo(100));
    Assert.That(overshoot.AsSpan(0, 100).ToArray(), Is.EqualTo(payload.AsSpan(last, 100).ToArray()));
  }

  [Test]
  public void OverlappingExtentMapIsRefusedRatherThanRead() {
    var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 11 + 2)).ToArray();
    var (image, geometry, inode, extent) = BuildSingleExtentFile("corrupt.bin", payload);
    var half = extent.BlockCount / 2;

    // The second record rewinds behind the end of the first: an impossible map.
    WriteExtentMap(image, geometry, inode, payload.Length,
      extent with { BlockCount = half },
      new BmbtRecord(half - 1, extent.StartBlock + half, extent.BlockCount - half, false));

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    if (profile.CanMount) {
      stream.Position = 0;
      Assert.Throws<InvalidDataException>(() =>
        FormatRegistry.OpenFilesystem("Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true)));
    } else {
      Assert.That(string.Join("; ", profile.Limitations), Does.Contain("overlapping"));
    }
  }

  [Test]
  public void ExtentPointingOutsideTheDataDeviceIsRefused() {
    var payload = Enumerable.Range(0, 32 * 1024).Select(i => (byte)(i * 3 + 8)).ToArray();
    var (image, geometry, inode, extent) = BuildSingleExtentFile("wild.bin", payload);

    WriteExtentMap(image, geometry, inode, payload.Length,
      extent with { StartBlock = geometry.DataBlocks + 1 });

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    if (profile.CanMount) {
      stream.Position = 0;
      Assert.Throws<InvalidDataException>(() =>
        FormatRegistry.OpenFilesystem("Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true)));
    } else {
      Assert.That(string.Join("; ", profile.Limitations), Does.Contain("outside the data device"));
    }
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new XfsWriter();
    writer.AddFile("x", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.BuildImageBytes(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Xfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }

  /// <summary>One on-disk BMBT extent record, in the decoded form the driver works with.</summary>
  private readonly record struct BmbtRecord(ulong StartOffset, ulong StartBlock, ulong BlockCount, bool Unwritten);

  /// <summary>
  /// Writes one contiguous file and hands back its geometry, inode and sole extent record so a
  /// test can rewrite the logical side of the map while the payload stays put on disk. Any hole a
  /// test then introduces is therefore backed by blocks that still hold the payload, which is what
  /// makes "the hole reads as zeroes" a claim a flattening reader would fail.
  /// </summary>
  private static (byte[] Image, XfsDriverGeometry Geometry, ulong Inode, BmbtRecord Extent) BuildSingleExtentFile(
      string name, byte[] payload) {
    var writer = new XfsWriter();
    writer.AddFile(name, payload);
    var image = writer.BuildImageBytes();

    using var probe = new MemoryStream(image, writable: false);
    var geometry = XfsDriverGeometry.Parse(probe);
    probe.Position = 0;
    using var reader = new XfsReader(probe, leaveOpen: true);
    var inode = checked((ulong)reader.Entries.Single(e => e.Name == name).InodeNumber);

    var extents = ReadExtentMap(image, geometry, inode);
    Assert.That(extents, Has.Length.EqualTo(1),
      "these tests assume the writer lays the payload down as one contiguous extent");
    Assert.That(extents[0].StartOffset, Is.Zero);
    Assert.That(extents[0].BlockCount * geometry.BlockSize, Is.EqualTo((ulong)payload.Length));
    return (image, geometry, inode, extents[0]);
  }

  /// <summary>
  /// Splits the contiguous file in half and pushes the second half further out logically, leaving a
  /// hole in between that no extent describes while its would-be blocks still contain the payload.
  /// </summary>
  private static (byte[] Image, int BlockSize, int DataBlocks, int HoleBlocks, byte[] Payload) BuildHoledFile() {
    const int holeBlocks = 4;
    var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 23 + 6)).ToArray();
    var (image, geometry, inode, extent) = BuildSingleExtentFile("holed.bin", payload);
    var dataBlocks = checked((int)extent.BlockCount);
    var split = (ulong)(dataBlocks / 2);

    WriteExtentMap(image, geometry, inode, payload.Length + (long)holeBlocks * geometry.BlockSize,
      extent with { BlockCount = split },
      new BmbtRecord(split + holeBlocks, extent.StartBlock + split, extent.BlockCount - split, false));

    return (image, checked((int)geometry.BlockSize), dataBlocks, holeBlocks, payload);
  }

  private static BmbtRecord[] ReadExtentMap(byte[] image, XfsDriverGeometry geometry, ulong inodeNumber) {
    var (inodeOffset, forkOffset) = ForkLocation(geometry, inodeNumber);
    var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(inodeOffset + 76, 4)));
    var records = new BmbtRecord[count];
    for (var i = 0; i < count; ++i) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(inodeOffset + forkOffset + i * 16, 8));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(inodeOffset + forkOffset + i * 16 + 8, 8));
      records[i] = new BmbtRecord(
        (hi >> 9) & ((1UL << 54) - 1),
        ((hi & 0x1FF) << 43) | (lo >> 21),
        lo & ((1UL << 21) - 1),
        (hi & (1UL << 63)) != 0);
    }
    return records;
  }

  private static void WriteExtentMap(
      byte[] image, XfsDriverGeometry geometry, ulong inodeNumber, long logicalSize, params BmbtRecord[] records) {
    var (inodeOffset, forkOffset) = ForkLocation(geometry, inodeNumber);
    Assert.That(records.Length * 16, Is.LessThanOrEqualTo(geometry.InodeSize - forkOffset),
      "the rewritten map has to stay inline");
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOffset + 56, 8), checked((ulong)logicalSize));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(inodeOffset + 76, 4), checked((uint)records.Length));
    for (var i = 0; i < records.Length; ++i) {
      var (startOffset, startBlock, blockCount, unwritten) = records[i];
      var hi = (unwritten ? 1UL << 63 : 0UL) | (startOffset << 9) | (startBlock >> 43);
      var lo = ((startBlock & ((1UL << 43) - 1)) << 21) | blockCount;
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOffset + forkOffset + i * 16, 8), hi);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(inodeOffset + forkOffset + i * 16 + 8, 8), lo);
    }
  }

  private static (int InodeOffset, int ForkOffset) ForkLocation(XfsDriverGeometry geometry, ulong inodeNumber)
    => (checked((int)InodeOffset(geometry, inodeNumber)), geometry.Version >= 5 ? 176 : 100);

  private static MountedFile OpenSingleFile(byte[] image, string name, out CountingStream counter) {
    var stream = new CountingStream(new MemoryStream(image, writable: false));
    var profile = FormatRegistry.ProbeFilesystem("Xfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));

    stream.Position = 0;
    var session = FormatRegistry.OpenFilesystem(
      "Xfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var file = session.Lookup(session.RootNodeId, name);
    Assert.That(file, Is.Not.Null);
    counter = stream;
    return new MountedFile(stream, session, session.OpenFile(file!.Value, FileAccess.Read));
  }

  /// <summary>Keeps the stream, session and handle alive together for the duration of a test.</summary>
  private sealed class MountedFile(Stream stream, IFilesystemSession session, IFilesystemFileHandle handle) : IDisposable {
    public long Length => handle.Length;
    public int Read(long offset, Span<byte> destination) => handle.Read(offset, destination);
    public void Dispose() {
      handle.Dispose();
      session.Dispose();
      stream.Dispose();
    }
  }

  private static long InodeOffset(XfsDriverGeometry geometry, ulong inodeNumber) {
    var inodesPerBlock = geometry.BlockSize / geometry.InodeSize;
    var inodePerBlockLog = 0;
    for (var value = inodesPerBlock; value > 1; value >>= 1) ++inodePerBlockLog;
    var aginoLog = geometry.AgBlockLog + inodePerBlockLog;
    var agNumber = inodeNumber >> aginoLog;
    var agInode = inodeNumber & ((1UL << aginoLog) - 1);
    var block = agInode / inodesPerBlock;
    var index = agInode % inodesPerBlock;
    return checked((long)((agNumber * geometry.AgBlocks + block) * geometry.BlockSize + index * geometry.InodeSize));
  }

  private sealed class CountingStream(Stream inner) : Stream {
    public long BytesRead { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) {
      var read = inner.Read(buffer, offset, count);
      BytesRead += read;
      return read;
    }
    public override int Read(Span<byte> buffer) {
      var read = inner.Read(buffer);
      BytesRead += read;
      return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    protected override void Dispose(bool disposing) {
      if (disposing) inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
