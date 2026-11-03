using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.WiseInstaller;

/// <summary>One file a Wise installer carries.</summary>
/// <param name="Path">Name, '/'-separated (see <c>docs/WISE-ON-DISK.md</c>).</param>
/// <param name="Offset">Offset of its stream in the installer.</param>
/// <param name="StoredSize">Stored size of the stream: DEFLATE data, pad byte if any, CRC-32.</param>
/// <param name="Size">Inflated size.</param>
/// <param name="Crc">CRC-32 of the inflated bytes.</param>
/// <param name="Modified">Date the script records for an installed file, when it has one.</param>
public sealed record WiseEntry(string Path, long Offset, long StoredSize, long Size, uint Crc, DateTime? Modified);

/// <summary>
/// Wise Installation System setup executables (versions 5 to 9): a stub program followed by an
/// overlay header and DEFLATE streams, each closed by the CRC-32 of its inflated bytes; the
/// compiled script says where each installed file's stream starts and ends.
/// </summary>
/// <remarks>
/// Implemented from <c>docs/WISE-ON-DISK.md</c>. Every stream is inflated and checked against
/// its CRC-32 when the installer is opened, so a listed file always extracts; a record whose
/// stream does not inflate to the size and CRC it gives is not taken for one.
/// </remarks>
public sealed class WiseReader {
  /// <summary>How far into the file the overlay header is looked for.</summary>
  public const int HeaderSearchLimit = 512 * 1024;

  /// <summary>Stubs end on a 16-byte boundary (PE file alignment, NE segment alignment), so the header starts on one.</summary>
  private const int HeaderAlignment = 16;

  private const uint FlagPkZip = 0x100;
  private const int MaxNameLength = 1024;

  private static readonly string[] SlotNames = [
    "WISE0001.DLL", "CTL3D32.DLL", "FILE0004", "Ocxreg32.EXE", "PROGRESS.DLL",
    "FILE0007", "FILE0008", "FILE0009", "FILE000A",
  ];

  private static readonly Encoding Ansi =
    CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

  private readonly byte[] _data;

  private WiseReader(byte[] data, long headerOffset, long dataBase, List<WiseEntry> entries, int unavailable) {
    this._data = data;
    this.HeaderOffset = headerOffset;
    this.DataBase = dataBase;
    this.Entries = entries;
    this.UnavailableRecords = unavailable;
  }

  /// <summary>Offset of the overlay header in the file.</summary>
  public long HeaderOffset { get; }

  /// <summary>Offset the script's stream offsets count from.</summary>
  public long DataBase { get; }

  /// <summary>The header streams, installed files, dialog sets, billboards and other streams.</summary>
  public IReadOnlyList<WiseEntry> Entries { get; }

  /// <summary>Install records whose stream lies beyond the end of this file (later volumes).</summary>
  public int UnavailableRecords { get; }

  /// <summary>Whether the stream holds a Wise overlay header within its first 512 KiB.</summary>
  public static bool IsWise(Stream stream) {
    try {
      stream.Position = 0;
      var length = (int)Math.Min(stream.Length, HeaderSearchLimit + 2L * 1024 * 1024);
      var head = new byte[length];
      stream.ReadExactly(head);
      return FindHeader(head) is not null;
    } catch (Exception e) when (e is IOException or EndOfStreamException or NotSupportedException) {
      return false;
    }
  }

  /// <summary>Opens a Wise installer held in memory.</summary>
  /// <exception cref="InvalidDataException">No overlay header was found, or a stream it needs is broken.</exception>
  /// <exception cref="NotSupportedException">The installer wraps its streams in PKZIP headers.</exception>
  public static WiseReader Open(byte[] data) {
    var header = FindHeader(data) ?? throw new InvalidDataException("no Wise overlay header in the first 512 KiB");
    if ((header.Flags & FlagPkZip) != 0)
      throw new NotSupportedException("the installer wraps its streams in PKZIP headers, which are not read");

    var entries = new List<WiseEntry>();
    var p = header.DataStart;
    byte[]? script = null;
    foreach (var (name, storedSize, required) in header.Streams()) {
      if (storedSize == 0)
        continue;
      var s = Probe(data, p, storedSize);
      if (s is { } found && found.StoredSize == storedSize) {
        entries.Add(new(name, p, storedSize, found.Size, found.Crc, null));
        if (name == "WiseScript.bin")
          script = Inflate(data, p, found);
        p += storedSize;
      } else if (required)
        throw new InvalidDataException($"the {name} stream the header announces is not at 0x{p:X}");
    }

    var dataBase = p;
    var records = Records(script!).ToList();
    var walk = WalkStreams(data, dataBase, records);
    var claims = new List<(string Name, Probed Stream, long Start, DateTime? Modified)>();
    foreach (var record in records) {
      if (dataBase + record.End > data.Length || !walk.TryGetValue(record.Start, out var stream) ||
          stream.StoredSize != record.End - record.Start || stream.Size != record.Size || (record.Crc != 0 && record.Crc != stream.Crc))
        continue;
      claims.Add((record.Name, stream, record.Start, record.Modified));
    }

    // Install records past the end of this file belong to later volumes. Shape alone does not make
    // a record, so one counts only where it continues the chain of streams: it starts where a
    // stream of this file, or another such record, ends.
    var ends = walk.Select(kv => kv.Key + kv.Value.StoredSize).ToHashSet();
    var unavailable = 0;
    foreach (var record in records.Where(r => r.Kind == RecordKind.InstallFile && r.IsVariablePath && dataBase + r.End > data.Length).OrderBy(r => r.Start))
      if (ends.Contains(record.Start)) {
        ends.Add(record.End);
        ++unavailable;
      }

    // A later record keeps a name; an earlier one naming a different stream gets ~1, ~2, ...
    var winner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var c in claims)
      winner[c.Name] = c.Start;
    var taken = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
    var seen = new HashSet<(string, long)>();
    var claimed = new HashSet<long>();
    foreach (var c in claims) {
      claimed.Add(c.Start);
      if (!seen.Add((c.Name.ToUpperInvariant(), c.Start)) || winner[c.Name] != c.Start)
        continue;
      entries.Add(new(Unique(c.Name, taken), dataBase + c.Start, c.Stream.StoredSize, c.Stream.Size, c.Stream.Crc, c.Modified));
    }
    foreach (var c in claims)
      if (winner[c.Name] != c.Start && seen.Add((c.Name.ToUpperInvariant() + "~", c.Start)))
        entries.Add(new(Unique(c.Name + "~1", taken, c.Name), dataBase + c.Start, c.Stream.StoredSize, c.Stream.Size, c.Stream.Crc, c.Modified));

    foreach (var (start, stream) in walk.OrderBy(kv => kv.Key))
      if (!claimed.Contains(start))
        entries.Add(new($"DATA/{start:X8}.bin", dataBase + start, stream.StoredSize, stream.Size, stream.Crc, null));

    return new(data, header.Offset, dataBase, entries, unavailable);
  }

  /// <summary>Inflates one entry and checks its size and CRC-32.</summary>
  /// <exception cref="InvalidDataException">The stream does not inflate to the size and CRC-32 it was listed with.</exception>
  public byte[] Extract(WiseEntry entry) {
    var s = Probe(this._data, entry.Offset, entry.StoredSize);
    if (s is not { } found || found.StoredSize != entry.StoredSize || found.Size != entry.Size || found.Crc != entry.Crc)
      throw new InvalidDataException($"{entry.Path}: the stream at 0x{entry.Offset:X} does not match its CRC-32");
    return Inflate(this._data, entry.Offset, found);
  }

  // ---- streams ----------------------------------------------------------------------------

  /// <summary>A stream that inflated cleanly and whose trailing CRC-32 matched.</summary>
  private readonly record struct Probed(long StoredSize, long Size, uint Crc, long DeflateLength);

  /// <summary>
  /// Inflates the stream at <paramref name="offset"/> and checks the CRC-32 after it (directly,
  /// or after one pad byte). <paramref name="limit"/> bounds the bytes it may span (0: to the end).
  /// </summary>
  private static Probed? Probe(byte[] data, long offset, long limit) {
    if (offset < 0 || offset >= data.Length)
      return null;
    var available = data.Length - offset;
    var span = limit > 0 ? Math.Min(available, limit) : available;
    try {
      using var input = new MemoryStream(data, (int)offset, (int)span, writable: false);
      var inflater = new DeflateDecompressor(input);
      var crc = new Crc32();
      var buffer = new byte[16 * 1024];
      long size = 0;
      int n;
      while ((n = inflater.Decompress(buffer)) > 0) {
        crc.Update(buffer.AsSpan(0, n));
        size += n;
      }
      if (!inflater.IsFinished)
        return null;
      var deflated = input.Position;
      var value = crc.Value;
      for (var pad = 0; pad <= 1; ++pad) {
        var at = offset + deflated + pad;
        if (at + 4 > data.Length || (limit > 0 && deflated + pad + 4 > limit))
          break;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at)) == value)
          return new(deflated + pad + 4, size, value, deflated);
      }
      return null;
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IndexOutOfRangeException or ArgumentException) {
      return null;
    }
  }

  private static byte[] Inflate(byte[] data, long offset, Probed stream) {
    using var input = new MemoryStream(data, (int)offset, (int)stream.DeflateLength, writable: false);
    var output = new byte[stream.Size];
    var inflater = new DeflateDecompressor(input);
    var done = 0;
    while (done < output.Length) {
      var n = inflater.Decompress(output.AsSpan(done));
      if (n == 0)
        break;
      done += n;
    }
    if (done != output.Length || Crc32.Compute(output) != stream.Crc)
      throw new InvalidDataException($"the stream at 0x{offset:X} does not match its CRC-32");
    return output;
  }

  /// <summary>
  /// Walks the data area stream by stream from the data base; where a stream does not decode,
  /// resumes at the next offset a record names. Returns every stream found, keyed by its offset
  /// from the data base.
  /// </summary>
  private static Dictionary<long, Probed> WalkStreams(byte[] data, long dataBase, IEnumerable<Record> records) {
    var starts = records.Select(r => r.Start).Where(s => dataBase + s < data.Length).Distinct().Order().ToArray();
    var found = new Dictionary<long, Probed>();
    long q = 0;
    var next = 0;
    while (dataBase + q < data.Length) {
      if (Probe(data, dataBase + q, 0) is { } s) {
        found[q] = s;
        q += s.StoredSize;
        continue;
      }
      while (next < starts.Length && starts[next] <= q)
        ++next;
      if (next == starts.Length)
        break;
      q = starts[next];
    }
    return found;
  }

  // ---- overlay header ---------------------------------------------------------------------

  private sealed class Header {
    public long Offset;
    public uint Flags;
    public long DataStart;
    public uint ScriptStored, ScriptSize, FinalStored, FinalSize, BitmapStored, BitmapSize, InstallScriptStored;
    public readonly uint[] Slots = new uint[9];

    public IEnumerable<(string Name, long StoredSize, bool Required)> Streams() {
      if (this.BitmapStored != 0)
        yield return ("WiseColors.dib", this.BitmapStored, true);
      yield return ("WiseScript.bin", this.ScriptStored, true);
      for (var i = 0; i < this.Slots.Length; ++i)
        yield return (SlotNames[i], this.Slots[i], false);
      if (this.InstallScriptStored != 0)
        yield return ("INSTALL_SCRIPT", this.InstallScriptStored, false);
      yield return ("FILE00XX.DAT", this.FinalStored, true);
    }
  }

  private static Header? FindHeader(byte[] data) {
    var limit = Math.Min(data.Length, HeaderSearchLimit);
    for (var h = 0; h < limit; h += HeaderAlignment)
      if (TryHeader(data, h) is { } header)
        return header;
    return null;
  }

  private static Header? TryHeader(byte[] data, int h) {
    var span = data.AsSpan();
    var p = h;
    if (p + 0x60 > span.Length)
      return null;
    int nameLength = span[p++];
    if (nameLength > 0x40)
      return null;
    if (nameLength > 0)
      p += nameLength + 4;
    if (p + 4 + 12 + 4 * 4 + 9 * 4 + 3 * 4 > span.Length)
      return null;
    var header = new Header { Offset = h, Flags = U32(span, p) };
    p += 4 + 12 + 8; // flags, colours, exit and cancel event offsets
    header.ScriptSize = U32(span, p);
    header.ScriptStored = U32(span, p + 4);
    p += 8;
    for (var i = 0; i < 9; ++i, p += 4)
      header.Slots[i] = U32(span, p);
    header.FinalStored = U32(span, p);
    header.FinalSize = U32(span, p + 4);
    p += 12; // final support file sizes, installer length

    if (header.ScriptSize == 0 || header.ScriptSize > 64 << 20 || header.ScriptStored < 6 ||
        header.ScriptStored > header.ScriptSize + 64 || header.FinalStored < 6 || header.FinalStored > header.FinalSize + 64)
      return null;

    // The three endings: none; bitmap sizes + marker + text; bitmap sizes + install script + charset + marker + text.
    if (p + 8 <= span.Length) {
      var bitmapStored = U32(span, p);
      var bitmapSize = U32(span, p + 4);
      foreach (var extra in (int[])[0, 8]) {
        var m = p + 8 + extra;
        if (m + 3 > span.Length)
          continue;
        var marker = BinaryPrimitives.ReadUInt16LittleEndian(span[m..]);
        if (marker is not (0x0800 or 0x0008))
          continue;
        var textEnd = m + 3 + span[m + 2];
        if (bitmapStored == 0 || bitmapStored > bitmapSize + 64)
          continue;
        var candidate = Clone(header);
        candidate.BitmapStored = bitmapStored;
        candidate.BitmapSize = bitmapSize;
        candidate.InstallScriptStored = extra == 0 ? 0 : U32(span, p + 8);
        candidate.DataStart = textEnd;
        if (Validates(data, candidate))
          return candidate;
      }
    }
    header.DataStart = p;
    return Validates(data, header) ? header : null;
  }

  private static Header Clone(Header h) {
    var c = new Header {
      Offset = h.Offset, Flags = h.Flags, ScriptStored = h.ScriptStored, ScriptSize = h.ScriptSize,
      FinalStored = h.FinalStored, FinalSize = h.FinalSize,
    };
    h.Slots.CopyTo(c.Slots, 0);
    return c;
  }

  /// <summary>The first stream (bitmap, else script) must inflate to its announced size and CRC.</summary>
  private static bool Validates(byte[] data, Header h) {
    if ((h.Flags & FlagPkZip) != 0)
      return h.DataStart + 4 <= data.Length && data[h.DataStart] == 'P' && data[h.DataStart + 1] == 'K';
    var (stored, size) = h.BitmapStored != 0 ? (h.BitmapStored, h.BitmapSize) : (h.ScriptStored, h.ScriptSize);
    if (h.DataStart + stored > data.Length)
      return false;
    return Probe(data, h.DataStart, stored) is { } s && s.StoredSize == stored && s.Size == size;
  }

  private static uint U32(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);

  // ---- script records ---------------------------------------------------------------------

  private enum RecordKind { InstallFile, DialogSet, Billboard }

  private readonly record struct Record(RecordKind Kind, long Start, long End, long Size, uint Crc, string Name, DateTime? Modified, bool IsVariablePath = false);

  /// <summary>
  /// Every place in the script that has the shape of an install-file, custom-dialog-set or
  /// billboard record, in script order. Shape alone is weak evidence; the caller keeps only
  /// records whose stream inflates to the size (and CRC) they give.
  /// </summary>
  private static IEnumerable<Record> Records(byte[] script) {
    for (var i = 0; i + 13 < script.Length; ++i) {
      switch (script[i]) {
        case 0x00 when i + 43 < script.Length: {
          var start = U32(script, i + 3);
          var end = U32(script, i + 7);
          var size = U32(script, i + 15);
          if (!Plausible(start, end, size))
            break;
          var name = CString(script, i + 43, out _);
          if (name is null)
            break;
          yield return new(RecordKind.InstallFile, start, end, size, U32(script, i + 39), InstallPath(name),
            DosTime(BinaryPrimitives.ReadUInt16LittleEndian(script.AsSpan(i + 11)), BinaryPrimitives.ReadUInt16LittleEndian(script.AsSpan(i + 13))),
            name.StartsWith('%'));
          break;
        }
        case 0x14: {
          var start = U32(script, i + 1);
          var end = U32(script, i + 5);
          var size = U32(script, i + 9);
          if (!Plausible(start, end, size))
            break;
          var variable = CString(script, i + 13, out var next, allowEmpty: true);
          var title = variable is null ? null : CString(script, next, out _, allowEmpty: true);
          if (title is null)
            break;
          yield return new(RecordKind.DialogSet, start, end, size, 0, "CustomDialogSet_" + SafeName(variable!.Replace("%", "") + "-" + title.Replace("%", "")), null);
          break;
        }
        case 0x06 when i + 19 < script.Length: {
          var flags = BinaryPrimitives.ReadUInt16LittleEndian(script.AsSpan(i + 1));
          var operand2 = BinaryPrimitives.ReadUInt16LittleEndian(script.AsSpan(i + 3));
          var operand3 = BinaryPrimitives.ReadUInt16LittleEndian(script.AsSpan(i + 5));
          for (int language = 0, at = i + 7; at + 12 <= script.Length; ++language, at += 12) {
            var start = U32(script, at);
            var end = U32(script, at + 4);
            var size = U32(script, at + 8);
            if (!Plausible(start, end, size))
              break;
            yield return new(RecordKind.Billboard, start, end, size, 0, $"CustomBillboardSet_{flags:X4}-{operand2}-{operand3}{language:X4}", null);
          }
          break;
        }
      }
    }
  }

  private static bool Plausible(uint start, uint end, uint size) => end > start && end - start >= 6 && size < 1u << 31;

  private static string? CString(byte[] s, int at, out int next, bool allowEmpty = false) {
    next = at;
    var end = Array.IndexOf(s, (byte)0, at, Math.Min(MaxNameLength, s.Length - at));
    if (end < 0 || (!allowEmpty && end == at))
      return null;
    for (var k = at; k < end; ++k)
      if (s[k] < 0x20)
        return null;
    next = end + 1;
    return Ansi.GetString(s, at, end - at);
  }

  /// <summary>The destination with every <c>%</c> dropped and backslashes made slashes.</summary>
  private static string InstallPath(string destination) =>
    string.Join('/', destination.Replace("%", "").Split('\\', '/').Where(s => s.Length > 0 && s != "." && s != ".."));

  /// <summary>Takes <paramref name="wanted"/>, or the first free <c>stem~n</c> when it is taken.</summary>
  private static string Unique(string wanted, HashSet<string> taken, string? stem = null) {
    if (taken.Add(wanted))
      return wanted;
    stem ??= wanted;
    for (var n = 1; ; ++n)
      if (taken.Add($"{stem}~{n}"))
        return $"{stem}~{n}";
  }

  private static string SafeName(string s) {
    var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
    return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
  }

  private static DateTime? DosTime(ushort date, ushort time) {
    if (date == 0)
      return null;
    try {
      return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Local);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }
}
