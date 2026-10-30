#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Spiffs;

/// <summary>One SPIFFS object (a file; SPIFFS has no directories, names carry the path).</summary>
public sealed record SpiffsEntry(string Name, long Size, ushort ObjectId, int HeaderPage, byte Type);

/// <summary>
/// Reads SPIFFS images (the NOR-flash file system of ESP8266/ESP32 firmware).
/// <para>
/// The geometry is not stored; it is found by testing the per-block magic of every
/// candidate in <see cref="SpiffsGeometry.Candidates"/> and, for the header variants
/// the magic cannot tell apart, by which one makes every index entry point at a data
/// page of the right object and span. A live page is written (<c>USED</c> clear),
/// finalized (<c>FINAL</c> clear) and not deleted (<c>DELET</c> set); among several
/// live index pages for one span the last found wins, as in SPIFFS' own scan.
/// </para>
/// </summary>
public sealed class SpiffsReader {
  private readonly byte[] _image;
  private readonly Dictionary<(ushort Id, ushort Span), int> _indexPages = [];
  private readonly List<SpiffsEntry> _entries = [];

  public SpiffsGeometry Geometry { get; }
  public IReadOnlyList<SpiffsEntry> Entries => _entries;

  public SpiffsReader(byte[] image, SpiffsGeometry? geometry = null) {
    ArgumentNullException.ThrowIfNull(image);
    _image = image;
    Geometry = geometry ?? Detect(image) ?? throw new InvalidDataException("SPIFFS: no block carries a SPIFFS magic for any known page/block size.");
    if (!Geometry.Valid || image.Length % Geometry.BlockSize != 0)
      throw new InvalidDataException("SPIFFS: the image length is not a whole number of blocks for this geometry.");
    Scan();
  }

  /// <summary>The geometry whose magic every block carries, with the header variant that explains the index tables best.</summary>
  public static SpiffsGeometry? Detect(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    var byLayout = SpiffsGeometry.Candidates()
      .Where(g => g.MagicMatches(image))
      .GroupBy(g => (g.PageSize, g.BlockSize, g.MagicIncludesLength))
      .OrderBy(g => g.Key.BlockSize)
      .FirstOrDefault();
    if (byLayout == null) return null;
    SpiffsGeometry? best = null;
    var bestScore = -1;
    foreach (var g in byLayout) {
      int score;
      try { score = new SpiffsReader(image, g).Consistency(); } catch (InvalidDataException) { continue; }
      if (score > bestScore) { best = g; bestScore = score; }
    }
    return best;
  }

  private int PageOffset(int page) => page * Geometry.PageSize;
  private bool PageInImage(int page) => page >= 0 && (long)(page + 1) * Geometry.PageSize <= _image.Length;

  private static bool Live(byte flags) =>
    (flags & SpiffsGeometry.FlagUsed) == 0 && (flags & SpiffsGeometry.FlagFinal) == 0 && (flags & SpiffsGeometry.FlagDelete) != 0;

  private void Scan() {
    var g = Geometry;
    var blocks = _image.Length / g.BlockSize;
    var headers = new Dictionary<ushort, int>();
    for (var b = 0; b < blocks; ++b) {
      for (var e = 0; e < g.LookupEntries; ++e) {
        var id = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(b * g.BlockSize + e * 2));
        if (id == SpiffsGeometry.Free || id == SpiffsGeometry.Deleted || (id & SpiffsGeometry.IndexFlag) == 0) continue;
        var page = b * g.PagesPerBlock + g.LookupPages + e;
        var ph = _image.AsSpan(PageOffset(page));
        if (BinaryPrimitives.ReadUInt16LittleEndian(ph) != id) continue;
        var flags = ph[4];
        if (!Live(flags) || (flags & SpiffsGeometry.FlagIndex) != 0) continue;
        var span = BinaryPrimitives.ReadUInt16LittleEndian(ph[2..]);
        if (span == 0 && (flags & SpiffsGeometry.FlagIxDelete) == 0) continue; // header mid-deletion
        _indexPages[((ushort)(id & ~SpiffsGeometry.IndexFlag), span)] = page;
        if (span == 0) headers[(ushort)(id & ~SpiffsGeometry.IndexFlag)] = page;
      }
    }
    foreach (var (id, page) in headers.OrderBy(h => h.Value)) {
      var p = _image.AsSpan(PageOffset(page));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
      var type = p[12];
      var raw = p.Slice(13, SpiffsGeometry.NameLength);
      var nul = raw.IndexOf((byte)0);
      var name = Encoding.UTF8.GetString(nul < 0 ? raw : raw[..nul]).TrimStart('/');
      if (name.Length == 0) continue;
      _entries.Add(new SpiffsEntry(name, size == 0xFFFFFFFF ? 0 : size, id, page, type));
    }
  }

  /// <summary>Index entries that point at a live data page of the right object and span.</summary>
  private int Consistency() {
    var good = 0;
    foreach (var e in _entries) {
      var pages = (int)((e.Size + Geometry.DataPerPage - 1) / Geometry.DataPerPage);
      for (var d = 0; d < pages; ++d)
        if (DataPage(e.ObjectId, d) is not null) ++good;
        else return good - 1_000_000;
    }
    return good;
  }

  private int? DataPage(ushort id, int span) {
    var g = Geometry;
    int indexPage, slot, tableStart;
    if (span < g.HeaderIndexEntries) {
      if (!_indexPages.TryGetValue((id, 0), out indexPage)) return null;
      slot = span;
      tableStart = g.IndexHeaderSize;
    } else {
      var ixSpan = 1 + (span - g.HeaderIndexEntries) / g.IndexEntries;
      if (!_indexPages.TryGetValue((id, (ushort)ixSpan), out indexPage)) return null;
      slot = (span - g.HeaderIndexEntries) % g.IndexEntries;
      tableStart = SpiffsGeometry.IndexPageHeaderSize;
    }
    var page = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(PageOffset(indexPage) + tableStart + slot * 2));
    if (page == SpiffsGeometry.Free || !PageInImage(page) || page % g.PagesPerBlock < g.LookupPages) return null;
    var ph = _image.AsSpan(PageOffset(page));
    if (BinaryPrimitives.ReadUInt16LittleEndian(ph) != id || BinaryPrimitives.ReadUInt16LittleEndian(ph[2..]) != span) return null;
    var flags = ph[4];
    if (!Live(flags) || (flags & SpiffsGeometry.FlagIndex) == 0) return null;
    return page;
  }

  /// <summary>Returns the bytes of <paramref name="entry"/>; a missing or stale data page is refused.</summary>
  public byte[] Extract(SpiffsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var g = Geometry;
    var result = new byte[entry.Size];
    var pages = (int)((entry.Size + g.DataPerPage - 1) / g.DataPerPage);
    for (var d = 0; d < pages; ++d) {
      var page = DataPage(entry.ObjectId, d)
        ?? throw new InvalidDataException($"SPIFFS: data page {d} of '{entry.Name}' is missing or not live.");
      var n = (int)Math.Min(g.DataPerPage, entry.Size - (long)d * g.DataPerPage);
      _image.AsSpan(PageOffset(page) + SpiffsGeometry.PageHeaderSize, n).CopyTo(result.AsSpan(d * g.DataPerPage));
    }
    return result;
  }
}
