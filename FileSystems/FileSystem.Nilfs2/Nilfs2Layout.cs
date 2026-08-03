#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nilfs2;

/// <summary>
/// Finds the payloads the private directory holds and the eight bytes that say
/// where each one starts.
/// </summary>
/// <remarks>
/// <para>A payload's position is written down as an offset from the start of
/// the segment that describes it. For the base segment that offset is a field
/// in its directory, and moving a payload is a change to that field — provided
/// the payload stays inside the base segment's own area.</para>
///
/// <para>It has to. The reader finds the first appended segment by carrying on
/// from where the base payloads end, and each further one from where the
/// previous segment's payloads end; a payload that reached past a segment
/// header would hide it, and one before its own segment's payload start is a
/// negative offset the format cannot express.</para>
/// </remarks>
internal static class Nilfs2Layout {

  /// <summary>One live payload of the base segment.</summary>
  /// <param name="Name">The file it belongs to.</param>
  /// <param name="Offset">Where its bytes are.</param>
  /// <param name="Size">How many of them there are.</param>
  /// <param name="OffsetField">Where the directory records its position.</param>
  internal readonly record struct BasePayload(string Name, long Offset, long Size, long OffsetField);

  /// <summary>What a volume's base segment is made of.</summary>
  internal sealed class Layout {
    /// <summary>Where the base segment's payloads begin.</summary>
    public long PayloadStart { get; init; }

    /// <summary>Where the area they may occupy ends — the first appended segment, or the image.</summary>
    public long PayloadEnd { get; set; }

    public List<BasePayload> Payloads { get; } = [];
  }

  /// <summary>Walks the base segment, or returns null when this is not one of ours.</summary>
  public static Layout? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var segmentStart = (long)Nilfs2Writer.SegmentStart;
    var head = ReadBytes(image, segmentStart, Nilfs2Writer.WriterMagic.Length + 24);
    if (head == null) return null;
    if (!head.AsSpan(0, Nilfs2Writer.WriterMagic.Length).SequenceEqual(Nilfs2Writer.WriterMagic))
      return null;

    var directorySize = BinaryPrimitives.ReadInt64LittleEndian(
      head.AsSpan(Nilfs2Writer.WriterMagic.Length));
    var payloadBase = BinaryPrimitives.ReadInt64LittleEndian(
      head.AsSpan(Nilfs2Writer.WriterMagic.Length + 8));
    var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(
      head.AsSpan(Nilfs2Writer.WriterMagic.Length + 16));
    if (directorySize < 0 || directorySize > int.MaxValue) return null;
    if (payloadBase <= 0 || payloadBase > image.Length) return null;

    var directoryStart = segmentStart + Nilfs2Writer.WriterMagic.Length + 24;
    var payloadStart = payloadBase;
    var directory = ReadBytes(image, directoryStart, (int)directorySize);
    if (directory == null) return null;

    var layout = new Layout { PayloadStart = payloadStart, PayloadEnd = image.Length };

    var cursor = 0;
    while (cursor + 4 <= directory.Length) {
      var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(cursor));
      cursor += 4;
      if (nameLength <= 0 || cursor + nameLength + 16 > directory.Length) break;

      var name = Encoding.UTF8.GetString(directory, cursor, nameLength);
      cursor += nameLength;

      var offset = BinaryPrimitives.ReadInt64LittleEndian(directory.AsSpan(cursor));
      var offsetField = directoryStart + cursor;
      cursor += 8;
      var size = BinaryPrimitives.ReadInt64LittleEndian(directory.AsSpan(cursor));
      cursor += 8;
      if (size < 0 || offset < 0 || payloadStart + offset + size > image.Length) break;

      layout.Payloads.Add(new BasePayload(name, payloadStart + offset, size, offsetField));
    }

    // The area the base payloads may occupy stops at the first appended
    // segment, because the reader finds that segment by carrying on from where
    // they end.
    var scanFrom = layout.Payloads.Count == 0
      ? payloadStart
      : layout.Payloads.Max(p => p.Offset + p.Size);
    var declared = payloadLength > 0 ? payloadStart + payloadLength : image.Length;
    layout.PayloadEnd = Math.Min(declared,
      FindFirstAppendedSegment(image, scanFrom) ?? image.Length);
    return layout;
  }

  /// <summary>Where the first appended segment's header sits, if there is one.</summary>
  private static long? FindFirstAppendedSegment(Stream image, long from) {
    var magic = Nilfs2Writer.SegmentMagic;
    var window = new byte[magic.Length];

    for (var at = from; at + magic.Length <= image.Length; ++at) {
      image.Position = at;
      image.ReadExactly(window);
      if (window.AsSpan().SequenceEqual(magic)) return at;
    }

    return null;
  }

  private static byte[]? ReadBytes(Stream image, long at, int length) {
    if (at < 0 || length <= 0 || at + length > image.Length) return null;

    var bytes = new byte[length];
    image.Position = at;
    image.ReadExactly(bytes);
    return bytes;
  }
}
