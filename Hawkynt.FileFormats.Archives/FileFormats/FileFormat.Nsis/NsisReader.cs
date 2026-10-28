using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using FileFormat.Bzip2;

namespace FileFormat.Nsis;

/// <summary>
/// Reads the files an NSIS (Nullsoft Scriptable Install System) installer carries.
/// </summary>
/// <remarks>
/// <para>Layout, from NSIS's own <c>Source/exehead/fileform.h</c> and <c>fileform.c</c>
/// (zlib/libpng licence): the exe stub, then at a multiple of 512 bytes the 28-byte
/// <c>firstheader</c> (flags, 0xDEADBEEF, "NullsoftInst", header length, archive length), then
/// either</para>
/// <list type="bullet">
///   <item><description>non-solid: a length-prefixed header block followed by one
///   length-prefixed block per file, each compressed on its own (bit 31 of the length word set)
///   or stored; or</description></item>
///   <item><description>solid: one compressed stream holding the same length-prefixed blocks,
///   lengths then without the compression bit.</description></item>
/// </list>
/// <para>The header holds the install script as an array of 28-byte instructions plus a string
/// table. File names are recovered by replaying <c>SetOutPath</c> (EW_CREATEDIR) and
/// <c>File</c> (EW_EXTRACTFILE) the way 7-Zip's NSIS handler (LGPL-2.1+) does; its compressor
/// signature table and NSIS 2/3 string-code detection were the model for the ones here.</para>
/// </remarks>
public sealed class NsisReader : IDisposable {

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly long _streamStart;       // absolute offset just past firstheader
  private readonly long _nonSolidDataStart; // absolute offset of data-section offset 0 (non-solid)
  private readonly byte[] _header;
  private readonly int _stringsOffset;
  private readonly int _stringChars;
  private readonly List<NsisEntry> _entries = [];
  private bool _sizesResolved;
  private bool _disposed;

  /// <summary>Gets the files the install script extracts, in script order.</summary>
  public IReadOnlyList<NsisEntry> Entries => this._entries;

  /// <summary>Gets the compressor the installer was built with.</summary>
  public NsisCompression Compression { get; }

  /// <summary>Gets whether the header and files share one compressed stream (<c>/SOLID</c>).</summary>
  public bool IsSolid { get; }

  /// <summary>Gets whether the installer's strings are UTF-16 (<c>Unicode true</c>, NSIS 3).</summary>
  public bool IsUnicode { get; }

  /// <summary>Gets whether the strings use the NSIS 3 escape codes rather than NSIS 2's.</summary>
  public bool IsNsis3 { get; }

  /// <summary>Gets whether the header was written for a 64-bit exehead.</summary>
  public bool Is64Bit { get; }

  /// <summary>Gets whether the LZMA stream carries the x86 branch-filter flag byte.</summary>
  public bool HasFilterFlag { get; }

  /// <summary>Gets the file offset of <c>firstheader</c>.</summary>
  public long FirstHeaderOffset { get; }

  /// <summary>Gets <c>firstheader.flags</c> (FH_FLAGS_*: 1 = uninstaller, 2 = silent, 4 = no CRC, 8 = force CRC).</summary>
  public int FirstHeaderFlags { get; }

  /// <summary>Gets the uncompressed size of the script header.</summary>
  public int HeaderLength { get; }

  /// <summary>Gets <c>length_of_all_following_data</c>: firstheader, data and CRC.</summary>
  public int ArchiveLength { get; }

  /// <summary>
  /// Opens an NSIS installer.
  /// </summary>
  /// <param name="stream">A seekable stream holding the whole installer executable.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  /// <exception cref="InvalidDataException">No NSIS first header, or a header that does not parse.</exception>
  /// <exception cref="NotSupportedException">An LZMA stream behind the x86 branch filter.</exception>
  public NsisReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._stream = stream;
    this._leaveOpen = leaveOpen;

    this.FirstHeaderOffset = FindFirstHeader(stream);
    Span<byte> fh = stackalloc byte[NsisConstants.FirstHeaderSize];
    stream.Position = this.FirstHeaderOffset;
    stream.ReadExactly(fh);
    this.FirstHeaderFlags = BinaryPrimitives.ReadInt32LittleEndian(fh);
    this.HeaderLength = BinaryPrimitives.ReadInt32LittleEndian(fh[20..]);
    this.ArchiveLength = BinaryPrimitives.ReadInt32LittleEndian(fh[24..]);
    if (this.HeaderLength <= 0 || this.HeaderLength > stream.Length * 64L)
      throw new InvalidDataException($"Implausible NSIS header length {this.HeaderLength}.");

    this._streamStart = this.FirstHeaderOffset + NsisConstants.FirstHeaderSize;
    Span<byte> sig = stackalloc byte[12];
    stream.Position = this._streamStart;
    if (stream.ReadAtLeast(sig, sig.Length, throwOnEndOfStream: false) < sig.Length)
      throw new InvalidDataException("NSIS data is truncated.");

    // The compressor is not recorded anywhere; it is recognised by the first bytes of the data,
    // exactly as the table in 7-Zip's NsisIn.cpp lays out:
    //   LL LL LL LL (== header length)     non-solid, header stored
    //   5D 00 00 dd dd dd dd 00            solid LZMA ([00|01] before it: filter flag byte)
    //   SS SS SS 80 [ff] 5D 00 00 ...      non-solid LZMA
    //   SS SS SS 80 31 tt                  non-solid bzip2
    //   SS SS SS 80 ..                     non-solid Deflate
    //   31 tt                              solid bzip2
    //   anything else                      solid Deflate
    var firstWord = BinaryPrimitives.ReadUInt32LittleEndian(sig);
    bool filterFlag;
    if (firstWord == (uint)this.HeaderLength) {
      this.Compression = NsisCompression.None;
      filterFlag = false;
    } else if (IsLzma(sig, out filterFlag)) {
      this.Compression = NsisCompression.Lzma;
      this.IsSolid = true;
    } else if (sig[3] == 0x80) {
      this.Compression = IsLzma(sig[4..], out filterFlag) ? NsisCompression.Lzma
        : IsBzip2(sig[4..]) ? NsisCompression.BZip2
        : NsisCompression.Zlib;
    } else {
      this.Compression = IsBzip2(sig) ? NsisCompression.BZip2 : NsisCompression.Zlib;
      this.IsSolid = true;
    }
    this.HasFilterFlag = filterFlag;

    if (this.IsSolid) {
      this._header = this.ReadSolidHeader();
    } else {
      var blockLength = firstWord & ~NsisConstants.CompressedFlag;
      this._nonSolidDataStart = this._streamStart + 4 + blockLength;
      this._header = this.ReadBlock(this._streamStart);
      if (this._header.Length != this.HeaderLength)
        throw new InvalidDataException($"NSIS header block holds {this._header.Length} bytes, the first header promises {this.HeaderLength}.");
    }

    // NSIS 3's Deflate is stock; NSIS 2's stored blocks lack NLEN. Which one applies is known
    // only from the strings, so the header itself was read with the stock rule first and the
    // NSIS 2 rule as the fallback (DecodeBlock, ReadSolidHeader).
    (this.Is64Bit, this._stringsOffset, this._stringChars, this.IsUnicode, this.IsNsis3) = this.ParseLayout();
    this.CollectEntries();
  }

  // ── public operations ───────────────────────────────────────────────

  /// <summary>
  /// Returns the decompressed bytes of one entry.
  /// </summary>
  /// <param name="entry">An entry from <see cref="Entries"/>.</param>
  public byte[] Extract(NsisEntry entry) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(entry);
    byte[]? result = null;
    this.ExtractMany([entry], (_, data) => result = data);
    return result ?? throw new InvalidDataException($"NSIS data block for '{entry.FileName}' was not found.");
  }

  /// <summary>
  /// Decodes the given entries, calling <paramref name="sink"/> once per entry. A solid installer
  /// is decoded in a single forward pass that stops after the last requested block.
  /// </summary>
  /// <param name="entries">Entries from <see cref="Entries"/>.</param>
  /// <param name="sink">Receives each entry with its bytes.</param>
  public void ExtractMany(IEnumerable<NsisEntry> entries, Action<NsisEntry, byte[]> sink) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(sink);
    var byOffset = entries.GroupBy(e => e.DataOffset).ToDictionary(g => (long)g.Key, g => g.ToArray());
    if (byOffset.Count == 0)
      return;

    if (!this.IsSolid) {
      foreach (var (offset, group) in byOffset.OrderBy(kv => kv.Key)) {
        var data = this.ReadBlock(this._nonSolidDataStart + offset);
        foreach (var e in group)
          sink(e, data);
      }
      return;
    }

    var pending = byOffset.Count;
    this.WalkSolid(
      wants: (offset, _) => byOffset.ContainsKey(offset),
      deliver: (offset, data) => {
        foreach (var e in byOffset[offset])
          sink(e, data);
        return --pending > 0;
      });
    if (pending > 0)
      throw new InvalidDataException("The NSIS solid stream ended before every requested file.");
  }

  /// <summary>
  /// Fills in <see cref="NsisEntry.Size"/> (and, for non-solid installers, <see cref="NsisEntry.CompressedSize"/>)
  /// for every entry. Compressed blocks have to be decoded to learn their size.
  /// </summary>
  public void ResolveSizes() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (this._sizesResolved)
      return;

    var byOffset = this._entries.GroupBy(e => (long)e.DataOffset).ToDictionary(g => g.Key, g => g.ToArray());
    if (!this.IsSolid) {
      foreach (var (offset, group) in byOffset) {
        var (stored, compressed) = this.MeasureBlock(this._nonSolidDataStart + offset);
        foreach (var e in group) {
          e.Size = stored;
          e.CompressedSize = compressed;
        }
      }
    } else if (byOffset.Count > 0) {
      var pending = byOffset.Count;
      this.WalkSolid(
        wants: (offset, length) => {
          if (!byOffset.TryGetValue(offset, out var group))
            return false;
          foreach (var e in group)
            e.Size = length;
          if (--pending == 0)
            throw new StopDecoding();
          return false;
        },
        deliver: (_, _) => true);
    }
    this._sizesResolved = true;
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  // ── locating and decoding ───────────────────────────────────────────

  private static long FindFirstHeader(Stream stream) {
    Span<byte> buf = stackalloc byte[NsisConstants.FirstHeaderSize];
    for (long pos = 0; pos + NsisConstants.FirstHeaderSize <= stream.Length; pos += NsisConstants.FirstHeaderAlignment) {
      stream.Position = pos;
      if (stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) < buf.Length)
        break;
      if ((BinaryPrimitives.ReadInt32LittleEndian(buf) & ~NsisConstants.FirstHeaderFlagsMask) == 0
          && BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]) == NsisConstants.FirstHeaderSignature
          && buf.Slice(8, 12).SequenceEqual(NsisConstants.Magic))
        return pos;
    }
    throw new InvalidDataException("No NSIS first header found.");
  }

  private static bool IsLzma(ReadOnlySpan<byte> p, out bool filterFlag) {
    filterFlag = false;
    if (LooksLikeLzmaProperties(p))
      return true;
    if (p[0] <= 1 && LooksLikeLzmaProperties(p[1..])) {
      filterFlag = true;
      return true;
    }
    return false;
  }

  // lc=3 lp=0 pb=2 (0x5D), then a dictionary size below 2^31 that is a multiple of 64 KiB.
  private static bool LooksLikeLzmaProperties(ReadOnlySpan<byte> p)
    => p.Length >= 7 && p[0] == 0x5D && p[1] == 0 && p[2] == 0 && p[5] == 0 && (p[6] & 0x80) == 0;

  private static bool IsBzip2(ReadOnlySpan<byte> p) => p[0] == 0x31 && p[1] < 14;

  /// <summary>Reads one length-prefixed block of a non-solid installer.</summary>
  private byte[] ReadBlock(long absoluteOffset) {
    var (compressed, length) = this.ReadLengthWord(absoluteOffset);
    if (!compressed) {
      var raw = new byte[length];
      this._stream.ReadExactly(raw);
      return raw;
    }

    using var output = new MemoryStream();
    this.DecodeBlock(absoluteOffset + 4, length, output);
    return output.ToArray();
  }

  private (long Size, long Compressed) MeasureBlock(long absoluteOffset) {
    var (compressed, length) = this.ReadLengthWord(absoluteOffset);
    if (!compressed)
      return (length, length);
    using var counter = new CountingSink();
    this.DecodeBlock(absoluteOffset + 4, length, counter);
    return (counter.Count, length);
  }

  private (bool Compressed, int Length) ReadLengthWord(long absoluteOffset) {
    if (absoluteOffset < this._streamStart || absoluteOffset + 4 > this._stream.Length)
      throw new InvalidDataException("NSIS data offset lies outside the file.");
    Span<byte> word = stackalloc byte[4];
    this._stream.Position = absoluteOffset;
    this._stream.ReadExactly(word);
    var value = BinaryPrimitives.ReadUInt32LittleEndian(word);
    var compressed = (value & NsisConstants.CompressedFlag) != 0 && this.Compression != NsisCompression.None;
    var length = (long)(value & ~NsisConstants.CompressedFlag);
    if (absoluteOffset + 4 + length > this._stream.Length)
      throw new InvalidDataException("NSIS data block runs past the end of the file.");
    return (compressed, (int)length);
  }

  private void DecodeBlock(long absoluteOffset, int length, Stream output) {
    // NSIS 2's inflate takes a stored block's LEN without NLEN; NSIS 3 is stock zlib. Before the
    // strings have told the dialect apart (the header block itself) the stock rule is tried first.
    var nsisDeflate = this._header is null ? (bool?)null : !this.IsNsis3;
    if (nsisDeflate is null && this.Compression == NsisCompression.Zlib) {
      var start = output.Position;
      try {
        this.DecodeBounded(absoluteOffset, length, output, nsisDeflate: false);
        return;
      } catch (InvalidDataException) {
        output.SetLength(start);
        output.Position = start;
      }
      nsisDeflate = true;
    }
    this.DecodeBounded(absoluteOffset, length, output, nsisDeflate ?? false);
  }

  private void DecodeBounded(long absoluteOffset, int length, Stream output, bool nsisDeflate) {
    this._stream.Position = absoluteOffset;
    using var bounded = new SubStream(this._stream, length, leaveOpen: true);
    this.Decode(bounded, output, nsisDeflate);
  }

  /// <summary>Decodes one compressed NSIS stream from <paramref name="input"/> into <paramref name="output"/>.</summary>
  private void Decode(Stream input, Stream output, bool nsisDeflate) {
    switch (this.Compression) {
      case NsisCompression.Lzma: {
        if (this.HasFilterFlag) {
          var flag = input.ReadByte();
          if (flag < 0)
            throw new InvalidDataException("NSIS LZMA stream is truncated.");
          if (flag > 1)
            throw new InvalidDataException($"Invalid NSIS LZMA filter flag {flag}.");
          if (flag == 1)
            throw new NotSupportedException("NSIS LZMA streams behind the x86 branch filter (seen only in 7-Zip's own installer) are not supported.");
        }
        var props = new byte[5];
        input.ReadExactly(props);
        new LzmaDecoder(input, props).Decode(output);
        break;
      }
      case NsisCompression.Zlib: {
        var inflater = new DeflateDecompressor(input) { StoredBlocksOmitComplement = nsisDeflate };
        var buffer = new byte[81920];
        while (true) {
          var n = inflater.Decompress(buffer);
          if (n <= 0)
            break;
          output.Write(buffer, 0, n);
        }
        if (!inflater.IsFinished)
          throw new InvalidDataException("NSIS Deflate stream is truncated.");
        break;
      }
      case NsisCompression.BZip2: {
        using var bz = Bzip2Stream.OpenNsisDecoder(input, leaveOpen: true);
        bz.CopyTo(output);
        break;
      }
      default:
        input.CopyTo(output);
        break;
    }
  }

  private byte[] ReadSolidHeader() {
    byte[]? header = null;
    try {
      this.WalkSolid(wants: (_, _) => false, deliver: (_, _) => true, onHeader: data => header = data, nsisDeflate: false);
    } catch (InvalidDataException) when (this.Compression == NsisCompression.Zlib) {
      // A stored block without NLEN: an NSIS 2 stream.
      header = null;
      this.WalkSolid(wants: (_, _) => false, deliver: (_, _) => true, onHeader: data => header = data, nsisDeflate: true);
    }
    return header ?? throw new InvalidDataException("NSIS solid stream ended inside the header.");
  }

  /// <summary>
  /// Runs the solid stream through a <see cref="SolidSink"/>, which splits it into the header
  /// block and the data blocks.
  /// </summary>
  /// <param name="wants">Called with each data block's offset and length; return true to receive its bytes.</param>
  /// <param name="deliver">Receives a wanted block; return false once nothing more is needed.</param>
  /// <param name="onHeader">Receives the header block; when null the header is skipped.</param>
  /// <param name="nsisDeflate">Decode stored Deflate blocks the NSIS 2 way; defaults to what the strings showed.</param>
  private void WalkSolid(Func<long, int, bool> wants, Func<long, byte[], bool> deliver, Action<byte[]>? onHeader = null, bool? nsisDeflate = null) {
    var sink = new SolidSink(this.HeaderLength, wants, deliver, onHeader);
    var nsis2 = nsisDeflate ?? !this.IsNsis3;
    try {
      this._stream.Position = this._streamStart;
      var remaining = this._stream.Length - this._streamStart;
      using var bounded = new SubStream(this._stream, remaining, leaveOpen: true);
      this.Decode(bounded, sink, nsis2);
    } catch (StopDecoding) {
      // The sink has everything it was asked for.
    }
  }

  // ── header parsing ──────────────────────────────────────────────────

  private (bool Is64Bit, int StringsOffset, int StringChars, bool Unicode, bool Nsis3) ParseLayout() {
    var h = this._header;
    // header = flags, blocks[8] of {offset, num}; the offset is pointer-sized for a 64-bit
    // exehead (12-byte packed records). A 64-bit layout shows as eight zero high words.
    var is64 = h.Length >= 4 + 12 * 8;
    for (var k = 0; is64 && k < 8; ++k)
      if (ReadInt32(h, 4 + 12 * k + 4) != 0)
        is64 = false;
    var recordSize = is64 ? 12 : 8;
    if (h.Length < 4 + recordSize * 8)
      throw new InvalidDataException("NSIS header is too short for its block table.");

    int Offset(int block) => ReadInt32(h, 4 + recordSize * block);
    int Count(int block) => ReadInt32(h, 4 + recordSize * block + recordSize - 4);

    var entriesOffset = Offset(NsisConstants.BlockEntries);
    var entriesCount = Count(NsisConstants.BlockEntries);
    var stringsOffset = Offset(NsisConstants.BlockStrings);
    var langOffset = Offset(NsisConstants.BlockLangTables);
    if (entriesOffset < 0 || stringsOffset < 0 || langOffset < stringsOffset || langOffset > h.Length || entriesOffset > h.Length)
      throw new InvalidDataException("NSIS header block table is inconsistent.");
    if (entriesCount < 0 || (long)entriesCount * NsisConstants.EntrySize > h.Length - entriesOffset)
      throw new InvalidDataException("NSIS instruction table runs past the header.");

    var tableBytes = langOffset - stringsOffset;
    if (tableBytes < 2 || h[langOffset - 1] != 0)
      throw new InvalidDataException("NSIS string table is not terminated.");
    // The table starts with the empty string; a UTF-16 table therefore starts with two zero bytes.
    var unicode = h[stringsOffset] == 0 && h[stringsOffset + 1] == 0;
    if (unicode && ((tableBytes & 1) != 0 || h[langOffset - 2] != 0))
      throw new InvalidDataException("NSIS UTF-16 string table is malformed.");
    var chars = unicode ? tableBytes / 2 : tableBytes;
    return (is64, stringsOffset, chars, unicode, DetectNsis3(h.AsSpan(stringsOffset, tableBytes), unicode));
  }

  /// <summary>
  /// NSIS 3 escapes a variable as 0x03 plus a coded short with both high bits set; NSIS 2 used
  /// 0xFD. Unicode installers are always NSIS 3. For ANSI ones, count both patterns.
  /// </summary>
  private static bool DetectNsis3(ReadOnlySpan<byte> table, bool unicode) {
    if (unicode)
      return true;
    int nsis3 = 0, nsis2 = 0;
    for (var i = 0; i + 2 < table.Length; ++i) {
      if ((table[i + 1] & 0x80) == 0 || (table[i + 2] & 0x80) == 0)
        continue;
      if (table[i] == NsisConstants.Nsis3VarCode)
        ++nsis3;
      else if (table[i] == NsisConstants.Nsis2VarCode)
        ++nsis2;
    }
    return nsis3 >= nsis2;
  }

  private void CollectEntries() {
    var h = this._header;
    var recordSize = this.Is64Bit ? 12 : 8;
    var entriesOffset = ReadInt32(h, 4 + recordSize * NsisConstants.BlockEntries);
    var entriesCount = ReadInt32(h, 4 + recordSize * NsisConstants.BlockEntries + recordSize - 4);

    var outDir = "$INSTDIR";
    var seen = new HashSet<(string, uint)>();
    Span<int> p = stackalloc int[6];
    for (var i = 0; i < entriesCount; ++i) {
      var at = entriesOffset + i * NsisConstants.EntrySize;
      var opcode = ReadInt32(h, at);
      for (var k = 0; k < 6; ++k)
        p[k] = ReadInt32(h, at + 4 + 4 * k);

      switch (opcode) {
        case NsisConstants.OpCreateDir when p[1] != 0: {
          // SetOutPath. "$OUTDIR\x" is relative to the current output directory.
          var path = this.RenderString(p[0], out var leadingVar);
          if (leadingVar == NsisConstants.VarOutDir)
            path = outDir + path["$OUTDIR".Length..];
          outDir = path;
          break;
        }
        case NsisConstants.OpExtractFile: {
          var name = this.RenderString(p[1], out var leadingVar);
          var absolute = leadingVar is NsisConstants.VarInstDir or NsisConstants.VarOutDir or NsisConstants.VarExeDir
                           or NsisConstants.VarTemp or NsisConstants.VarPluginsDir or ShellFolderMarker
                         || IsAbsolutePath(name);
          var full = absolute ? name : outDir.TrimEnd('\\') + "\\" + (name.Length == 0 ? "file" : name);
          if (full.StartsWith("$INSTDIR\\", StringComparison.OrdinalIgnoreCase))
            full = full["$INSTDIR\\".Length..].TrimStart('\\');
          full = full.Replace('\\', '/');
          var offset = (uint)p[2];
          if (!seen.Add((full, offset)))
            break;
          this._entries.Add(new NsisEntry(full, offset, ToFileTime(p[3], p[4])));
          break;
        }
      }
    }
  }

  private static bool IsAbsolutePath(string s)
    => (s.Length >= 2 && s[0] == '\\' && s[1] == '\\') || (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':');

  private static DateTime? ToFileTime(int low, int high) {
    var ticks = ((long)(uint)high << 32) | (uint)low;
    if (ticks <= 0 || ticks == -1 || ticks > DateTime.MaxValue.ToFileTimeUtc())
      return null;
    return DateTime.FromFileTimeUtc(ticks);
  }

  // ── strings ─────────────────────────────────────────────────────────

  private const int ShellFolderMarker = -2;

  /// <summary>
  /// Renders a script string with its variables spelled out (<c>$INSTDIR</c>, <c>$R0</c>,
  /// <c>$SYSDIR</c>, ...). <paramref name="leadingVar"/> reports what the string starts with:
  /// a variable index, <see cref="ShellFolderMarker"/> for a shell folder, or -1.
  /// </summary>
  private string RenderString(int index, out int leadingVar) {
    leadingVar = -1;
    if (index < 0)
      return $"$(LSTR_{-(index + 1)})";
    if (index >= this._stringChars)
      return string.Empty;

    var sb = new StringBuilder();
    int langCode, shellCode, varCode, skipCode;
    if (this.IsNsis3)
      (langCode, shellCode, varCode, skipCode) = (NsisConstants.Nsis3LangCode, NsisConstants.Nsis3ShellCode, NsisConstants.Nsis3VarCode, NsisConstants.Nsis3SkipCode);
    else
      (langCode, shellCode, varCode, skipCode) = (NsisConstants.Nsis2LangCode, NsisConstants.Nsis2ShellCode, NsisConstants.Nsis2VarCode, NsisConstants.Nsis2SkipCode);

    var pos = index;
    var first = true;
    while (pos < this._stringChars) {
      var c = this.CharAt(pos++);
      if (c == 0)
        break;
      if (c == langCode || c == shellCode || c == varCode) {
        // Two bytes follow: one UTF-16 unit, or two ANSI bytes. Low byte first either way.
        int b0, b1;
        if (this.IsUnicode) {
          var w = pos < this._stringChars ? this.CharAt(pos++) : 0;
          (b0, b1) = (w & 0xFF, w >> 8);
        } else {
          b0 = pos < this._stringChars ? this.CharAt(pos++) : 0;
          b1 = pos < this._stringChars ? this.CharAt(pos++) : 0;
        }
        var data = ((b1 & 0x7F) << 7) | (b0 & 0x7F);
        if (c == varCode) {
          if (first)
            leadingVar = data;
          sb.Append('$').Append(VariableName(data));
        } else if (c == shellCode) {
          if (first)
            leadingVar = ShellFolderMarker;
          sb.Append(this.ShellFolderName(b0, b1));
        } else {
          sb.Append("$(LSTR_").Append(data).Append(')');
        }
      } else if (c == skipCode) {
        if (pos < this._stringChars)
          sb.Append(ToChar(this.CharAt(pos++)));
      } else {
        sb.Append(ToChar(c));
      }
      first = false;
    }
    return sb.ToString();
  }

  private int CharAt(int index) => this.IsUnicode
    ? BinaryPrimitives.ReadUInt16LittleEndian(this._header.AsSpan(this._stringsOffset + index * 2))
    : this._header[this._stringsOffset + index];

  // ANSI strings are in the compiling machine's code page, which the file does not record;
  // Latin-1 keeps every byte distinct and round-trips the ASCII that file names nearly always are.
  private static char ToChar(int c) => (char)c;

  private static string VariableName(int index) => index switch {
    < 10 => index.ToString(System.Globalization.CultureInfo.InvariantCulture),
    < 20 => "R" + (index - 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
    _ when index - 20 < NsisConstants.InternalVariables.Length => NsisConstants.InternalVariables[index - 20],
    _ => "_" + (index - 20 - NsisConstants.InternalVariables.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) + "_",
  };

  private string ShellFolderName(int index1, int index2) {
    // Bit 7 set: the path comes from HKLM\Software\Microsoft\Windows\CurrentVersion, value name
    // in the string table at (index1 & 0x3F); bit 6 asks for the 64-bit registry view.
    if ((index1 & 0x80) != 0) {
      var valueName = this.RenderString(index1 & 0x3F, out _);
      var name = valueName switch {
        "ProgramFilesDir" => "$PROGRAMFILES",
        "CommonFilesDir" => "$COMMONFILES",
        _ => "$_REGISTRY_(" + valueName + ")",
      };
      return (index1 & 0x40) != 0 ? name + "64" : name;
    }
    var folders = NsisConstants.ShellFolders;
    if (index1 < folders.Length && folders[index1] is { } a)
      return "$" + a;
    if (index2 < folders.Length && folders[index2] is { } b)
      return "$" + b;
    return "$_SHELL_" + index1.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_";
  }

  private static int ReadInt32(byte[] data, int offset)
    => offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) : 0;

  // ── solid stream splitting ──────────────────────────────────────────

  private sealed class StopDecoding : Exception;

  /// <summary>Counts bytes written to it and discards them.</summary>
  private sealed class CountingSink : Stream {
    public long Count { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this.Count;
    public override long Position { get => this.Count; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this.Count += count;
    public override void Write(ReadOnlySpan<byte> buffer) => this.Count += buffer.Length;
  }

  /// <summary>
  /// Write-only stream that splits a decoded solid stream into its blocks as the decoder
  /// produces them: the header block first, then data blocks whose offsets count from the end of
  /// the header block. It throws <see cref="StopDecoding"/> once its caller needs nothing more.
  /// </summary>
  private sealed class SolidSink(int headerLength, Func<long, int, bool> wants, Func<long, byte[], bool> deliver, Action<byte[]>? onHeader) : Stream {
    private readonly byte[] _word = new byte[4];
    private int _wordFill;
    private bool _inBlock;
    private long _blockOffset = -1;  // -1 = the header block
    private long _nextOffset = -1;
    private long _remaining;
    private byte[]? _buffer;
    private int _bufferFill;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data) {
      while (!data.IsEmpty) {
        if (!this._inBlock) {
          var take = Math.Min(4 - this._wordFill, data.Length);
          data[..take].CopyTo(this._word.AsSpan(this._wordFill));
          this._wordFill += take;
          data = data[take..];
          if (this._wordFill < 4)
            return;
          this._wordFill = 0;
          this.BeginBlock((int)(BinaryPrimitives.ReadUInt32LittleEndian(this._word) & 0x7FFFFFFF));
          continue;
        }

        var n = (int)Math.Min(this._remaining, data.Length);
        if (this._buffer != null)
          data[..n].CopyTo(this._buffer.AsSpan(this._bufferFill));
        this._bufferFill += n;
        this._remaining -= n;
        data = data[n..];
        if (this._remaining == 0)
          this.EndBlock();
      }
    }

    private void BeginBlock(int length) {
      this._blockOffset = this._nextOffset;
      this._nextOffset = this._blockOffset < 0 ? 0 : this._blockOffset + 4 + length;
      bool want;
      if (this._blockOffset < 0) {
        if (length != headerLength)
          throw new InvalidDataException($"NSIS solid stream starts with a {length}-byte block, the first header promises {headerLength}.");
        want = onHeader != null;
      } else
        want = wants(this._blockOffset, length);

      this._buffer = want ? new byte[length] : null;
      this._bufferFill = 0;
      this._remaining = length;
      this._inBlock = true;
      if (length == 0)
        this.EndBlock();
    }

    private void EndBlock() {
      this._inBlock = false;
      var data = this._buffer;
      this._buffer = null;
      if (this._blockOffset < 0) {
        if (data != null) {
          onHeader!(data);
          throw new StopDecoding();
        }
        return;
      }
      if (data != null && !deliver(this._blockOffset, data))
        throw new StopDecoding();
    }
  }
}
