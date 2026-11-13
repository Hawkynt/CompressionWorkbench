using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;
using FileFormat.Bzip2;

namespace FileFormat.Nsis;

/// <summary>A file an NSIS installer writes.</summary>
/// <param name="Path">Its path below the installation directory, "/"- or "\"-separated.</param>
/// <param name="Data">Its contents.</param>
/// <param name="LastWriteTimeUtc">The modification time the installer gives it; the time of writing when null.</param>
public sealed record NsisInstallerFile(string Path, byte[] Data, DateTime? LastWriteTimeUtc = null);

/// <summary>How <see cref="NsisInstallerWriter"/> builds an installer.</summary>
public sealed class NsisInstallerOptions {
  /// <summary>The compressor: zlib, bzip2 or LZMA. The stub must be the one NSIS ships for it.</summary>
  public NsisCompression Compression { get; init; } = NsisCompression.Lzma;

  /// <summary>One compressed stream for the header and every file, instead of one per file.</summary>
  public bool Solid { get; init; }

  /// <summary>The product name the installer shows ($(^Name)).</summary>
  public string Name { get; init; } = "Setup";

  /// <summary>
  /// The default installation directory (InstallDir): an absolute path, or one starting with
  /// <c>$PROGRAMFILES</c> or <c>$TEMP</c>. <c>/D=</c> on the command line overrides it.
  /// Defaults to <c>$PROGRAMFILES\</c> and the name.
  /// </summary>
  public string? InstallDirectory { get; init; }
}

/// <summary>
/// Writes NSIS 3 installers: an exehead stub from NSIS's <c>Stubs</c> folder, prepared as makensis
/// prepares it, then the firstheader, the script header and the files, and the CRC-32. The script
/// is what makensis compiles from <c>Section</c>, <c>SetOutPath $INSTDIR\...</c> and <c>File</c>
/// lines with the default pages, so the installer runs interactively or with <c>/S /D=dir</c>.
/// </summary>
/// <remarks>
/// <para>Layout from NSIS's own sources (zlib/libpng licence): <c>Source/exehead/fileform.h</c>
/// (firstheader, header, section, entry and page structures, EW_* opcodes),
/// <c>Source/exehead/fileform.c</c> (the CRC-32 over the file from byte 512),
/// <c>Source/build.cpp</c> and <c>Source/fileform.cpp</c> (block order and header defaults), and the
/// English strings of <c>Contrib/Language files/English.nlf</c>.</para>
/// <para>The exehead stub is native code and is not part of this library: the caller passes
/// one, or <see cref="LoadStub"/> takes it from an NSIS installation. Only the Unicode x86 stubs
/// of NSIS 3 are accepted.</para>
/// </remarks>
public static class NsisInstallerWriter {
  private const int FirstHeaderFlags = 0;
  private const int HeaderSize = 300;          // header with 8 blocks, x86 pointers
  private const int LangStrings = 47;          // language strings of an NSIS 3.10 language table
  private const int LangTableSize = 2 + 4 + 4 + 4 * LangStrings;
  private const int SectionSize = 6 * 4 + NsisStubPatcher.MaxStringLength * 2;
  private const int OpRet = 1;
  private const int ExtractFlags = 0x05000090; // overwrite on, MB_ABORTRETRYIGNORE | MB_ICONSTOP, IDIGNORE when silent
  private const int VarInstDir = 21, VarTemp = 25;

  // The language-table slots makensis fills for this script shape, and NSIS's English texts for them.
  private static readonly (int Index, string Text)[] EnglishStrings = [
    (0, "Nullsoft Install System"),
    (1, "\u0001\u8082 Setup"), // $(^Name) Setup
    (5, "Can't write: "),
    (17, "Error decompressing data! Corrupted installer?"),
    (21, "Extract: "),
    (22, "Extract: error writing to file "),
    (25, "Output folder: "),
    (29, "Skipped: "),
    (30, "Copy Details To Clipboard"),
    (36, "Error opening file for writing: \r\n\r\n\u0003\u8080\r\n\r\nClick Abort to stop the installation,\r\nRetry to try again, or\r\nIgnore to skip this file."),
    (37, "Custom"),
    (38, "Cancel"),
    (39, ": Installing"),
    (40, "Show &details"),
    (41, "Completed"),
    (42, "< &Back"),
    (43, "&Next >"),
    (44, "Click Next to continue."),
    (45, ": Completed"),
    (46, "&Close"),
  ];
  private const int LangName = 2;
  private const int LangFileError = 36;
  private const int LangCustom = 37;

  // The default pages (instfiles, then completed); negative values are language strings -(index+1).
  private static readonly int[] DefaultPages = [
    106, 3, -1, -1, -1, 0, -40, 0, -47, -45, -39, 0, -41, -42, 0, 0,
    -1, 5, -1, -1, -1, 10, -46, -43, -47, 0, -39, 0, 0, 0, 0, 0,
  ];

  /// <summary>The name of the stub NSIS ships for a compressor ("lzma_solid-x86-unicode", ...).</summary>
  public static string StubFileName(NsisCompression compression, bool solid) => compression switch {
    NsisCompression.Zlib => "zlib",
    NsisCompression.BZip2 => "bzip2",
    NsisCompression.Lzma => "lzma",
    _ => throw new ArgumentOutOfRangeException(nameof(compression), "NSIS stubs exist for zlib, bzip2 and LZMA."),
  } + (solid ? "_solid" : "") + "-x86-unicode";

  /// <summary>
  /// Reads the matching stub from an NSIS installation: <paramref name="nsisDirectory"/>, else the
  /// NSISDIR environment variable, else the usual install locations. Null when none is found.
  /// </summary>
  public static byte[]? LoadStub(NsisCompression compression, bool solid, string? nsisDirectory = null) {
    var name = StubFileName(compression, solid);
    foreach (var directory in CandidateDirectories(nsisDirectory)) {
      var path = Path.Combine(directory, "Stubs", name);
      if (File.Exists(path))
        return File.ReadAllBytes(path);
    }
    return null;
  }

  private static IEnumerable<string> CandidateDirectories(string? nsisDirectory) {
    if (!string.IsNullOrEmpty(nsisDirectory))
      yield return nsisDirectory;
    if (Environment.GetEnvironmentVariable("NSISDIR") is { Length: > 0 } fromEnvironment)
      yield return fromEnvironment;
    foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
      if (Environment.GetFolderPath(folder) is { Length: > 0 } root)
        yield return Path.Combine(root, "NSIS");
    yield return "/usr/share/nsis";
    yield return "/usr/local/share/nsis";
  }

  /// <summary>Writes an installer for <paramref name="files"/> using <paramref name="stub"/>.</summary>
  /// <exception cref="ArgumentException">The stub is not an NSIS 3 Unicode exehead, a path leaves the installation directory, or the install directory cannot be expressed.</exception>
  public static void Write(Stream output, ReadOnlySpan<byte> stub, IReadOnlyList<NsisInstallerFile> files, NsisInstallerOptions? options = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    options ??= new NsisInstallerOptions();
    _ = StubFileName(options.Compression, options.Solid);
    var image = NsisStubPatcher.Prepare(stub);

    var strings = new StringTable();
    // makensis's fixed prefix: $PROGRAMFILES and $COMMONFILES are shell codes that name these
    // strings by offset (registry value, then fallback), so they must sit at 1, 17 and 32.
    strings.Add([]);
    strings.Add(Literal("ProgramFilesDir"));
    strings.Add(Literal("CommonFilesDir"));
    strings.Add(Literal(@"C:\Program Files"));
    var programFiles = new[] { 2, 0x2081 }; // shell code: HKLM ...\CurrentVersion value at 1, fallback at 32
    strings.Add(programFiles);
    strings.Add([.. programFiles, .. Literal(@"\Common Files")]);
    strings.Add([2, 0x3491]);

    var installDirectory = Encode(options.InstallDirectory ?? @"$PROGRAMFILES\" + options.Name, programFiles);
    var installDirectoryPtr = strings.Add(installDirectory);
    var lastSeparator = installDirectory.LastIndexOf('\\');
    var autoAppend = lastSeparator >= 0 && lastSeparator < installDirectory.Length - 1 ? installDirectoryPtr + lastSeparator + 1 : 0;

    var entries = new List<int[]>();
    var blocks = new List<byte[]>();
    long dataPosition = 0;
    string? currentDirectory = null;
    long sizeKb = 0;
    foreach (var file in files) {
      var (directory, name) = Split(file.Path);
      if (directory != currentDirectory) {
        var path = strings.Add([3, 0x8080 | VarInstDir, .. directory.Length == 0 ? [] : Literal("\\" + directory)]);
        entries.Add([11, path, 1, 0, 0, 0, 0]); // EW_CREATEDIR, SetOutPath
        currentDirectory = directory;
      }
      var fileTime = (file.LastWriteTimeUtc ?? DateTime.UtcNow).ToFileTimeUtc();
      entries.Add([20, ExtractFlags, strings.Add(Literal(name)), checked((int)dataPosition), (int)fileTime, (int)(fileTime >> 32), -(LangFileError + 1)]);
      var block = options.Solid ? Frame(file.Data, false) : StoreOrCompress(file.Data, options.Compression);
      blocks.Add(block);
      dataPosition += block.Length;
      if (dataPosition > int.MaxValue)
        throw new ArgumentException("NSIS installers address their data with 31-bit offsets; the files are too large.", nameof(files));
      sizeKb += (file.Data.Length + 1023) / 1024;
    }
    var codeSize = entries.Count;
    entries.Add([OpRet, 0, 0, 0, 0, 0, 0]);

    var langTable = new int[LangStrings];
    foreach (var (index, text) in EnglishStrings)
      langTable[index] = strings.Add(Encode(text));
    langTable[LangName] = strings.Add(Literal(options.Name));
    var wininit = strings.Add([2, 0x2424, .. Literal(@"\wininit.ini")]); // $WINDIR\wininit.ini

    var header = BuildHeader(entries, codeSize, (int)Math.Min(sizeKb, int.MaxValue), strings, langTable, installDirectoryPtr, autoAppend, wininit);

    byte[] dataBlock;
    if (options.Solid) {
      using var whole = new MemoryStream();
      whole.Write(Frame(header, false));
      foreach (var b in blocks)
        whole.Write(b);
      dataBlock = Compress(whole.ToArray(), options.Compression);
    } else {
      using var parts = new MemoryStream();
      parts.Write(Frame(Compress(header, options.Compression), true));
      foreach (var b in blocks)
        parts.Write(b);
      dataBlock = parts.ToArray();
    }

    var firstHeader = new byte[NsisConstants.FirstHeaderSize];
    BinaryPrimitives.WriteInt32LittleEndian(firstHeader, FirstHeaderFlags);
    BinaryPrimitives.WriteUInt32LittleEndian(firstHeader.AsSpan(4), NsisConstants.FirstHeaderSignature);
    NsisConstants.Magic.CopyTo(firstHeader.AsSpan(8));
    BinaryPrimitives.WriteInt32LittleEndian(firstHeader.AsSpan(20), header.Length);
    BinaryPrimitives.WriteInt32LittleEndian(firstHeader.AsSpan(24), checked(firstHeader.Length + dataBlock.Length + 4));

    // The exehead checks a CRC-32 over the whole file but its first 512 bytes, the CRC excluded.
    var crc = new Crc32();
    crc.Update(image.AsSpan(512));
    crc.Update(firstHeader);
    crc.Update(dataBlock);
    Span<byte> crcBytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc.Value);

    output.Write(image);
    output.Write(firstHeader);
    output.Write(dataBlock);
    output.Write(crcBytes);
  }

  private static byte[] BuildHeader(List<int[]> entries, int codeSize, int sizeKb, StringTable strings, int[] langTable, int installDirectory, int autoAppend, int wininit) {
    var pagesOffset = HeaderSize;
    var sectionsOffset = pagesOffset + DefaultPages.Length * 4;
    var entriesOffset = sectionsOffset + SectionSize;
    var stringsOffset = entriesOffset + entries.Count * NsisConstants.EntrySize;
    var stringBytes = strings.ToBytes();
    var langOffset = stringsOffset + stringBytes.Length;
    var colorsOffset = langOffset + LangTableSize;

    var h = new byte[colorsOffset];
    var w = 0;
    void Int(int value) {
      BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(w), value);
      w += 4;
    }

    Int(128); // CH_FLAGS_NO_ROOT_DIR, makensis's default
    (int Offset, int Count)[] blockTable = [
      (pagesOffset, 2), (sectionsOffset, 1), (entriesOffset, entries.Count), (stringsOffset, 0),
      (langOffset, 1), (colorsOffset, 0), (0, 0), (0, 0),
    ];
    foreach (var (offset, count) in blockTable) {
      Int(offset);
      Int(count);
    }
    Int(0); Int(0); Int(0);     // install_reg_rootkey, install_reg_key_ptr, install_reg_value_ptr
    Int(-1); Int(-1); Int(-1);  // bg_color1, bg_color2, bg_textcolor
    Int(0); Int(0xFF00);        // lb_bg, lb_fg
    Int(LangTableSize);
    Int(-15);                   // license_bg: COLOR_BTNFACE
    for (var i = 0; i < 10; ++i)
      Int(-1);                  // .onInit ... .onRebootFailed: none
    for (var i = 0; i < 32; ++i)
      Int(0);                   // install_types
    Int(-(LangCustom + 1));     // install_types[32]: the "Custom" text
    Int(installDirectory);
    Int(autoAppend);
    Int(-1); Int(-1);           // str_uninstchild, str_uninstcmd: no uninstaller
    Int(wininit);
    if (w != HeaderSize)
      throw new InvalidOperationException("NSIS header layout mismatch.");

    foreach (var value in DefaultPages)
      Int(value);
    Int(0);          // name_ptr: invisible section
    Int(-1);         // install_types: all
    Int(1);          // SF_SELECTED
    Int(0);          // code
    Int(codeSize);   // code_size
    Int(sizeKb);
    w += NsisStubPatcher.MaxStringLength * 2; // name[] stays empty
    foreach (var entry in entries)
      foreach (var value in entry)
        Int(value);
    stringBytes.CopyTo(h.AsSpan(w));
    w += stringBytes.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(w), 0x0409); // LANGID: English (United States)
    w += 2;
    Int(0); Int(0); // dlg_offset, right-to-left
    foreach (var value in langTable)
      Int(value);
    return h;
  }

  private static (string Directory, string Name) Split(string path) {
    var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':')))
      throw new ArgumentException($"'{path}' is not a relative path inside the installation directory.", nameof(path));
    return (string.Join('\\', parts[..^1]), parts[^1]);
  }

  // A block's length word, the compressed flag in bit 31, then the bytes.
  private static byte[] Frame(byte[] data, bool compressed) {
    var block = new byte[4 + data.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)data.Length | (compressed ? NsisConstants.CompressedFlag : 0));
    data.CopyTo(block, 4);
    return block;
  }

  // makensis stores a block as is when compressing does not make it smaller.
  private static byte[] StoreOrCompress(byte[] data, NsisCompression compression) {
    var packed = Compress(data, compression);
    return packed.Length < data.Length ? Frame(packed, true) : Frame(data, false);
  }

  private static byte[] Compress(byte[] data, NsisCompression compression) {
    switch (compression) {
      case NsisCompression.Zlib:
        return DeflateCompressor.Compress(data, DeflateCompressionLevel.Best);
      case NsisCompression.BZip2: {
        using var ms = new MemoryStream();
        using (var bz = Bzip2Stream.OpenNsisEncoder(ms, leaveOpen: true))
          bz.Write(data);
        return ms.ToArray();
      }
      case NsisCompression.Lzma: {
        // makensis's properties: lc=3 lp=0 pb=2 and an 8 MB dictionary ("5D 00 00 80 00"), the
        // signature readers such as 7-Zip use to tell a non-solid LZMA block from Deflate.
        var encoder = new LzmaEncoder(1 << 23);
        using var ms = new MemoryStream();
        ms.Write(encoder.Properties);
        encoder.Encode(ms, data, writeEndMarker: true);
        return ms.ToArray();
      }
      default:
        throw new ArgumentOutOfRangeException(nameof(compression));
    }
  }

  private static int[] Literal(string text) {
    var units = new List<int>(text.Length);
    foreach (var c in text) {
      if (c is >= (char)1 and <= (char)4)
        units.Add(4); // NS_SKIP_CODE: the next unit is literal
      units.Add(c);
    }
    return [.. units];
  }

  // Script text with $PROGRAMFILES, $TEMP, $INSTDIR and $0, and codes already in place (\u0001-\u0003 + operand).
  private static int[] Encode(string text, int[]? programFiles = null) {
    var units = new List<int>();
    for (var i = 0; i < text.Length; ++i) {
      var c = text[i];
      if (c is >= (char)1 and <= (char)3 && i + 1 < text.Length) {
        units.Add(c);
        units.Add(text[++i]);
      } else if (c == '$' && Starts(text, i, "$PROGRAMFILES") && programFiles != null) {
        units.AddRange(programFiles);
        i += "$PROGRAMFILES".Length - 1;
      } else if (c == '$' && Starts(text, i, "$TEMP")) {
        units.Add(3);
        units.Add(0x8080 | VarTemp);
        i += "$TEMP".Length - 1;
      } else if (c == '$' && Starts(text, i, "$INSTDIR")) {
        throw new ArgumentException("The install directory cannot refer to $INSTDIR.");
      } else if (c == '$' && i + 1 < text.Length && text[i + 1] != '$' && char.IsLetter(text[i + 1])) {
        throw new ArgumentException($"Unsupported variable in '{text}': only $PROGRAMFILES and $TEMP are understood.");
      } else {
        if (c == '$' && i + 1 < text.Length && text[i + 1] == '$')
          ++i; // "$$" is a literal dollar sign
        if (c == 4)
          units.Add(4);
        units.Add(c);
      }
    }
    return [.. units];

    static bool Starts(string t, int at, string token)
      => string.CompareOrdinal(t, at, token, 0, token.Length) == 0
         && (at + token.Length == t.Length || !char.IsLetterOrDigit(t[at + token.Length]));
  }

  /// <summary>NSIS's string block: UTF-16 strings addressed by character offset, identical strings shared.</summary>
  private sealed class StringTable {
    private readonly List<ushort> _units = [];
    private readonly Dictionary<string, int> _offsets = [];

    public int Add(int[] units) {
      var key = string.Join(',', units);
      if (this._offsets.TryGetValue(key, out var existing))
        return existing;
      var offset = this._units.Count;
      foreach (var u in units)
        this._units.Add((ushort)u);
      this._units.Add(0);
      this._offsets[key] = offset;
      return offset;
    }

    public byte[] ToBytes() {
      var bytes = new byte[this._units.Count * 2];
      for (var i = 0; i < this._units.Count; ++i)
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), this._units[i]);
      return bytes;
    }
  }
}
