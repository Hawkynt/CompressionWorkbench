using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Compression.Registry;

/// <summary>
/// APEv1/APEv2 metadata tags, shared by every format that carries them.
/// </summary>
/// <remarks>
/// <para>
/// The tag is a run of items bracketed by 32-byte descriptors. A footer follows
/// the items unless the no-footer flag is set, and a header precedes them when
/// the has-header flag is set; both descriptors carry the same magic, version,
/// size and item count, and only bit 29 of the flags says which one you are
/// looking at.
/// </para>
/// <para>
/// The size field counts the items plus the footer and never the header, so a
/// tag written with a header occupies 32 bytes more than it declares. Getting
/// that wrong is what makes a tagged file look like it has trailing garbage.
/// </para>
/// <para>
/// This is the native tagging format of Monkey's Audio and of WavPack — not an
/// oddity to tolerate but the normal case — and an optional trailer on MP3.
/// </para>
/// </remarks>
public static class ApeTagReader {

  /// <summary>Size of the header, and of the footer, each.</summary>
  public const int DescriptorSize = 32;

  /// <summary>Size of an ID3v1 tag, the other trailer these formats may carry.</summary>
  public const int Id3v1Size = 128;

  private const uint FlagHasHeader = 0x8000_0000;
  private const uint FlagIsHeader = 0x2000_0000;
  private const uint MaxItemCount = 65_535;

  /// <summary>Where a tag sits in a buffer, and which bytes hold its items.</summary>
  /// <param name="Start">Offset of the first byte of the tag — the header when there is one.</param>
  /// <param name="Length">Total bytes occupied, descriptors included.</param>
  /// <param name="ItemsStart">Offset of the first item.</param>
  /// <param name="ItemsEnd">One past the last item byte.</param>
  /// <param name="ItemCount">Item count as declared by the descriptor.</param>
  public readonly record struct ApeTag(
    long Start,
    long Length,
    long ItemsStart,
    long ItemsEnd,
    uint ItemCount) {

    /// <summary>One past the last byte of the tag.</summary>
    public long End => this.Start + this.Length;
  }

  /// <summary>
  /// Reads the tag whose <em>header</em> begins at <paramref name="offset" />.
  /// </summary>
  /// <remarks>
  /// This is the case a forward scan runs into: the reader has consumed
  /// everything it understands and the next bytes are a tag rather than more
  /// payload. A tag written without a header cannot be found this way — its
  /// first bytes are items, which carry no magic — so use
  /// <see cref="TryReadEndingAt" /> for that.
  /// </remarks>
  public static bool TryReadHeaderAt(byte[] file, long offset, out ApeTag tag) {
    ArgumentNullException.ThrowIfNull(file);
    tag = default;
    if (!TryReadDescriptor(file, offset, out var size, out var itemCount, out var flags))
      return false;
    if ((flags & FlagIsHeader) == 0)
      return false;

    // size counts items + footer, so the items run from just past the header to
    // offset + size, where the footer begins.
    var length = (long)size + DescriptorSize;
    if (offset + length > file.Length)
      return false;

    tag = new ApeTag(offset, length, offset + DescriptorSize, offset + size, itemCount);
    return true;
  }

  /// <summary>
  /// Reads the tag whose footer ends at <paramref name="end" />, refusing to
  /// reach back before <paramref name="lowerBound" />.
  /// </summary>
  public static bool TryReadEndingAt(byte[] file, long end, long lowerBound, out ApeTag tag) {
    ArgumentNullException.ThrowIfNull(file);
    tag = default;
    var footer = end - DescriptorSize;
    if (footer < lowerBound)
      return false;
    if (!TryReadDescriptor(file, footer, out var size, out var itemCount, out var flags))
      return false;
    if ((flags & FlagIsHeader) != 0)
      return false;

    var itemsStart = footer + DescriptorSize - size;
    var start = (flags & FlagHasHeader) != 0 ? itemsStart - DescriptorSize : itemsStart;
    if (start < lowerBound)
      return false;

    tag = new ApeTag(start, end - start, itemsStart, footer, itemCount);
    return true;
  }

  /// <summary>
  /// Searches <paramref name="start" />..<paramref name="end" /> for a tag,
  /// scanning back from the end so the footer is met before anything earlier
  /// that happens to look like one.
  /// </summary>
  public static bool TryFind(byte[] file, long start, long end, out ApeTag tag) {
    ArgumentNullException.ThrowIfNull(file);
    tag = default;
    var clampedEnd = Math.Min(end, file.Length);
    if (clampedEnd - start < DescriptorSize)
      return false;

    for (var position = clampedEnd - DescriptorSize; position >= start; --position) {
      if (!TryReadDescriptor(file, position, out var size, out var itemCount, out var flags))
        continue;

      // A footer describes the run that precedes it; a header, the run that follows.
      if ((flags & FlagIsHeader) != 0) {
        if (TryReadHeaderAt(file, position, out tag))
          return true;
        continue;
      }

      var itemsStart = position + DescriptorSize - size;
      var tagStart = (flags & FlagHasHeader) != 0 ? itemsStart - DescriptorSize : itemsStart;
      if (tagStart < start)
        continue;

      tag = new ApeTag(tagStart, position + DescriptorSize - tagStart, itemsStart, position, itemCount);
      return true;
    }

    return false;
  }

  /// <summary>
  /// True when everything from <paramref name="offset" /> to the end of
  /// <paramref name="file" /> is accounted for by an APE tag, an ID3v1 tag, or
  /// both.
  /// </summary>
  /// <remarks>
  /// The whole remainder must be explained. A reader that merely stopped at the
  /// first thing it recognised would swallow real corruption sitting behind a
  /// tag, so a partial match is reported as no match at all.
  /// </remarks>
  public static bool IsTrailingMetadata(byte[] file, long offset) {
    ArgumentNullException.ThrowIfNull(file);
    var end = (long)file.Length;
    if (offset > end)
      return false;

    // ID3v1 is always last when present.
    if (end - offset >= Id3v1Size && file.AsSpan((int)(end - Id3v1Size), 3).SequenceEqual("TAG"u8))
      end -= Id3v1Size;
    if (end == offset)
      return true;

    if (TryReadHeaderAt(file, offset, out var tag) && tag.End == end)
      return true;

    return TryReadEndingAt(file, end, offset, out tag) && tag.Start == offset;
  }

  /// <summary>
  /// Renders the text items of <paramref name="tag" /> as an ini, naming binary
  /// items without inlining them. Null when nothing could be decoded.
  /// </summary>
  public static string? TryRenderIni(byte[] file, in ApeTag tag) {
    ArgumentNullException.ThrowIfNull(file);
    var builder = new StringBuilder();
    builder.AppendLine("; APEv2 tags");

    var rendered = 0;
    var position = (int)tag.ItemsStart;
    var end = (int)Math.Min(tag.ItemsEnd, file.Length);
    for (var i = 0; i < tag.ItemCount; ++i) {
      if (position + 8 > end)
        break;
      var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4));
      position += 8;

      // The key is a null-terminated ASCII string.
      var keyStart = position;
      while (position < end && file[position] != 0)
        ++position;
      if (position >= end)
        break;
      var key = Encoding.ASCII.GetString(file, keyStart, position - keyStart);
      ++position;

      if (valueLength < 0 || position + valueLength > end)
        break;

      // Bits 1-2 of the item flags hold the value type; 0 is UTF-8 text.
      if (((flags >> 1) & 0x03) == 0) {
        var value = Encoding.UTF8.GetString(file, position, valueLength).Replace("\0", "; ");
        builder.Append(key).Append('=').AppendLine(value);
      } else
        builder
          .Append("; ")
          .Append(key)
          .Append(" (binary, ")
          .Append(valueLength.ToString(CultureInfo.InvariantCulture))
          .AppendLine(" bytes)");

      ++rendered;
      position += valueLength;
    }

    return rendered > 0 ? builder.ToString() : null;
  }

  private static bool TryReadDescriptor(
      byte[] file, long offset, out uint size, out uint itemCount, out uint flags) {
    size = itemCount = flags = 0;
    if (offset < 0 || offset + DescriptorSize > file.Length)
      return false;
    if (!file.AsSpan((int)offset, 8).SequenceEqual("APETAGEX"u8))
      return false;

    size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offset + 12));
    itemCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offset + 16));
    flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offset + 20));
    return size >= DescriptorSize && itemCount is > 0 and <= MaxItemCount;
  }
}
