#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Byte-preserving maintenance for the outer Tahoe-LAFS storage-server share
/// container. The opaque encrypted/erasure-coded share payload is never decoded
/// or rewritten semantically.
/// </summary>
internal static class TahoeLafsMaintenance {

  internal static void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var source = ReadAll(input);
    var layout = TahoeLafsContainer.Parse(source);
    var result = layout.Kind == TahoeLafsShareKind.Mutable
      ? PackMutable(source, layout, preserveLength: false)
      : source;

    output.Position = 0;
    output.SetLength(0);
    output.Write(result);
    output.Position = 0;
  }

  internal static void Defragment(Stream archive, DefragOptions? options = null) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Tahoe-LAFS defrag requires a readable, writable, seekable stream.", nameof(archive));

    options ??= new DefragOptions();
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException($"Tahoe-LAFS share packing supports only {DefragMode.ConsolidateAtStart}.");
    options.CancellationToken.ThrowIfCancellationRequested();

    var source = ReadAll(archive);
    var layout = TahoeLafsContainer.Parse(source);
    if (layout.Kind == TahoeLafsShareKind.Immutable)
      return; // immutable storage shares already have data immediately followed by leases

    var result = PackMutable(source, layout, preserveLength: true);
    options.CancellationToken.ThrowIfCancellationRequested();

    archive.Position = 0;
    archive.Write(result);
    archive.SetLength(result.Length);
    archive.Position = 0;
  }

  internal static IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var source = ReadAll(archive);
    var layout = TahoeLafsContainer.Parse(source);
    var result = new List<DefragBlockInfo>(5);

    if (layout.Kind == TahoeLafsShareKind.Immutable) {
      result.Add(new(0, TahoeLafsContainer.ImmutableHeaderSize, DefragBlockKind.MetadataReserved));
      if (layout.DataLength > 0)
        result.Add(new(layout.DataOffset, layout.DataLength, DefragBlockKind.Used, "share.immutable.bin"));
      if (layout.LeaseOffset < layout.FileLength)
        result.Add(new(layout.LeaseOffset, layout.FileLength - layout.LeaseOffset, DefragBlockKind.MetadataReserved));
      return result;
    }

    result.Add(new(0, TahoeLafsContainer.MutableDataOffset, DefragBlockKind.MetadataReserved));
    if (layout.DataLength > 0)
      result.Add(new(layout.DataOffset, layout.DataLength, DefragBlockKind.Used, "share.mutable.bin"));
    if (layout.LeaseOffset > layout.DataEnd)
      result.Add(new(layout.DataEnd, layout.LeaseOffset - layout.DataEnd, DefragBlockKind.Free));
    if (layout.UsedEnd > layout.LeaseOffset)
      result.Add(new(layout.LeaseOffset, layout.UsedEnd - layout.LeaseOffset, DefragBlockKind.MetadataReserved));
    if (layout.FileLength > layout.UsedEnd)
      result.Add(new(layout.UsedEnd, layout.FileLength - layout.UsedEnd, DefragBlockKind.Free));
    return result;
  }

  private static byte[] PackMutable(byte[] source, TahoeLafsContainerLayout layout, bool preserveLength) {
    var newLeaseOffset = layout.DataEnd;
    var leaseTailLength = layout.UsedEnd - layout.LeaseOffset;
    var compactLength = checked(newLeaseOffset + leaseTailLength);
    var resultLength = preserveLength ? source.LongLength : compactLength;
    if (resultLength > int.MaxValue)
      throw new NotSupportedException("Tahoe-LAFS maintenance currently requires a share container smaller than 2 GiB.");

    var result = new byte[checked((int)resultLength)];
    source.AsSpan(0, checked((int)newLeaseOffset)).CopyTo(result);
    source.AsSpan(checked((int)layout.LeaseOffset), checked((int)leaseTailLength))
      .CopyTo(result.AsSpan(checked((int)newLeaseOffset)));

    BinaryPrimitives.WriteUInt64BigEndian(
      result.AsSpan(TahoeLafsContainer.MutableExtraLeaseOffsetOffset, sizeof(ulong)),
      checked((ulong)newLeaseOffset));
    return result;
  }

  private static byte[] ReadAll(Stream stream) {
    if (!stream.CanRead)
      throw new ArgumentException("Tahoe-LAFS operation requires a readable stream.", nameof(stream));
    if (stream.CanSeek)
      stream.Position = 0;

    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    if (stream.CanSeek)
      stream.Position = 0;
    return buffer.ToArray();
  }
}
