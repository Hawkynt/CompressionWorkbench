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
  /// <summary>
  /// The default installation directory, written as is (Inno constants allowed, a literal brace
  /// doubled); null means <c>{autopf}\</c> and the application name, as the compiler's default.
  /// </summary>
  public string? DefaultDirName { get; init; }
}

/// <summary>
/// The parts of an Inno Setup 6.x/7.x installation an installer is built from: the loader
/// (SetupLdr.e32), the setup program (Setup.e32), the default messages (Default.isl) and the
/// wizard bitmaps. They are native code and Inno Setup's own text, so they are read from the user's
/// installation and never shipped here.
/// </summary>
public sealed record InnoSetupStubs(byte[] SetupLdr, byte[] Setup, string DefaultMessages, byte[] WizardImage, byte[] WizardSmallImage) {

  /// <summary>
  /// Reads the stubs from <paramref name="directory"/>, else the INNOSETUPDIR environment variable,
  /// else the usual install locations ("Inno Setup 7", then "Inno Setup 6" under Program Files).
  /// Null when none is found.
  /// </summary>
  public static InnoSetupStubs? Load(string? directory = null) {
    foreach (var dir in Candidates(directory)) {
      string P(string name) => Path.Combine(dir, name);
      if (!File.Exists(P("SetupLdr.e32")) || !File.Exists(P("Setup.e32")) || !File.Exists(P("Default.isl")))
        continue;
      byte[] Image(params string[] names) {
        foreach (var n in names)
          if (File.Exists(P(n))) return File.ReadAllBytes(P(n));
        return [];
      }
      return new(File.ReadAllBytes(P("SetupLdr.e32")), File.ReadAllBytes(P("Setup.e32")),
        File.ReadAllText(P("Default.isl"), Encoding.UTF8),
        Image("WizClassicImage.bmp", "WizModernImage.bmp"),
        Image("WizClassicSmallImage.bmp", "WizModernSmallImage.bmp"));
    }
    return null;
  }

  private static IEnumerable<string> Candidates(string? directory) {
    if (!string.IsNullOrEmpty(directory))
      yield return directory;
    if (Environment.GetEnvironmentVariable("INNOSETUPDIR") is { Length: > 0 } fromEnvironment)
      yield return fromEnvironment;
    foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
      if (Environment.GetFolderPath(folder) is { Length: > 0 } root) {
        yield return Path.Combine(root, "Inno Setup 7");
        yield return Path.Combine(root, "Inno Setup 6");
      }
  }
}

/// <summary>
/// Writes Inno Setup installers for the setup data layouts of Inno Setup 6.0 to 7.1.1, whichever
/// the given stubs speak: the user's SetupLdr.e32 followed by the file data (setup-1), the setup
/// headers (setup-0) and the setup program, with the loader's offset table resource pointing at
/// them. The installer is not uninstallable, runs without elevation and installs exactly the given
/// files and directories below its installation directory.
/// </summary>
/// <remarks>
/// <para>
/// Layouts converted from Inno Setup 6.0.5 to 7.1.1 (Inno Setup License, see
/// THIRD-PARTY-NOTICE.InnoSetup.txt): Struct.pas / Shared.Struct.pas of every release with its own
/// setup data id (records, packed, strings as length-prefixed UTF-16 ahead of each record's fixed
/// part), SetupEnt.pas (that serialisation), Compress.pas / Compression.Base.pas (compressed blocks:
/// a CRC-checked header, then data in 4 KiB pieces each behind its CRC-32; the call-instruction
/// transform), Compile.pas / Compiler.SetupCompiler.pas (write order, defaults, the offset table and
/// the PE header fields), and the message identifier units (the message table).
/// </para>
/// <para>The setup headers are stored, not LZMA-compressed; Setup reads either.</para>
/// </remarks>
public static class InnoSetupWriter {
  private const int OffsetTableResourceId = 11111;
  private static readonly byte[] OffsetTableId = [.. "rDlPtS"u8, 0xCD, 0xE6, 0xD7, (byte)'{', 0x0B, (byte)'*'];
  private static readonly byte[] ChunkId = "zlb\x1a"u8.ToArray();

  /// <summary>A setup data layout: one per setup data id, in release order.</summary>
  internal enum Layout { V600, V610, V630, V6401, V642, V643, V650, V652, V660, V661, V670, V7003, V711 }

  private static readonly Dictionary<string, Layout> Layouts = new(StringComparer.Ordinal) {
    ["6.0.0"] = Layout.V600, ["6.1.0"] = Layout.V610, ["6.3.0"] = Layout.V630, ["6.4.0.1"] = Layout.V6401,
    ["6.4.2"] = Layout.V642, ["6.4.3"] = Layout.V643, ["6.5.0"] = Layout.V650, ["6.5.2"] = Layout.V652,
    ["6.6.0"] = Layout.V660, ["6.6.1"] = Layout.V661, ["6.7.0"] = Layout.V670, ["7.0.0.3"] = Layout.V7003,
    ["7.1.1"] = Layout.V711,
  };

  /// <summary>The ids and the layout the setup program expects.</summary>
  internal sealed record Format(Layout Layout, byte[] SetupId, byte[] MessagesId) {
    public bool AtLeast(Layout l) => this.Layout >= l;
  }

  // TSetupHeader.Options the compiler sets by default, as bit numbers of each layout's option set.
  // 6.0-6.3: DisableStartupPrompt, CreateAppDir, WindowShowCaption, WindowResizable,
  // WindowStartMaximized, UsePreviousAppDir, UsePreviousGroup, UpdateUninstallLogAppName,
  // UsePreviousSetupType, AlwaysShowComponentsList, FlatComponentsList, ShowComponentSizes,
  // UsePreviousTasks, AllowUNCPath, UsePreviousUserInfo, RestartIfNeededByRun,
  // AllowCancelDuringInstall, WizardImageStretch, AppendDefaultDirName, AppendDefaultGroupName,
  // UsePreviousLanguage, DisableWelcomePage, CloseApplications, RestartApplications,
  // AllowNetworkDrive, UsePreviousPrivileges; later layouts the same flags where they still exist
  // (6.6 adds WizardModern-era BorderStyled, 6.7 moves the UsePrevious* flags into strings).
  private static readonly int[] OptionBits60 = [0, 1, 6, 7, 8, 13, 15, 16, 17, 19, 20, 21, 22, 26, 28, 30, 32, 33, 34, 35, 39, 40, 41, 42, 43, 46];
  private static readonly int[] OptionBits64 = [0, 1, 9, 10, 11, 12, 14, 15, 16, 17, 21, 23, 25, 27, 28, 29, 30, 34, 35, 36, 37, 38, 41];
  private static readonly int[] OptionBits65 = [0, 1, 9, 10, 11, 12, 14, 15, 16, 17, 21, 23, 25, 27, 28, 29, 30, 33, 34, 35, 36, 37, 40];
  private static readonly int[] OptionBits66 = [.. OptionBits65, 44];
  private static readonly int[] OptionBits67 = [0, 1, 9, 11, 12, 13, 17, 20, 22, 23, 24, 25, 28, 29, 30, 31, 32, 35, 39, 40];

  /// <summary>Writes an installer for <paramref name="files"/> built on <paramref name="stubs"/>.</summary>
  /// <exception cref="NotSupportedException">The stubs are not Inno Setup 6.0–7.1.1 Unicode ones, or Default.isl lacks a message.</exception>
  /// <exception cref="ArgumentException">A path is empty, leaves the installation directory or holds a character Windows forbids.</exception>
  public static void Write(Stream output, InnoSetupStubs stubs, IReadOnlyList<InnoSetupInstallerFile> files, InnoSetupInstallerOptions? options = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stubs);
    ArgumentNullException.ThrowIfNull(files);
    options ??= new InnoSetupInstallerOptions();
    var entries = Prepare(files);

    var loader = (byte[])stubs.SetupLdr.Clone();
    var format = Identify(stubs);
    var tableSize = format.AtLeast(Layout.V650) ? 64 : 44;
    var tableOffset = InnoLoader.FindRcData(new MemoryStream(loader, false), OffsetTableResourceId);
    // the loader ships the table resource as a placeholder (zeros, 'x' from 6.5); the compiler fills it in
    if (tableOffset is not { } table || table + tableSize > loader.Length
        || (loader.AsSpan((int)table, tableSize).ContainsAnyExcept(loader[(int)table]) && !loader.AsSpan((int)table, OffsetTableId.Length).SequenceEqual(OffsetTableId))
        || loader.AsSpan().IndexOf(format.SetupId.AsSpan().TrimEnd((byte)0)) < 0)
      throw new NotSupportedException("SetupLdr.e32 is not the loader that belongs to this Setup.e32, or has no offset table resource.");
    var setup = (byte[])stubs.Setup.Clone();
    UpdatePeHeaderFields(loader);
    UpdatePeHeaderFields(setup);

    var compressed = options.Compression == InnoSetupWriterCompression.Lzma2;
    var (data, locations) = SetupOne(format, entries, compressed);
    var header0 = SetupZero(format, stubs, entries, locations, options, compressed);
    var exe = SetupProgram(format, setup, out var exeCrc);

    var offset1 = (long)loader.Length;
    var offset0 = offset1 + data.Length;
    var offsetExe = offset0 + header0.Length;
    var total = offsetExe + exe.Length;
    if (!format.AtLeast(Layout.V650) && total > uint.MaxValue)
      throw new NotSupportedException("An installer of this Inno Setup version is limited to 4 GiB.");

    var t = loader.AsSpan((int)table, tableSize);
    t.Clear();
    OffsetTableId.CopyTo(t);
    if (format.AtLeast(Layout.V650)) {
      // TSetupLdrOffsetTable version 2 (aligned record): 64-bit offsets, padding before the CRC
      BinaryPrimitives.WriteUInt32LittleEndian(t[12..], 2);
      BinaryPrimitives.WriteInt64LittleEndian(t[16..], total);
      BinaryPrimitives.WriteInt64LittleEndian(t[24..], offsetExe);
      BinaryPrimitives.WriteUInt32LittleEndian(t[32..], (uint)setup.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(t[36..], exeCrc);
      BinaryPrimitives.WriteInt64LittleEndian(t[40..], offset0);
      BinaryPrimitives.WriteInt64LittleEndian(t[48..], offset1);
      BinaryPrimitives.WriteUInt32LittleEndian(t[60..], Crc32.Compute(t[..60]));
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(t[12..], 1);
      BinaryPrimitives.WriteUInt32LittleEndian(t[16..], (uint)total);
      BinaryPrimitives.WriteUInt32LittleEndian(t[20..], (uint)offsetExe);
      BinaryPrimitives.WriteUInt32LittleEndian(t[24..], (uint)setup.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(t[28..], exeCrc);
      BinaryPrimitives.WriteUInt32LittleEndian(t[32..], (uint)offset0);
      BinaryPrimitives.WriteUInt32LittleEndian(t[36..], (uint)offset1);
      BinaryPrimitives.WriteUInt32LittleEndian(t[40..], Crc32.Compute(t[..40]));
    }

    output.Write(loader);
    output.Write(data);
    output.Write(header0);
    output.Write(exe);
  }

  /// <summary>
  /// The layout of <paramref name="stubs"/>, from the setup data and message table ids stored in
  /// Setup.e32 (64-byte, zero-padded strings the setup program compares against).
  /// </summary>
  internal static Format Identify(InnoSetupStubs stubs) {
    var setupId = FindId(stubs.Setup, "Inno Setup Setup Data (");
    var messagesId = FindId(stubs.Setup, "Inno Setup Messages (");
    if (setupId == null || messagesId == null)
      throw new NotSupportedException("Setup.e32 holds no Inno Setup setup data id; it is not an Inno Setup setup program.");
    var text = Encoding.ASCII.GetString(setupId).TrimEnd('\0');
    var version = text["Inno Setup Setup Data (".Length..text.IndexOf(')')];
    if (!Layouts.TryGetValue(version, out var layout) || (layout < Layout.V630 && !text.EndsWith(" (u)", StringComparison.Ordinal)))
      throw new NotSupportedException($"Setup.e32 speaks setup data '{text}'; Inno Setup 6.0 to 7.1.1 (Unicode) are written.");
    return new(layout, setupId, messagesId);
  }

  /// <summary>The 64-byte id beginning with <paramref name="prefix"/>, padded with zeros as stored.</summary>
  private static byte[]? FindId(byte[] image, string prefix) {
    var at = image.AsSpan().IndexOf(Encoding.ASCII.GetBytes(prefix));
    if (at < 0) return null;
    var end = image.AsSpan(at, Math.Min(64, image.Length - at)).IndexOf((byte)0);
    if (end < 0) return null;
    var id = new byte[64];
    image.AsSpan(at, end).CopyTo(id);
    return id;
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

  /// <summary>setup-1: one chunk per file ("zlb" 0x1A, then the stored or LZMA2 bytes), and each file's location entry.</summary>
  private static (byte[] Data, List<byte[]> Locations) SetupOne(Format format, List<Entry> entries, bool compressed) {
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
      locations.Add(Location(format, start, data.Position - start - ChunkId.Length, e, chunkCompressed));
    }
    return (data.ToArray(), locations);
  }

  /// <summary>A TSetupFileLocationEntry of <paramref name="format"/>'s layout.</summary>
  private static byte[] Location(Format format, long start, long chunkSize, Entry e, bool chunkCompressed) {
    var w = new RecordWriter();
    w.Int(0); // FirstSlice
    w.Int(0); // LastSlice
    if (format.AtLeast(Layout.V652)) w.Long(start); else w.Int(checked((int)start));
    w.Long(0); // ChunkSuboffset
    w.Long(e.Data!.Length);
    w.Long(chunkSize);
    w.Raw(format.AtLeast(Layout.V6401) ? SHA256.HashData(e.Data) : SHA1.HashData(e.Data));
    w.Long(e.Time.ToFileTimeUtc());
    w.Int(0); // FileVersionMS
    w.Int(0); // FileVersionLS
    if (format.AtLeast(Layout.V643)) {
      // VersionInfoValid, TimeStampInUTC, CallInstructionOptimized, ChunkEncrypted, ChunkCompressed
      w.Byte((byte)((1 << 1) | (chunkCompressed ? 1 << 4 : 0)));
    } else {
      // VersionInfoValid, VersionInfoNotValid, TimeStampInUTC, IsUninstExe, CallInstructionOptimized,
      // ApplyTouchDateTime, ChunkEncrypted, ChunkCompressed, SolidBreak (6.3: then Sign)
      w.Short((short)((1 << 1) | (1 << 2) | (chunkCompressed ? 1 << 7 : 0)));
      if (format.AtLeast(Layout.V630)) w.Byte(0); // Sign: no setting
    }
    return w.ToArray();
  }

  /// <summary>setup-0: the setup id, (6.5+) the encryption header, the headers block and the file locations block.</summary>
  private static byte[] SetupZero(Format format, InnoSetupStubs stubs, List<Entry> entries, List<byte[]> locations, InnoSetupInstallerOptions options, bool compressed) {
    var output = new MemoryStream();
    output.Write(format.SetupId);
    if (format.AtLeast(Layout.V650)) {
      // TSetupEncryptionHeader: EncryptionUse none, KDF salt, KDF iterations, base nonce, password test
      var header = new byte[49];
      BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(17), 220000);
      Span<byte> crc = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(header));
      output.Write(crc);
      output.Write(header);
    }
    var (block1, block2) = Headers(format, stubs, entries, locations, options, compressed);
    var wide = format.AtLeast(Layout.V670);
    CompressedBlock(output, block1, compressed: false, wide);
    CompressedBlock(output, block2, compressed: false, wide);
    return output.ToArray();
  }

  /// <summary>The uncompressed contents of setup-0's two blocks: the headers and entries, then the file locations.</summary>
  internal static (byte[] Block1, byte[] Block2) Headers(InnoSetupStubs stubs, IReadOnlyList<InnoSetupInstallerFile> files, InnoSetupInstallerOptions options) {
    var format = Identify(stubs);
    var entries = Prepare(files);
    var compressed = options.Compression == InnoSetupWriterCompression.Lzma2;
    var (_, locations) = SetupOne(format, entries, compressed);
    return Headers(format, stubs, entries, locations, options, compressed);
  }

  private static (byte[] Block1, byte[] Block2) Headers(Format format, InnoSetupStubs stubs, List<Entry> entries, List<byte[]> locations, InnoSetupInstallerOptions options, bool compressed) {
    var isl = IslFile.Parse(stubs.DefaultMessages);
    var dirs = entries.Where(e => e.Data is null).ToList();
    var files = entries.Where(e => e.Data is not null).ToList();
    var appName = options.AppName.Replace("{", "{{", StringComparison.Ordinal);
    var ntVersion = format.AtLeast(Layout.V610) ? (0x06010000, (short)0x0100) : (0x06000000, (short)0); // Windows 7 SP1 / Vista

    var w = new RecordWriter();
    // TSetupHeader strings
    w.Strings(appName, "", appName, "", "", "", "", "", "", options.AppVersion.Replace("{", "{{", StringComparison.Ordinal),
      options.DefaultDirName ?? "{autopf}\\" + appName, "(Default)");
    if (!format.AtLeast(Layout.V711)) w.Strings(""); // BaseFilename
    w.Strings("{app}", "", "", "", "{sysuserinfoname}", "{sysuserinfoorg}", "",
      "", "", "", "", "yes", "no", "*.exe,*.dll,*.chm", "", "no", "no");
    if (format.AtLeast(Layout.V630)) w.Strings(format.AtLeast(Layout.V7003) ? "x86compatible" : "", ""); // ArchitecturesAllowed, ...InstallIn64BitMode
    if (format.AtLeast(Layout.V642)) w.Strings(""); // CloseApplicationsFilterExcludes
    if (format.AtLeast(Layout.V650)) w.Strings(""); // SevenZipLibraryName
    if (format.AtLeast(Layout.V670)) w.Strings("yes", "yes", "yes", "yes", "yes"); // UsePreviousAppDir, Group, SetupType, Tasks, UserInfo
    w.Ansi("", "", "", ""); // LicenseText, InfoBeforeText, InfoAfterText, CompiledCodeText
    // counts: language, custom message, permission, type, component, task, dir, (6.5+: ISSig key,)
    // file, file location, icon, ini, registry, install delete, uninstall delete, run, uninstall run
    w.Int(1); w.Int(isl.CustomMessages.Count); w.Int(0); w.Int(0); w.Int(0); w.Int(0); w.Int(dirs.Count);
    if (format.AtLeast(Layout.V650)) w.Int(0);
    w.Int(files.Count); w.Int(files.Count);
    for (var i = 0; i < 7; ++i) w.Int(0);
    if (format.AtLeast(Layout.V7003)) w.Int(0); // CompiledCodeVersion: no [Code]
    w.Version(ntVersion);
    w.Version((0, 0)); // OnlyBelowVersion
    if (!format.AtLeast(Layout.V6401)) {
      w.Int(0x00FF0000); // BackColor (clBlue)
      w.Int(0); // BackColor2 (clBlack)
    }
    if (!format.AtLeast(Layout.V660)) {
      w.Byte(0); // WizardStyle: classic
      w.Int(100); // WizardSizePercentX
      w.Int(100); // WizardSizePercentY
    } else {
      w.Int(120); // WizardSizePercentX
      w.Int(120); // WizardSizePercentY
      w.Byte(0); // WizardDarkStyle: light
    }
    w.Byte(0); // WizardImageAlphaFormat: ignored
    if (!format.AtLeast(Layout.V6401))
      w.Zero(20 + 8); // PasswordHash, PasswordSalt
    else if (!format.AtLeast(Layout.V650)) {
      w.Int(0); // PasswordTest
      w.Zero(16); // EncryptionKDFSalt
      w.Int(200000); // EncryptionKDFIterations
      w.Zero(24); // EncryptionBaseNonce
    }
    if (format.AtLeast(Layout.V652)) {
      var imageBack = format.AtLeast(Layout.V660) ? unchecked((int)0xFF000005) : unchecked((int)0x80000005); // clWindow
      w.Int(imageBack); // WizardImageBackColor
      w.Int(imageBack); // WizardSmallImageBackColor
      if (format.AtLeast(Layout.V670)) w.Int(0x1FFFFFFF); // WizardBackColor: clNone
    }
    if (format.AtLeast(Layout.V660)) {
      w.Int(0); // WizardImageBackColorDynamicDark
      w.Int(0); // WizardSmallImageBackColorDynamicDark
      if (format.AtLeast(Layout.V670)) w.Int(0x1FFFFFFF); // WizardBackColorDynamicDark: clNone
    }
    if (format.AtLeast(Layout.V661)) w.Byte(255); // WizardImageOpacity
    if (format.AtLeast(Layout.V670)) {
      w.Byte(255); // WizardBackImageOpacity
      w.Byte(0); // WizardLightControlStyling: all
    }
    w.Long(0); // ExtraDiskSpaceRequired
    w.Int(1); // SlicesPerDisk
    w.Byte(0); // UninstallLogMode: append
    w.Byte(0); // DirExistsWarning: auto
    w.Byte(3); // PrivilegesRequired: lowest
    w.Byte(0); // PrivilegesRequiredOverridesAllowed: none
    w.Byte(0); // ShowLanguageDialog: yes
    w.Byte(0); // LanguageDetectionMethod: UI language
    w.Byte((byte)(compressed ? 4 : 0)); // CompressMethod: LZMA2 or stored
    if (!format.AtLeast(Layout.V630)) {
      w.Byte(0); // ArchitecturesAllowed: any
      w.Byte(0); // ArchitecturesInstallIn64BitMode: none
    }
    w.Byte(0); // DisableDirPage: auto
    w.Byte(0); // DisableProgramGroupPage: auto
    w.Long(0); // UninstallDisplaySize
    var (bits, optionBytes) = format.Layout switch {
      Layout.V600 or Layout.V610 => (OptionBits60, 6),
      Layout.V630 => (OptionBits60, 7),
      Layout.V6401 or Layout.V642 or Layout.V643 => (OptionBits64, 6),
      Layout.V650 or Layout.V652 => (OptionBits65, 6),
      Layout.V660 or Layout.V661 => (OptionBits66, 6),
      _ => (OptionBits67, 8),
    };
    w.Bits(bits, optionBytes);

    // TSetupLanguageEntry
    var lang = isl.LangOptions;
    if (format.AtLeast(Layout.V660)) {
      w.Strings("default", lang.GetValueOrDefault("LanguageName", "English"), lang.GetValueOrDefault("DialogFontName", "Segoe UI"),
        lang.GetValueOrDefault("WelcomeFontName", "Segoe UI"));
      w.AnsiBytes(Messages(format, isl));
      w.Ansi("", "", "");
      w.Short((short)IslNumber(lang.GetValueOrDefault("LanguageID", "$0409")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("DialogFontSize", "9")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("DialogFontBaseScaleHeight", "15")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("DialogFontBaseScaleWidth", "7")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("WelcomeFontSize", "14")));
    } else {
      w.Strings("default", lang.GetValueOrDefault("LanguageName", "English"), lang.GetValueOrDefault("DialogFontName", "Tahoma"),
        lang.GetValueOrDefault("TitleFontName", "Arial"), lang.GetValueOrDefault("WelcomeFontName", "Verdana"),
        lang.GetValueOrDefault("CopyrightFontName", "Arial"));
      w.AnsiBytes(Messages(format, isl));
      w.Ansi("", "", "");
      w.Int((int)IslNumber(lang.GetValueOrDefault("LanguageID", "$0409")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("DialogFontSize", "8")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("TitleFontSize", "29")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("WelcomeFontSize", "12")));
      w.Int((int)IslNumber(lang.GetValueOrDefault("CopyrightFontSize", "8")));
    }
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
      w.Version(ntVersion);
      w.Version((0, 0));
      w.Short(-1); // PermissionsEntry
      w.Byte(0); // Options
    }

    // TSetupFileEntry
    for (var i = 0; i < files.Count; ++i) {
      w.Strings("", files[i].DestName, "", "", "", "", "", "", "", "");
      if (format.AtLeast(Layout.V650)) {
        w.Strings("", "", "", "", ""); // Excludes, DownloadISSigSource, DownloadUserName, DownloadPassword, ExtractArchivePassword
        w.Ansi(""); // Verification.ISSigAllowedKeys
        w.Zero(32 + 1); // Verification.Hash, Verification.Typ: none
      }
      w.Version(ntVersion);
      w.Version((0, 0));
      w.Int(i); // LocationEntry
      w.Int(0); // Attribs
      w.Long(0); // ExternalSize
      w.Short(-1); // PermissionsEntry
      if (format.AtLeast(Layout.V7003)) w.Byte(0); // Bitness: install default
      w.Zero(format.AtLeast(Layout.V670) ? 8 : format.AtLeast(Layout.V650) ? 5 : 4); // Options
      w.Byte(0); // FileType: user file
    }

    // wizard images and small images; 6.6 adds their dynamic-dark twins, 6.7 back images too
    foreach (var image in (byte[][])[stubs.WizardImage, stubs.WizardSmallImage]) {
      if (image.Length == 0) w.Int(0);
      else {
        w.Int(1);
        w.Int(image.Length);
        w.Raw(image);
      }
    }
    var extraGroups = format.AtLeast(Layout.V670) ? 4 : format.AtLeast(Layout.V660) ? 2 : 0;
    for (var i = 0; i < extraGroups; ++i) w.Int(0);

    var block2 = new MemoryStream();
    foreach (var l in locations) block2.Write(l);
    return (w.ToArray(), block2.ToArray());
  }

  /// <summary>The message table: id, header and every message in TSetupMessageID order as zero-terminated UTF-16.</summary>
  private static byte[] Messages(Format format, IslFile isl) {
    var names = format.Layout switch {
      Layout.V600 => InnoMessageIds.V600,
      Layout.V610 or Layout.V630 => InnoMessageIds.V610,
      Layout.V6401 or Layout.V642 or Layout.V643 => InnoMessageIds.V640,
      _ => InnoMessageIds.V650,
    };
    var body = new MemoryStream();
    foreach (var name in names) {
      if (!isl.Messages.TryGetValue(name, out var text))
        throw new NotSupportedException($"Default.isl has no message '{name}'; it does not belong to this Setup.e32.");
      body.Write(Encoding.Unicode.GetBytes(text + "\0"));
    }
    var messages = body.ToArray();
    var total = 64 + 16 + messages.Length;
    var result = new byte[total];
    format.MessagesId.CopyTo(result, 0);
    var h = result.AsSpan(64);
    BinaryPrimitives.WriteInt32LittleEndian(h, names.Length);
    BinaryPrimitives.WriteInt32LittleEndian(h[4..], total);
    BinaryPrimitives.WriteInt32LittleEndian(h[8..], ~total);
    BinaryPrimitives.WriteUInt32LittleEndian(h[12..], Crc32.Compute(messages));
    messages.CopyTo(result, 80);
    return result;
  }

  /// <summary>The setup program as SetupLdr extracts it: call-transformed, then an LZMA1 block.</summary>
  private static byte[] SetupProgram(Format format, byte[] setup, out uint crc) {
    crc = Crc32.Compute(setup);
    var transformed = (byte[])setup.Clone();
    TransformCallInstructions(transformed, 0);
    var encoder = new LzmaEncoder(1 << 22);
    var packed = new MemoryStream();
    packed.Write(encoder.Properties);
    encoder.Encode(packed, transformed, writeEndMarker: false);
    var output = new MemoryStream();
    CompressedBlock(output, packed.ToArray(), compressed: true, format.AtLeast(Layout.V670));
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

  /// <summary>
  /// A compressed block: CRC-32 of the header (the stored size, data plus its CRCs, 32-bit before
  /// 6.7 and 64-bit from it, and the compressed flag), then the data in 4 KiB pieces, each behind its
  /// CRC-32.
  /// </summary>
  private static void CompressedBlock(Stream output, byte[] data, bool compressed, bool wide) {
    var pieces = (data.Length + 4095) / 4096;
    var stored = (long)data.Length + 4L * pieces;
    Span<byte> header = stackalloc byte[wide ? 13 : 9];
    if (wide) BinaryPrimitives.WriteInt64LittleEndian(header[4..], stored);
    else BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)stored);
    header[^1] = (byte)(compressed ? 1 : 0);
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

    /// <summary>A Delphi set of <paramref name="bytes"/> bytes with the given members.</summary>
    public void Bits(int[] members, int bytes) {
      var b = new byte[bytes];
      foreach (var m in members) b[m >> 3] |= (byte)(1 << (m & 7));
      this._ms.Write(b);
    }

    /// <summary>TSetupVersionData: WinVersion 0, NTVersion, NTServicePack.</summary>
    public void Version((int Nt, short ServicePack) v) {
      this.Int(0);
      this.Int(v.Nt);
      this.Short(v.ServicePack);
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
