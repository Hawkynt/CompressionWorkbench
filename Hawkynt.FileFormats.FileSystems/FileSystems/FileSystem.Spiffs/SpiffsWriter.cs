#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Spiffs;

/// <summary>
/// Builds a freshly formatted SPIFFS image holding the given files, in the state SPIFFS
/// itself leaves after formatting and writing them: every block erased to 0xFF with its
/// erase count (the block index, as <c>SPIFFS_format</c> counts them) and magic in the
/// lookup area; per file one object index header page, the data pages, and further
/// object index pages once the header's table is full, each recorded in its block's
/// object lookup. Object ids are handed out from 1. Names are stored as SPIFFS tools
/// store them, with a leading slash.
/// </summary>
public sealed class SpiffsWriter(SpiffsGeometry geometry, long imageSize) {
  private readonly List<(string Name, byte[] Data)> _files = [];

  public SpiffsGeometry Geometry { get; } = geometry;

  /// <summary>Why <paramref name="name"/> cannot be stored, or null.</summary>
  public static string? Reject(string name) {
    var stored = "/" + name.Replace('\\', '/').TrimStart('/');
    var bytes = Encoding.UTF8.GetByteCount(stored);
    if (stored.Length < 2) return "empty name";
    return bytes > SpiffsGeometry.NameLength - 1 ? $"'{stored}' is {bytes} bytes; SPIFFS names hold at most {SpiffsGeometry.NameLength - 1}" : null;
  }

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (Reject(name) is { } why) throw new ArgumentException("SPIFFS: " + why, nameof(name));
    var stored = "/" + name.Replace('\\', '/').TrimStart('/');
    if (_files.Any(f => f.Name == stored)) throw new ArgumentException($"SPIFFS: duplicate file '{stored}'.", nameof(name));
    _files.Add((stored, data));
  }

  public byte[] Build() {
    var g = Geometry;
    if (!g.Valid) throw new InvalidOperationException("SPIFFS: invalid geometry.");
    if (imageSize <= 0 || imageSize % g.BlockSize != 0 || imageSize > int.MaxValue)
      throw new IOException($"SPIFFS: image size {imageSize} is not a positive multiple of the block size {g.BlockSize}.");
    var blocks = (int)(imageSize / g.BlockSize);
    if (_files.Count >= 0x7FFF) throw new IOException("SPIFFS: too many files.");

    var image = new byte[imageSize];
    image.AsSpan().Fill(0xFF);
    for (var b = 0; b < blocks; ++b) {
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(b * g.BlockSize + g.EraseCountOffsetInBlock), (ushort)b);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(b * g.BlockSize + g.MagicOffsetInBlock), g.Magic(b, blocks));
    }

    // Pages handed out in order, skipping each block's lookup pages.
    var next = 0;
    var capacity = blocks * g.LookupEntries;
    // SPIFFS needs free blocks to garbage-collect into; keep the last one spare as mkspiffs leaves room.
    var usable = capacity - g.LookupEntries;
    int Take(ushort lookupId) {
      if (next >= usable) throw new IOException("SPIFFS: the files do not fit the image.");
      var block = next / g.LookupEntries;
      var entry = next % g.LookupEntries;
      ++next;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(block * g.BlockSize + entry * 2), lookupId);
      return block * g.PagesPerBlock + g.LookupPages + entry;
    }

    ushort id = 0;
    foreach (var (name, data) in _files) {
      ++id;
      var dataPages = (data.Length + g.DataPerPage - 1) / g.DataPerPage;
      var extraIndexPages = dataPages <= g.HeaderIndexEntries ? 0 : (dataPages - g.HeaderIndexEntries + g.IndexEntries - 1) / g.IndexEntries;

      var header = Take((ushort)(id | SpiffsGeometry.IndexFlag));
      var hp = image.AsSpan(header * g.PageSize, g.PageSize);
      WritePageHeader(hp, (ushort)(id | SpiffsGeometry.IndexFlag), 0, IndexFlags);
      hp[5..8].Fill(0xFF);                                              // _align
      BinaryPrimitives.WriteUInt32LittleEndian(hp[8..], (uint)data.Length);
      hp[12] = 1;                                                       // SPIFFS_TYPE_FILE
      hp.Slice(13, SpiffsGeometry.NameLength).Clear();
      Encoding.UTF8.GetBytes(name, hp[13..]);
      // meta stays 0xFF (SPIFFS writes 0xFF when no metadata is given); alignment pad likewise.

      var indexes = new int[extraIndexPages];
      for (var i = 0; i < extraIndexPages; ++i) {
        indexes[i] = Take((ushort)(id | SpiffsGeometry.IndexFlag));
        var ip = image.AsSpan(indexes[i] * g.PageSize, g.PageSize);
        WritePageHeader(ip, (ushort)(id | SpiffsGeometry.IndexFlag), (ushort)(i + 1), IndexFlags);
        ip[5..8].Fill(0xFF);
      }

      for (var d = 0; d < dataPages; ++d) {
        var page = Take(id);
        var dp = image.AsSpan(page * g.PageSize, g.PageSize);
        WritePageHeader(dp, id, (ushort)d, DataFlags);
        var n = Math.Min(g.DataPerPage, data.Length - d * g.DataPerPage);
        data.AsSpan(d * g.DataPerPage, n).CopyTo(dp[SpiffsGeometry.PageHeaderSize..]);
        int tablePage, slotOffset;
        if (d < g.HeaderIndexEntries) {
          tablePage = header;
          slotOffset = g.IndexHeaderSize + d * 2;
        } else {
          var k = d - g.HeaderIndexEntries;
          tablePage = indexes[k / g.IndexEntries];
          slotOffset = SpiffsGeometry.IndexPageHeaderSize + k % g.IndexEntries * 2;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(tablePage * g.PageSize + slotOffset), (ushort)page);
      }
    }
    return image;
  }

  // USED and FINAL cleared; an index page also clears INDEX.
  private const byte DataFlags = 0xFF & ~(SpiffsGeometry.FlagUsed | SpiffsGeometry.FlagFinal);
  private const byte IndexFlags = 0xFF & ~(SpiffsGeometry.FlagUsed | SpiffsGeometry.FlagFinal | SpiffsGeometry.FlagIndex);

  private static void WritePageHeader(Span<byte> page, ushort id, ushort span, byte flags) {
    BinaryPrimitives.WriteUInt16LittleEndian(page, id);
    BinaryPrimitives.WriteUInt16LittleEndian(page[2..], span);
    page[4] = flags;
  }
}
