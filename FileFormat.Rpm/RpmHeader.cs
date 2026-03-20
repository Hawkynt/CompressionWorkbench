using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rpm;

/// <summary>
/// Represents a parsed RPM header structure (either the Signature or the main Header section).
/// </summary>
/// <remarks>
/// <para>
/// An RPM header structure begins with a 3-byte magic (0x8E 0xAD 0xE8), a 1-byte version,
/// 4 reserved bytes, a big-endian int32 index entry count, and a big-endian int32 store size.
/// It is followed by the index entries and the store blob.
/// </para>
/// </remarks>
public sealed class RpmHeader {
  private readonly IReadOnlyList<RpmHeaderEntry> _entries;
  private readonly byte[] _store;

  /// <summary>Gets the list of index entries in this header.</summary>
  public IReadOnlyList<RpmHeaderEntry> Entries => this._entries;

  /// <summary>Gets the raw store data referenced by the index entries.</summary>
  public byte[] Store => this._store;

  /// <summary>
  /// Initializes a new <see cref="RpmHeader"/> with the provided entries and store.
  /// </summary>
  /// <param name="entries">The parsed index entries.</param>
  /// <param name="store">The raw store bytes.</param>
  internal RpmHeader(IReadOnlyList<RpmHeaderEntry> entries, byte[] store) {
    this._entries = entries;
    this._store   = store;
  }

  /// <summary>
  /// Reads a string value for the given tag from the store.
  /// Returns <see langword="null"/> if the tag is not present.
  /// </summary>
  /// <param name="tag">The tag number to look up.</param>
  /// <returns>The NUL-terminated string value, or <see langword="null"/> if not found.</returns>
  public string? GetString(int tag) {
    var entry = FindEntry(tag);
    if (entry is null)
      return null;

    if (entry.Type is not RpmConstants.TypeString
                  and not RpmConstants.TypeI18nString
                  and not RpmConstants.TypeStringArray)
      return null;

    return ReadNulTerminatedString(this._store, entry.Offset);
  }

  /// <summary>
  /// Reads an <see cref="int"/> value for the given tag from the store.
  /// Returns <see langword="null"/> if the tag is not present.
  /// </summary>
  /// <param name="tag">The tag number to look up.</param>
  /// <returns>The big-endian int32 value, or <see langword="null"/> if not found.</returns>
  public int? GetInt32(int tag) {
    var entry = FindEntry(tag);
    if (entry is null)
      return null;

    if (entry.Type is not RpmConstants.TypeInt32)
      return null;

    if (entry.Offset + 4 > this._store.Length)
      return null;

    return BinaryPrimitives.ReadInt32BigEndian(this._store.AsSpan(entry.Offset, 4));
  }

  // -------------------------------------------------------------------------
  // Parsing
  // -------------------------------------------------------------------------

  /// <summary>
  /// Parses an RPM header structure from the current position in <paramref name="stream"/>.
  /// </summary>
  /// <param name="stream">The stream positioned at the start of the header magic bytes.</param>
  /// <returns>The parsed <see cref="RpmHeader"/>.</returns>
  /// <exception cref="InvalidDataException">Thrown when the header is malformed.</exception>
  internal static RpmHeader Read(Stream stream) {
    // Preamble: 3 magic + 1 version + 4 reserved + 4 nindex + 4 hsize = 16 bytes
    Span<byte> preamble = stackalloc byte[RpmConstants.HeaderPreambleSize];
    stream.ReadExactly(preamble);

    // Validate magic
    if (preamble[0] != RpmConstants.HeaderMagic[0]
     || preamble[1] != RpmConstants.HeaderMagic[1]
     || preamble[2] != RpmConstants.HeaderMagic[2])
      throw new InvalidDataException(
        $"Invalid RPM header magic: 0x{preamble[0]:X2} 0x{preamble[1]:X2} 0x{preamble[2]:X2}.");

    if (preamble[3] != RpmConstants.HeaderVersion)
      throw new InvalidDataException(
        $"Unsupported RPM header version: {preamble[3]}.");

    int nindex = BinaryPrimitives.ReadInt32BigEndian(preamble[8..]);
    int hsize  = BinaryPrimitives.ReadInt32BigEndian(preamble[12..]);

    if (nindex < 0)
      throw new InvalidDataException($"Invalid RPM header: negative index entry count {nindex}.");
    if (hsize < 0)
      throw new InvalidDataException($"Invalid RPM header: negative store size {hsize}.");

    // Read index entries
    var entries = new List<RpmHeaderEntry>(nindex);
    Span<byte> entryBuf = stackalloc byte[RpmConstants.IndexEntrySize];
    for (int i = 0; i < nindex; ++i) {
      stream.ReadExactly(entryBuf);
      int tag    = BinaryPrimitives.ReadInt32BigEndian(entryBuf);
      int type   = BinaryPrimitives.ReadInt32BigEndian(entryBuf[4..]);
      int offset = BinaryPrimitives.ReadInt32BigEndian(entryBuf[8..]);
      int count  = BinaryPrimitives.ReadInt32BigEndian(entryBuf[12..]);
      entries.Add(new RpmHeaderEntry(tag, type, offset, count));
    }

    // Read store
    byte[] store = new byte[hsize];
    if (hsize > 0)
      stream.ReadExactly(store);

    return new RpmHeader(entries, store);
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private RpmHeaderEntry? FindEntry(int tag) {
    foreach (var e in this._entries) {
      if (e.Tag == tag)
        return e;
    }
    return null;
  }

  private static string ReadNulTerminatedString(byte[] store, int offset) {
    if (offset < 0 || offset >= store.Length)
      return string.Empty;

    int end = offset;
    while (end < store.Length && store[end] != 0)
      ++end;

    return Encoding.UTF8.GetString(store, offset, end - offset);
  }
}
