using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.UnityBundle;

namespace Compression.Tests.UnityBundle;

[TestFixture]
public sealed class UnityFsTailEditTests {
  [Test]
  public void Add_AppendsBeforeBlocksInfo_WithoutReadingUntouchedStorageBlocks() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x554E4954).NextBytes(large);
    var descriptor = new UnityBundleFormatDescriptor();
    using var archive = Expandable(BuildTailBundle([
      ArchiveInputInfo.InMemory("large.bin", large),
      ArchiveInputInfo.InMemory("small.txt", "small"u8.ToArray()),
    ]));
    var beforeBytes = archive.ToArray();
    var beforeLayout = ReadLayout(beforeBytes);
    var oldStorage = beforeBytes.AsSpan(beforeLayout.DataOffset, beforeLayout.BlocksInfoOffset - beforeLayout.DataOffset).ToArray();

    using var counted = new CountingStream(archive);
    descriptor.Add(counted, [ArchiveInputInfo.InMemory("new.txt", "new payload"u8.ToArray())]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "BlocksInfoAtEnd add should read only the header/trailer metadata, not 4 MiB of existing storage blocks.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "BlocksInfoAtEnd add should append the new Stored block and replacement BlocksInfo only.");

    var afterBytes = archive.ToArray();
    var afterLayout = ReadLayout(afterBytes);
    Assert.That(afterBytes.AsSpan(afterLayout.DataOffset, oldStorage.Length).ToArray(), Is.EqualTo(oldStorage),
      "All pre-existing compressed storage blocks must remain byte-identical at their original positions.");

    var reader = new UnityBundleReader(afterBytes);
    Assert.That(reader.Nodes.Select(node => node.Path), Does.Contain("new.txt"));
    Assert.That(reader.ExtractNode(reader.Nodes.Single(node => node.Path == "large.bin")), Is.EqualTo(large));
    Assert.That(reader.ExtractNode(reader.Nodes.Single(node => node.Path == "new.txt")), Is.EqualTo("new payload"u8.ToArray()));
  }

  [Test]
  public void Remove_FinalWholeBlock_RewritesOnlyTrailer() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x554E524D).NextBytes(large);
    var descriptor = new UnityBundleFormatDescriptor();
    using var archive = Expandable(BuildTailBundle([
      ArchiveInputInfo.InMemory("large.bin", large),
      ArchiveInputInfo.InMemory("remove.txt", "delete me"u8.ToArray()),
    ]));
    var beforeBytes = archive.ToArray();
    var beforeReader = new UnityBundleReader(beforeBytes);
    Assert.That(beforeReader.Blocks[^1].UncompressedSize, Is.EqualTo((uint)"delete me"u8.Length),
      "fixture requires the tiny removed node to occupy its own final storage block");

    var beforeLayout = ReadLayout(beforeBytes);
    var keptStorageLength = beforeLayout.BlocksInfoOffset - beforeLayout.DataOffset - checked((int)beforeReader.Blocks[^1].CompressedSize);
    var keptStorage = beforeBytes.AsSpan(beforeLayout.DataOffset, keptStorageLength).ToArray();

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["remove.txt"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024),
      "Removing a final whole storage block should inspect only BlocksInfo metadata.");
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024),
      "The final removed block is discarded by truncation; only replacement BlocksInfo/header bytes need writing.");

    var afterBytes = archive.ToArray();
    var afterLayout = ReadLayout(afterBytes);
    Assert.That(afterBytes.AsSpan(afterLayout.DataOffset, keptStorage.Length).ToArray(), Is.EqualTo(keptStorage));
    var reader = new UnityBundleReader(afterBytes);
    Assert.That(reader.Nodes.Select(node => node.Path), Is.EqualTo(new[] { "large.bin" }));
    Assert.That(reader.ExtractNode(reader.Nodes.Single()), Is.EqualTo(large));
  }

  [Test]
  public void Remove_ZeroLengthNode_IsMetadataOnly() {
    var large = new byte[4 * 1024 * 1024];
    new Random(0x554E5A45).NextBytes(large);
    var descriptor = new UnityBundleFormatDescriptor();
    using var archive = Expandable(BuildTailBundle([
      ArchiveInputInfo.InMemory("large.bin", large),
      ArchiveInputInfo.InMemory("zero.txt", []),
    ]));

    using var counted = new CountingStream(archive);
    descriptor.Remove(counted, ["zero.txt"]);

    Assert.That(counted.BytesRead, Is.LessThan(128 * 1024));
    Assert.That(counted.BytesWritten, Is.LessThan(128 * 1024));
    var reader = new UnityBundleReader(archive.ToArray());
    Assert.That(reader.Nodes.Select(node => node.Path), Is.EqualTo(new[] { "large.bin" }));
    Assert.That(reader.ExtractNode(reader.Nodes.Single()), Is.EqualTo(large));
  }

  [Test]
  public void Remove_NodeSharingFinalBlockWithSurvivor_FallsBackAndRemainsCorrect() {
    var descriptor = new UnityBundleFormatDescriptor();
    var bundle = BuildTailBundle([
      ArchiveInputInfo.InMemory("a.txt", new byte[100]),
      ArchiveInputInfo.InMemory("b.txt", new byte[100]),
    ], blockSize: 4096);
    using var archive = Expandable(bundle);

    descriptor.Remove(archive, ["b.txt"]);

    var reader = new UnityBundleReader(archive.ToArray());
    Assert.That(reader.Nodes.Select(node => node.Path), Is.EqualTo(new[] { "a.txt" }));
    Assert.That(reader.ExtractNode(reader.Nodes.Single()), Is.EqualTo(new byte[100]));
  }

  private static byte[] BuildTailBundle(IReadOnlyList<ArchiveInputInfo> inputs, int blockSize = 128 * 1024) {
    using var output = new MemoryStream();
    var descriptor = new UnityBundleFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions {
      MethodName = "stored",
      FormatSpecific = FormatCreateOptions.FormatSpecificFrom(new Dictionary<string, string> {
        ["FormatVersion"] = "7",
        ["BlockSize"] = blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["BlocksInfoCompression"] = "lz4hc",
        ["BlocksInfoAtEnd"] = "true",
      }),
    });
    return output.ToArray();
  }

  private static MemoryStream Expandable(byte[] bytes) {
    var result = new MemoryStream(bytes.Length + 256 * 1024);
    result.Write(bytes);
    result.Position = 0;
    return result;
  }

  private static (int DataOffset, int BlocksInfoOffset) ReadLayout(byte[] bundle) {
    var pos = 0;
    SkipCString(bundle, ref pos);
    var version = checked((int)ReadUInt32BE(bundle, ref pos));
    SkipCString(bundle, ref pos);
    SkipCString(bundle, ref pos);
    _ = ReadInt64BE(bundle, ref pos);
    var compressedInfoSize = checked((int)ReadUInt32BE(bundle, ref pos));
    _ = ReadUInt32BE(bundle, ref pos);
    var flags = ReadUInt32BE(bundle, ref pos);
    if (version >= 7)
      pos = (pos + 15) & ~15;
    if ((flags & 0x200) != 0)
      pos = (pos + 15) & ~15;
    return (pos, bundle.Length - compressedInfoSize);
  }

  private static void SkipCString(byte[] data, ref int pos) {
    while (pos < data.Length && data[pos++] != 0) { }
  }

  private static uint ReadUInt32BE(byte[] data, ref int pos) {
    var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
    pos += 4;
    return value;
  }

  private static long ReadInt64BE(byte[] data, ref int pos) {
    var value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos, 8));
    pos += 8;
    return value;
  }

  private sealed class CountingStream(Stream inner) : Stream {
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) {
      var read = inner.Read(buffer, offset, count);
      this.BytesRead += read;
      return read;
    }

    public override int Read(Span<byte> buffer) {
      var read = inner.Read(buffer);
      this.BytesRead += read;
      return read;
    }

    public override int ReadByte() {
      var value = inner.ReadByte();
      if (value >= 0) ++this.BytesRead;
      return value;
    }

    public override void Write(byte[] buffer, int offset, int count) {
      inner.Write(buffer, offset, count);
      this.BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      inner.Write(buffer);
      this.BytesWritten += buffer.Length;
    }
  }
}
