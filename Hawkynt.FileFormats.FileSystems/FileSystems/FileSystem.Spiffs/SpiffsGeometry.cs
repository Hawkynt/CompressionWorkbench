#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Spiffs;

/// <summary>
/// The build-time parameters a SPIFFS image depends on but does not record:
/// logical page and block size, the length of the per-object metadata field, whether
/// object index tables are 2-byte aligned, and whether the per-block magic folds in the
/// block's distance from the end (<c>SPIFFS_USE_MAGIC_LENGTH</c>).
/// <para>
/// Derived layout, as in SPIFFS' <c>spiffs_nucleus.h</c> (MIT, Peter Andersson,
/// revision f5e26c4): every block starts with <c>max(1, pagesPerBlock·2 / pageSize)</c>
/// object-lookup pages holding one 16-bit object id per remaining page of the block
/// (0xFFFF free, 0 deleted, bit 15 set for index pages). The last two 16-bit slots of
/// the lookup area hold the magic and the erase count. A page starts with a 5-byte
/// header (<c>obj_id</c>, <c>span_ix</c>, <c>flags</c>), padded to 8 in index pages.
/// The object index header page adds <c>size</c> (u32), <c>type</c> (u8),
/// <c>name[32]</c> and <c>meta[metaLength]</c>, after which the 16-bit page indexes
/// of the first data pages follow.
/// </para>
/// </summary>
public sealed record SpiffsGeometry(int PageSize, int BlockSize, int MetaLength, bool AlignedIndexTables, bool MagicIncludesLength) {
  public const int NameLength = 32;
  public const int PageHeaderSize = 5;
  public const int IndexPageHeaderSize = 8;
  public const ushort IndexFlag = 0x8000;
  public const ushort Free = 0xFFFF;
  public const ushort Deleted = 0;
  public const byte FlagUsed = 1 << 0;      // 0 = written
  public const byte FlagFinal = 1 << 1;     // 0 = finalized
  public const byte FlagIndex = 1 << 2;     // 0 = index page
  public const byte FlagIxDelete = 1 << 6;  // 0 = index header being deleted
  public const byte FlagDelete = 1 << 7;    // 0 = deleted

  public int PagesPerBlock => BlockSize / PageSize;
  public int LookupPages => Math.Max(1, PagesPerBlock * 2 / PageSize);
  public int LookupEntries => PagesPerBlock - LookupPages;
  public int DataPerPage => PageSize - PageHeaderSize;

  /// <summary><c>sizeof(spiffs_page_object_ix_header)</c>: 8 + 4 + 1 + 32 + meta, rounded to 2 when aligned.</summary>
  public int IndexHeaderSize {
    get {
      var raw = IndexPageHeaderSize + 4 + 1 + NameLength + MetaLength;
      return AlignedIndexTables ? (raw + 1) & ~1 : raw;
    }
  }

  public int HeaderIndexEntries => (PageSize - IndexHeaderSize) / 2;
  public int IndexEntries => (PageSize - IndexPageHeaderSize) / 2;

  public int MagicOffsetInBlock => LookupPages * PageSize - 4;
  public int EraseCountOffsetInBlock => LookupPages * PageSize - 2;

  public ushort Magic(int blockIndex, int blockCount)
    => (ushort)(0x20140529 ^ PageSize ^ (MagicIncludesLength ? blockCount - blockIndex : 0));

  /// <summary>Whether the lookup area leaves room for the magic and erase count (<c>SPIFFS_CHECK_MAGIC_POSSIBLE</c>).</summary>
  public bool MagicPossible
    => LookupEntries % (PageSize / 2) * 2 <= PageSize - 4;

  public bool Valid
    => PageSize is >= 64 and <= 8192 && (PageSize & (PageSize - 1)) == 0
       && BlockSize >= PageSize * 4 && BlockSize % PageSize == 0
       && MetaLength is >= 0 and <= 64 && HeaderIndexEntries > 0 && MagicPossible;

  /// <summary>
  /// The page sizes, block sizes and header variants tried when an image does not say;
  /// they cover mkspiffs' Arduino ESP8266/ESP32 builds and ESP-IDF's spiffsgen.py defaults.
  /// </summary>
  public static IEnumerable<SpiffsGeometry> Candidates() {
    foreach (var page in new[] { 256, 512, 128, 1024 })
      foreach (var block in new[] { 4096, 8192, 16384, 32768, 65536 })
        foreach (var length in new[] { true, false })
          foreach (var (meta, aligned) in new[] { (0, false), (0, true), (4, false), (4, true) }) {
            var g = new SpiffsGeometry(page, block, meta, aligned, length);
            if (g.Valid) yield return g;
          }
  }

  /// <summary>Whether every block of <paramref name="image"/> carries this geometry's magic.</summary>
  public bool MagicMatches(ReadOnlySpan<byte> image) {
    if (image.Length < BlockSize || image.Length % BlockSize != 0) return false;
    var blocks = image.Length / BlockSize;
    for (var b = 0; b < blocks; ++b) {
      var at = b * BlockSize + MagicOffsetInBlock;
      if (BinaryPrimitives.ReadUInt16LittleEndian(image[at..]) != Magic(b, blocks)) return false;
    }
    return true;
  }
}
