using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.InnoSetup;

/// <summary>One file or directory an installer writes below its installation directory.</summary>
/// <param name="Path">Relative path, '/' or '\' separated.</param>
/// <param name="Data">The file's bytes, or null for a directory.</param>
/// <param name="LastWriteTimeUtc">Time stamp the installed file gets; null means now.</param>
public sealed record InnoSetupInstallerFile(string Path, byte[]? Data, DateTime? LastWriteTimeUtc = null);

/// <summary>How <see cref="InnoSetupWriter"/> stores the files.</summary>
public enum InnoSetupWriterCompression {
  /// <summary>Stored.</summary>
  Stored,
  /// <summary>LZMA2, one chunk per file.</summary>
  Lzma2,
}

/// <summary>Settings of an installer written by <see cref="InnoSetupWriter"/>.</summary>
public sealed class InnoSetupInstallerOptions {
  /// <summary>Application name (the wizard's title and the default folder name).</summary>
  public string AppName { get; init; } = "Setup";
  /// <summary>Application version.</summary>
  public string AppVersion { get; init; } = "1.0";
  /// <summary>How the files are stored.</summary>
  public InnoSetupWriterCompression Compression { get; init; } = InnoSetupWriterCompression.Lzma2;
}

/// <summary>
/// The parts of an Inno Setup 6.0.x installation an installer is built from: the loader
/// (SetupLdr.e32), the setup program (Setup.e32), the default messages (Default.isl) and the
/// wizard bitmaps. They are native code and Inno Setup's own text, so they are read from the user's
/// installation and never shipped here.
/// </summary>
public sealed record InnoSetupStubs(byte[] SetupLdr, byte[] Setup, string DefaultMessages, byte[] WizardImage, byte[] WizardSmallImage) {

  /// <summary>
  /// Reads the stubs from <paramref name="directory"/>, else the INNOSETUPDIR environment variable,
  /// else the usual install locations ("Inno Setup 6" under Program Files). Null when none is found.
  /// </summary>
  public static InnoSetupStubs? Load(string? directory = null) {
    foreach (var dir in Candidates(directory)) {
      string P(string name) => Path.Combine(dir, name);
      if (!File.Exists(P("SetupLdr.e32")) || !File.Exists(P("Setup.e32")) || !File.Exists(P("Default.isl")))
        continue;
      return new(File.ReadAllBytes(P("SetupLdr.e32")), File.ReadAllBytes(P("Setup.e32")),
        File.ReadAllText(P("Default.isl"), Encoding.UTF8),
        File.Exists(P("WizModernImage.bmp")) ? File.ReadAllBytes(P("WizModernImage.bmp")) : [],
        File.Exists(P("WizModernSmallImage.bmp")) ? File.ReadAllBytes(P("WizModernSmallImage.bmp")) : []);
    }
    return null;
  }

  private static IEnumerable<string> Candidates(string? directory) {
    if (!string.IsNullOrEmpty(directory))
      yield return directory;
    if (Environment.GetEnvironmentVariable("INNOSETUPDIR") is { Length: > 0 } fromEnvironment)
      yield return fromEnvironment;
    foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
      if (Environment.GetFolderPath(folder) is { Length: > 0 } root)
        yield return Path.Combine(root, "Inno Setup 6");
  }
}

/// <summary>
/// Writes Inno Setup 6.0.x installers: the user's SetupLdr.e32 followed by the file data
/// (setup-1), the setup headers (setup-0) and the setup program, with the loader's offset table
/// resource pointing at them. The installer is not uninstallable, runs without elevation and
/// installs exactly the given files and directories below its installation directory.
/// </summary>
/// <remarks>
/// <para>
/// Layouts converted from Inno Setup 6.0.5 (Inno Setup License, see THIRD-PARTY-NOTICE.InnoSetup.txt):
/// Struct.pas (records, packed, strings as length-prefixed UTF-16 ahead of each record's fixed
/// part), SetupEnt.pas (that serialisation), Compress.pas (compressed blocks: a CRC-checked header,
/// then data in 4 KiB pieces each behind its CRC-32; the call-instruction transform), Compile.pas
/// (write order, defaults, the offset table and the PE header fields), Msgs.pas and MsgIDs.pas
/// (the message table).
/// </para>
/// <para>The setup headers are stored, not LZMA-compressed; Setup reads either.</para>
/// </remarks>
public static class InnoSetupWriter {
  private const int OffsetTableResourceId = 11111;
  private const int OffsetTableSize = 44;
  private static readonly byte[] OffsetTableId = [.. "rDlPtS"u8, 0xCD, 0xE6, 0xD7, (byte)'{', 0x0B, (byte)'*'];
  private const string SetupId = "Inno Setup Setup Data (6.0.0) (u)";
  private const string MessagesId = "Inno Setup Messages (6.0.0) (u)";
  private static readonly byte[] ChunkId = "zlb\x1a"u8.ToArray();

  // TSetupHeader.Options as Compile.pas sets them by default: DisableStartupPrompt, CreateAppDir,
  // WindowShowCaption, WindowResizable, WindowStartMaximized, UsePreviousAppDir, UsePreviousGroup,
  // UpdateUninstallLogAppName, UsePreviousSetupType, AlwaysShowComponentsList, FlatComponentsList,
  // ShowComponentSizes, UsePreviousTasks, AllowUNCPath, UsePreviousUserInfo, RestartIfNeededByRun,
  // AllowCancelDuringInstall, WizardImageStretch, AppendDefaultDirName, AppendDefaultGroupName,
  // UsePreviousLanguage, DisableWelcomePage, CloseApplications, RestartApplications,
  // AllowNetworkDrive, UsePreviousPrivileges.
  private static readonly int[] DefaultOptionBits = [0, 1, 6, 7, 8, 13, 15, 16, 17, 19, 20, 21, 22, 26, 28, 30, 32, 33, 34, 35, 39, 40, 41, 42, 43, 46];

  /// <summary>Writes an installer for <paramref name="files"/> built on <paramref name="stubs"/>.</summary>
  /// <exception cref="NotSupportedException">The stubs are not Inno Setup 6.0.x Unicode ones, or Default.isl lacks a message.</exception>
  /// <exception cref="ArgumentException">A path is empty, leaves the installation directory or holds a character Windows forbids.</exception>
  public static void Write(Stream output, InnoSetupStubs stubs, IReadOnlyList<InnoSetupInstallerFile> files, InnoSetupInstallerOptions? options = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stubs);
    ArgumentNullException.ThrowIfNull(files);
    options ??= new InnoSetupInstallerOptions();
    var entries = Prepare(files);

    var loader = (byte[])stubs.SetupLdr.Clone();
    var tableOffset = InnoLoader.FindRcData(new MemoryStream(loader, false), OffsetTableResourceId);
    // the loader ships the table resource zeroed; the compiler fills it in
    if (tableOffset is not { } table || table + OffsetTableSize > loader.Length
        || (loader.AsSpan((int)table, OffsetTableSize).ContainsAnyExcept((byte)0) && !loader.AsSpan((int)table, OffsetTableId.Length).SequenceEqual(OffsetTableId))
        || IndexOf(loader, Encoding.ASCII.GetBytes(SetupId)) < 0)
      throw new NotSupportedException("SetupLdr.e32 is not the loader of Inno Setup 6.0.x (Unicode) with an offset table resource.");
    if (IndexOf(stubs.Setup, Encoding.ASCII.GetBytes(SetupId)) < 0 || IndexOf(stubs.Setup, Encoding.ASCII.GetBytes(MessagesId)) < 0)
      throw new NotSupportedException("Setup.e32 is not from Inno Setup 6.0.x (Unicode); only that setup data layout is written.");
    var setup = (byte[])stubs.Setup.Clone();
    UpdatePeHeaderFields(loader);
    UpdatePeHeaderFields(setup);

    var compressed = options.Compression == InnoSetupWriterCompression.Lzma2;

    // setup-1: one chunk per file
    var data = new MemoryStream();
    var locations = new List<byte[]>();
    foreach (var e in entries) {
      if (e.Data is null) continue;
      var start = data.Position;
      data.Write(ChunkId);
      var chunkCompressed = compressed && e.Data.Length > 0;
      if (chunkCompressed) {
        var encoder = new Lzma2Encoder(DictionarySize(e.Data.Length));
        data.WriteByte(encoder.DictionarySizeByte);
        encoder.Encode(data, e.Data);
      } else
        data.Write(e.Data);
      locations.Add(Location(start, data.Position - start - ChunkId.Length, e, chunkCompressed));
    }

    var header0 = SetupZero(stubs, entries, locations, options, compressed);
    var exe = SetupProgram(setup, out var exeCrc);

    var offset1 = (long)loader.Length;
    var offset0 = offset1 + data.Length;
    var offsetExe = offset0 + header0.Length;
    var total = offsetExe + exe.Length;
    if (total > uint.MaxValue)
      throw new NotSupportedException("An Inno Setup 6.0 installer is limited to 4 GiB.");

    var t = loader.AsSpan((int)table, OffsetTableSize);
    OffsetTableId.CopyTo(t);
    BinaryPrimitives.WriteUInt32LittleEndian(t[12..], 1); // table version
    BinaryPrimitives.WriteUInt32LittleEndian(t[16..], (uint)total);
    BinaryPrimitives.WriteUInt32LittleEndian(t[20..], (uint)offsetExe);
    BinaryPrimitives.WriteUInt32LittleEndian(t[24..], (uint)setup.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(t[28..], exeCrc);
    BinaryPrimitives.WriteUInt32LittleEndian(t[32..], (uint)offset0);
    BinaryPrimitives.WriteUInt32LittleEndian(t[36..], (uint)offset1);
    BinaryPrimitives.WriteUInt32LittleEndian(t[40..], Crc32.Compute(t[..40]));

    output.Write(loader);
    data.Position = 0;
    data.CopyTo(output);
    output.Write(header0);
    output.Write(exe);
  }

  private static int DictionarySize(int length) => Math.Clamp((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 1)), 1 << 16, 1 << 26);

  private sealed record Entry(string DestName, byte[]? Data, DateTime Time);

  /// <summary>Directories first (in input order), then files; every name in Inno's {app}\ form with braces doubled.</summary>
  private static List<Entry> Prepare(IReadOnlyList<InnoSetupInstallerFile> files) {
    var now = DateTime.UtcNow;
    var result = new List<Entry>();
    foreach (var f in files) {
      var parts = f.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        throw new ArgumentException($"Inno Setup entry '{f.Path}' has no name.", nameof(files));
      foreach (var part in parts)
        if (part is "." or ".." || part.IndexOfAny(['*', '?', '"', '<', '>', '|', ':']) >= 0 || part.Any(char.IsControl) || part.EndsWith(' ') || part.EndsWith('.'))
          throw new ArgumentException($"Inno Setup entry '{f.Path}' is not a valid Windows path below the installation directory.", nameof(files));
      var name = "{app}\\" + string.Join('\\', parts).Replace("{", "{{", StringComparison.Ordinal);
      result.Add(new(name, f.Data, (f.LastWriteTimeUtc ?? now).ToUniversalTime()));
    }
    return [.. result.Where(e => e.Data is null), .. result.Where(e => e.Data is not null)];
  }

  /// <summary>A TSetupFileLocationEntry (74 bytes).</summary>
  private static byte[] Location(long start, long chunkSize, Entry e, bool chunkCompressed) {
    var b = new byte[74];
    var s = b.AsSpan();
    BinaryPrimitives.WriteInt32LittleEndian(s[8..], checked((int)start)); // FirstSlice, LastSlice 0
    BinaryPrimitives.WriteInt64LittleEndian(s[12..], 0); // ChunkSuboffset
    BinaryPrimitives.WriteInt64LittleEndian(s[20..], e.Data!.Length);
    BinaryPrimitives.WriteInt64LittleEndian(s[28..], chunkSize);
    SHA1.HashData(e.Data, s[36..56]);
    BinaryPrimitives.WriteInt64LittleEndian(s[56..], e.Time.ToFileTimeUtc());
    // FileVersionMS/LS stay 0; flags: VersionInfoNotValid, TimeStampInUTC, ChunkCompressed
    var flags = (1 << 1) | (1 << 2) | (chunkCompressed ? 1 << 7 : 0);
    BinaryPrimitives.WriteUInt16LittleEndian(s[72..], (ushort)flags);
    return b;
  }

  /// <summary>setup-0: the setup id, the headers block and the file locations block.</summary>
  private static byte[] SetupZero(InnoSetupStubs stubs, List<Entry> entries, List<byte[]> locations, InnoSetupInstallerOptions options, bool compressed) {
    var isl = IslFile.Parse(stubs.DefaultMessages);
    var dirs = entries.Where(e => e.Data is null).ToList();
    var files = entries.Where(e => e.Data is not null).ToList();
    var appName = options.AppName.Replace("{", "{{", StringComparison.Ordinal);

    var w = new RecordWriter();
    // TSetupHeader strings
    w.Strings(appName, "", appName, "", "", "", "", "", "", options.AppVersion.Replace("{", "{{", StringComparison.Ordinal),
      "{autopf}\\" + appName, "(Default)", "", "{app}", "", "", "", "{sysuserinfoname}", "{sysuserinfoorg}", "",
      "", "", "", "", "yes", "no", "*.exe,*.dll,*.chm", "", "no", "no");
    w.Ansi("", "", "", ""); // LicenseText, InfoBeforeText, InfoAfterText, CompiledCodeText
    // counts: language, custom message, permission, type, component, task, dir, file, file location,
    // icon, ini, registry, install delete, uninstall delete, run, uninstall run
    foreach (var n in (ReadOnlySpan<int>)[1, isl.CustomMessages.Count, 0, 0, 0, 0, dirs.Count, files.Count, files.Count, 0, 0, 0, 0, 0, 0, 0])
      w.Int(n);
    w.Version(0x06000000); // MinVersion: Windows 6.0 (NT)
    w.Version(0); // OnlyBelowVersion
    w.Int(0x00FF0000); // BackColor (clBlue)
    w.Int(0); // BackColor2 (clBlack)
    w.Byte(0); // WizardStyle: classic
    w.Int(100); // WizardSizePercentX
    w.Int(100); // WizardSizePercentY
    w.Byte(0); // WizardImageAlphaFormat: ignored
    w.Zero(20 + 8); // PasswordHash, PasswordSalt
    w.Long(0); // ExtraDiskSpaceRequired
    w.Int(1); // SlicesPerDisk
    w.Byte(0); // UninstallLogMode: append
    w.Byte(0); // DirExistsWarning: auto
    w.Byte(3); // PrivilegesRequired: lowest
    w.Byte(0); // PrivilegesRequiredOverridesAllowed: none
    w.Byte(0); // ShowLanguageDialog: yes
    w.Byte(0); // LanguageDetectionMethod: UI language
    w.Byte((byte)(compressed ? 4 : 0)); // CompressMethod: LZMA2 or stored
    w.Byte(0); // ArchitecturesAllowed: any
    w.Byte(0); // ArchitecturesInstallIn64BitMode: none
    w.Byte(0); // DisableDirPage: auto
    w.Byte(0); // DisableProgramGroupPage: auto
    w.Long(0); // UninstallDisplaySize
    ulong optionBits = 0;
    foreach (var bit in DefaultOptionBits) optionBits |= 1UL << bit;
    for (var i = 0; i < 6; ++i) w.Byte((byte)(optionBits >> (8 * i)));

    // TSetupLanguageEntry
    var lang = isl.LangOptions;
    w.Strings("default", lang.GetValueOrDefault("LanguageName", "English"), lang.GetValueOrDefault("DialogFontName", "Tahoma"),
      lang.GetValueOrDefault("TitleFontName", "Arial"), lang.GetValueOrDefault("WelcomeFontName", "Verdana"),
      lang.GetValueOrDefault("CopyrightFontName", "Arial"));
    w.AnsiBytes(Messages(isl));
    w.Ansi("", "", "");
    w.Int((int)IslNumber(lang.GetValueOrDefault("LanguageID", "$0409")));
    w.Int((int)IslNumber(lang.GetValueOrDefault("DialogFontSize", "8")));
    w.Int((int)IslNumber(lang.GetValueOrDefault("TitleFontSize", "29")));
    w.Int((int)IslNumber(lang.GetValueOrDefault("WelcomeFontSize", "12")));
    w.Int((int)IslNumber(lang.GetValueOrDefault("CopyrightFontSize", "8")));
    w.Byte((byte)(lang.GetValueOrDefault("RightToLeft", "no").Equals("yes", StringComparison.OrdinalIgnoreCase) ? 1 : 0));

    // TSetupCustomMessageEntry
    foreach (var (name, value) in isl.CustomMessages) {
      w.Strings(name, value);
      w.Int(0); // LangIndex
    }

    // TSetupDirEntry
    foreach (var d in dirs) {
      w.Strings(d.DestName, "", "", "", "", "", "");
      w.Int(0); // Attribs
      w.Version(0x06000000);
      w.Version(0);
      w.Short(-1); // PermissionsEntry
      w.Byte(0); // Options
    }

    // TSetupFileEntry
    for (var i = 0; i < files.Count; ++i) {
      w.Strings("", files[i].DestName, "", "", "", "", "", "", "", "");
      w.Version(0x06000000);
      w.Version(0);
      w.Int(i); // LocationEntry
      w.Int(0); // Attribs
      w.Long(0); // ExternalSize
      w.Short(-1); // PermissionsEntry
      w.Int(0); // Options
      w.Byte(0); // FileType: user file
    }

    // wizard images, then the small ones
    foreach (var image in (byte[][])[stubs.WizardImage, stubs.WizardSmallImage]) {
      if (image.Length == 0) w.Int(0);
      else {
        w.Int(1);
        w.Int(image.Length);
        w.Raw(image);
      }
    }

    var output = new MemoryStream();
    var id = new byte[64];
    Encoding.ASCII.GetBytes(SetupId, id);
    output.Write(id);
    StoredBlock(output, w.ToArray());
    StoredBlock(output, [.. locations.SelectMany(l => l)]);
    return output.ToArray();
  }

  /// <summary>The message table: id, header and every message in TSetupMessageID order as zero-terminated UTF-16.</summary>
  private static byte[] Messages(IslFile isl) {
    var body = new MemoryStream();
    foreach (var name in InnoMessageIds.Names) {
      if (!isl.Messages.TryGetValue(name, out var text))
        throw new NotSupportedException($"Default.isl has no message '{name}'; it is not the one of Inno Setup 6.0.x.");
      body.Write(Encoding.Unicode.GetBytes(text + "\0"));
    }
    var messages = body.ToArray();
    var total = 64 + 16 + messages.Length;
    var result = new byte[total];
    Encoding.ASCII.GetBytes(MessagesId, result);
    var h = result.AsSpan(64);
    BinaryPrimitives.WriteInt32LittleEndian(h, InnoMessageIds.Names.Length);
    BinaryPrimitives.WriteInt32LittleEndian(h[4..], total);
    BinaryPrimitives.WriteInt32LittleEndian(h[8..], ~total);
    BinaryPrimitives.WriteUInt32LittleEndian(h[12..], Crc32.Compute(messages));
    messages.CopyTo(result, 80);
    return result;
  }

  /// <summary>The setup program as SetupLdr extracts it: call-transformed, then an LZMA1 block.</summary>
  private static byte[] SetupProgram(byte[] setup, out uint crc) {
    crc = Crc32.Compute(setup);
    var transformed = (byte[])setup.Clone();
    TransformCallInstructions(transformed, 0);
    var encoder = new LzmaEncoder(1 << 22);
    var packed = new MemoryStream();
    packed.Write(encoder.Properties);
    encoder.Encode(packed, transformed, writeEndMarker: false);
    var output = new MemoryStream();
    CompressedBlock(output, packed.ToArray(), compressed: true);
    return output.ToArray();
  }

  /// <summary>
  /// The x86 CALL/JMP transform (version 3) in its encoding direction: at each E8/E9 whose operand's
  /// top byte is 00 or FF, the low three bytes become relative to the buffer start, and the top byte
  /// is inverted when bit 23 of the operand is set.
  /// </summary>
  internal static void TransformCallInstructions(Span<byte> buffer, uint addressOffset) {
    if (buffer.Length < 5) return;
    var size = buffer.Length - 4;
    var i = 0;
    while (i < size) {
      if (buffer[i] is 0xE8 or 0xE9) {
        ++i;
        if (buffer[i + 3] is 0x00 or 0xFF) {
          var address = (addressOffset + (uint)i + 4) & 0xFFFFFF;
          var relative = (uint)(buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16));
          if ((relative & 0x800000) != 0)
            buffer[i + 3] = (byte)~buffer[i + 3];
          relative += address;
          buffer[i] = (byte)relative;
          buffer[i + 1] = (byte)(relative >> 8);
          buffer[i + 2] = (byte)(relative >> 16);
        }
        i += 4;
      } else
        ++i;
    }
  }

  private static void StoredBlock(Stream output, byte[] data) => CompressedBlock(output, data, compressed: false);

  /// <summary>
  /// A compressed block: CRC-32 of the five header bytes, the stored size (data plus its CRCs) and
  /// the compressed flag, then the data in 4 KiB pieces, each behind its CRC-32.
  /// </summary>
  private static void CompressedBlock(Stream output, byte[] data, bool compressed) {
    var pieces = (data.Length + 4095) / 4096;
    Span<byte> header = stackalloc byte[9];
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(data.Length + 4 * pieces));
    header[8] = (byte)(compressed ? 1 : 0);
    BinaryPrimitives.WriteUInt32LittleEndian(header, Crc32.Compute(header[4..]));
    output.Write(header);
    Span<byte> crc = stackalloc byte[4];
    for (var p = 0; p < data.Length; p += 4096) {
      var piece = data.AsSpan(p, Math.Min(4096, data.Length - p));
      BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(piece));
      output.Write(crc);
      output.Write(piece);
    }
  }

  /// <summary>Image version 6.0 and the TS-aware, NX and dynamic-base characteristics, as the compiler sets them.</summary>
  private static void UpdatePeHeaderFields(byte[] image) {
    var pe = image.Length >= 0x40 ? BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C)) : -1;
    if (pe < 0 || pe + 24 + 0x48 > image.Length || BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe)) != 0x00004550
        || BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20)) != 224 || BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 24)) != 0x10B)
      throw new NotSupportedException("An Inno Setup stub is not a 32-bit PE image.");
    var opt = pe + 24;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 0x2C), 6);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 0x2E), 0);
    var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(opt + 0x46));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 0x46), (ushort)(characteristics | 0x8000 | 0x0100 | 0x0040));
  }

  private static long IslNumber(string s) {
    s = s.Trim();
    return s.StartsWith('$') ? Convert.ToInt64(s[1..], 16) : long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

  /// <summary>Packed-record serialisation: strings first (Int32 byte length, then the bytes), then the fixed fields.</summary>
  private sealed class RecordWriter {
    private readonly MemoryStream _ms = new();

    public void Strings(params string[] values) {
      foreach (var v in values) {
        var bytes = Encoding.Unicode.GetBytes(v);
        this.Int(bytes.Length);
        this._ms.Write(bytes);
      }
    }

    public void Ansi(params string[] values) {
      foreach (var v in values) this.AnsiBytes(Encoding.Latin1.GetBytes(v));
    }

    public void AnsiBytes(byte[] value) {
      this.Int(value.Length);
      this._ms.Write(value);
    }

    public void Int(int v) {
      Span<byte> b = stackalloc byte[4];
      BinaryPrimitives.WriteInt32LittleEndian(b, v);
      this._ms.Write(b);
    }

    public void Long(long v) {
      Span<byte> b = stackalloc byte[8];
      BinaryPrimitives.WriteInt64LittleEndian(b, v);
      this._ms.Write(b);
    }

    public void Short(short v) {
      Span<byte> b = stackalloc byte[2];
      BinaryPrimitives.WriteInt16LittleEndian(b, v);
      this._ms.Write(b);
    }

    public void Byte(byte v) => this._ms.WriteByte(v);
    public void Zero(int n) => this._ms.Write(new byte[n]);
    public void Raw(byte[] bytes) => this._ms.Write(bytes);

    /// <summary>TSetupVersionData: WinVersion 0, NTVersion, NTServicePack 0.</summary>
    public void Version(int ntVersion) {
      this.Int(0);
      this.Int(ntVersion);
      this.Short(0);
    }

    public byte[] ToArray() => this._ms.ToArray();
  }

  /// <summary>The [LangOptions], [Messages] and [CustomMessages] of an .isl file, read the way the compiler does.</summary>
  private sealed class IslFile {
    public Dictionary<string, string> LangOptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Messages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, string Value)> CustomMessages { get; } = [];

    public static IslFile Parse(string text) {
      var isl = new IslFile();
      var section = "";
      foreach (var raw in text.Split('\n')) {
        var line = raw.Trim().TrimStart('\uFEFF').Trim();
        if (line.Length == 0 || line[0] == ';') continue;
        if (line[0] == '[') {
          section = line.Trim('[', ']').Trim();
          continue;
        }
        var eq = line.IndexOf('=');
        if (eq < 0) continue;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..];
        switch (section.ToLowerInvariant()) {
          case "langoptions": isl.LangOptions[key] = value; break;
          case "messages": isl.Messages[key] = value.Replace("%n", "\r\n", StringComparison.Ordinal); break;
          case "custommessages":
            isl.CustomMessages.RemoveAll(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            isl.CustomMessages.Add((key, value.Replace("%n", "\r\n", StringComparison.Ordinal)));
            break;
        }
      }
      return isl;
    }
  }
}
