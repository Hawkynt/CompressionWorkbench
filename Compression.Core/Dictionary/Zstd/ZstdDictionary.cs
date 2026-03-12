using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Zstd;

/// <summary>
/// Represents a parsed Zstandard dictionary (RFC 8878 Section 5) that can be used
/// to initialize decompression/compression state for dictionary-compressed frames.
/// </summary>
public sealed class ZstdDictionary {
  /// <summary>Magic number identifying a Zstd dictionary (0xEC30A437).</summary>
  public const uint DictionaryMagic = 0xEC30A437;

  /// <summary>Minimum size of a valid dictionary (magic + dictionary ID).</summary>
  private const int MinHeaderSize = 8;

  /// <summary>Number of repeat offsets used by Zstd.</summary>
  private const int RepeatOffsetCount = 3;

  /// <summary>Default repeat offsets per the Zstd specification.</summary>
  private static readonly int[] DefaultRepeatOffsets = [1, 4, 8];

  /// <summary>Gets the dictionary identifier.</summary>
  public uint DictionaryId { get; }

  /// <summary>Gets the dictionary content that prepopulates the sliding window.</summary>
  public byte[] Content { get; }

  /// <summary>
  /// Gets the raw entropy tables and content payload (everything after the 8-byte header).
  /// When entropy table parsing is wired into the decompressor, this provides the
  /// complete payload for on-demand decoding of Huffman and FSE tables.
  /// </summary>
  public byte[] RawPayload { get; }

  /// <summary>
  /// Gets the initial repeat offsets derived from the dictionary content.
  /// Per the specification, these are initialized from the last bytes of the content.
  /// </summary>
  public int[] RepeatOffsets { get; }

  private ZstdDictionary(uint dictionaryId, byte[] content, byte[] rawPayload, int[] repeatOffsets) {
    this.DictionaryId = dictionaryId;
    this.Content = content;
    this.RawPayload = rawPayload;
    this.RepeatOffsets = repeatOffsets;
  }

  /// <summary>
  /// Parses a Zstd dictionary from raw bytes.
  /// </summary>
  /// <param name="data">The raw dictionary bytes including the 8-byte header.</param>
  /// <returns>A parsed <see cref="ZstdDictionary"/> instance.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when the data is too short or the magic number is invalid.
  /// </exception>
  public static ZstdDictionary Parse(ReadOnlySpan<byte> data) {
    if (data.Length < ZstdDictionary.MinHeaderSize)
      ThrowTooShort();

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (magic != ZstdDictionary.DictionaryMagic)
      ThrowInvalidMagic(magic);

    var dictId = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

    // The payload after the 8-byte header contains entropy tables followed by content.
    // Full entropy table parsing requires FSE/Huffman decoding infrastructure and is
    // deferred to integration with the decompressor. For now, store the raw payload
    // and treat the entire payload as content for window prepopulation.
    var rawPayload = data[ZstdDictionary.MinHeaderSize..].ToArray();
    var content = rawPayload;
    var repeatOffsets = DeriveRepeatOffsets(content);

    return new(dictId, content, rawPayload, repeatOffsets);
  }

  /// <summary>
  /// Creates a raw (content-only) dictionary without entropy tables.
  /// This is used when the dictionary is just history data to prepopulate the window.
  /// </summary>
  /// <param name="dictionaryId">The dictionary identifier.</param>
  /// <param name="content">The content bytes for window prepopulation.</param>
  /// <returns>A <see cref="ZstdDictionary"/> with the given content and default repeat offsets.</returns>
  public static ZstdDictionary CreateRaw(uint dictionaryId, byte[] content) {
    ArgumentNullException.ThrowIfNull(content);
    var repeatOffsets = DeriveRepeatOffsets(content);
    return new(dictionaryId, content, content, repeatOffsets);
  }

  /// <summary>
  /// Serializes this dictionary to the standard Zstd dictionary format.
  /// </summary>
  /// <returns>The serialized dictionary bytes including the 8-byte header.</returns>
  public byte[] ToBytes() {
    var result = new byte[ZstdDictionary.MinHeaderSize + this.RawPayload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), ZstdDictionary.DictionaryMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), this.DictionaryId);
    this.RawPayload.AsSpan().CopyTo(result.AsSpan(ZstdDictionary.MinHeaderSize));
    return result;
  }

  /// <summary>
  /// Returns the content bytes suitable for prepopulating a sliding window.
  /// </summary>
  /// <returns>A read-only span over the dictionary content.</returns>
  public ReadOnlySpan<byte> GetContentForWindow() => this.Content;

  /// <summary>
  /// Derives repeat offsets from the dictionary content.
  /// Per RFC 8878, the repeat offsets are initialized from the last bytes of the content.
  /// If the content is too short, default offsets [1, 4, 8] are used.
  /// </summary>
  private static int[] DeriveRepeatOffsets(byte[] content) {
    // Per the Zstd specification, repeat offsets are derived from the dictionary content.
    // The last 8 bytes of content provide offset values (as little-endian uint32 pairs).
    // If content is insufficient, use the default offsets.
    if (content.Length < 8)
      return [.. ZstdDictionary.DefaultRepeatOffsets];

    var span = content.AsSpan();
    var offsets = new int[ZstdDictionary.RepeatOffsetCount];

    // Read the last 8 bytes as two 4-byte little-endian values.
    // Offset 1 is derived from the last 4 bytes, offset 2 from the preceding 4 bytes.
    // Offset 3 defaults to 8 if not enough data.
    var end = content.Length;
    offsets[0] = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(end - 4)..]);
    offsets[1] = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(end - 8)..]);

    // Validate offsets: must be > 0; fall back to defaults if invalid
    if (offsets[0] <= 0) offsets[0] = ZstdDictionary.DefaultRepeatOffsets[0];
    if (offsets[1] <= 0) offsets[1] = ZstdDictionary.DefaultRepeatOffsets[1];

    // Third offset: read from bytes -12..-8 if available, else default
    if (content.Length >= 12) {
      offsets[2] = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(end - 12)..]);
      if (offsets[2] <= 0) offsets[2] = ZstdDictionary.DefaultRepeatOffsets[2];
    } else
      offsets[2] = ZstdDictionary.DefaultRepeatOffsets[2];

    return offsets;
  }

  [DoesNotReturn]
  [MethodImpl(MethodImplOptions.NoInlining)]
  [StackTraceHidden]
  private static void ThrowTooShort() => throw new InvalidDataException("Zstd dictionary too short.");

  [DoesNotReturn]
  [MethodImpl(MethodImplOptions.NoInlining)]
  [StackTraceHidden]
  private static void ThrowInvalidMagic(uint magic) => throw new InvalidDataException($"Invalid Zstd dictionary magic: 0x{magic:X8}.");

}
