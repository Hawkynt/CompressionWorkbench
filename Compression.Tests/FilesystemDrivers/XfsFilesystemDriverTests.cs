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
  public void WritableMountStaysFailClosed() {
    var writer = new XfsWriter();
    writer.AddFile("x", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.BuildImageBytes(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Xfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
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
