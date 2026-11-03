using System.Text;

namespace FileFormat.InnoSetup;

/// <summary>Compression of a setup-1 chunk.</summary>
public enum InnoCompression {
  /// <summary>Stored.</summary>
  Stored,
  /// <summary>zlib (with the two-byte zlib header).</summary>
  Zlib,
  /// <summary>bzip2.</summary>
  BZip2,
  /// <summary>LZMA with Inno's five-byte properties header.</summary>
  Lzma1,
  /// <summary>LZMA2 with Inno's one-byte dictionary-size header.</summary>
  Lzma2,
  /// <summary>A value the header does not define.</summary>
  Unknown,
}

/// <summary>Encryption of a setup-1 chunk.</summary>
public enum InnoEncryption {
  /// <summary>Not encrypted.</summary>
  None,
  /// <summary>ARC4 keyed with MD5(salt + password) (4.2.2 – 5.3.8).</summary>
  Arc4Md5,
  /// <summary>ARC4 keyed with SHA-1(salt + password) (5.3.9 – 6.3.x).</summary>
  Arc4Sha1,
  /// <summary>XChaCha20 keyed by PBKDF2-SHA256 (6.4+).</summary>
  XChaCha20,
}

/// <summary>A [Files] entry of the install script.</summary>
internal sealed record InnoFileEntry(
  string Source, string Destination, string Languages, uint Location, ulong ExternalSize,
  bool DeleteAfterInstall, bool Bits32, bool Bits64, bool IgnoreVersion, bool PromptIfOlder,
  bool CompareTimeStamp, bool OverwriteSameVersion, bool ReplaceSameVersionIfContentsDiffer,
  bool IsUninstallerExe);

/// <summary>A [Dirs] entry of the install script.</summary>
internal sealed record InnoDirectoryEntry(string Name, bool DeleteAfterInstall);

/// <summary>Where one file's bytes live in setup-1 (a "file location" / data entry).</summary>
internal sealed record InnoDataEntry(
  uint FirstSlice, uint LastSlice, ulong ChunkOffset, ulong FileOffset, ulong FileSize, ulong ChunkSize,
  InnoChecksumKind ChecksumKind, byte[] Checksum, DateTime? Timestamp, ulong FileVersion,
  bool VersionInfoValid, InnoCompression Compression, InnoEncryption Encryption, InnoExeFilter Filter);

/// <summary>Kind of the per-file checksum.</summary>
internal enum InnoChecksumKind { Adler32, Crc32, Md5, Sha1, Sha256 }

/// <summary>The x86 call/jump address transform applied to executables before compression.</summary>
internal enum InnoExeFilter { None, Filter4108, Filter5200, Filter5309 }

/// <summary>
/// Parses the setup-0 header blocks: the setup header, every entry list that precedes the file
/// locations, and the file locations themselves.
/// </summary>
/// <remarks>
/// Converted from innoextract (https://github.com/dscharrer/innoextract, zlib licence),
/// src/setup/*.cpp: each loader below consumes the same fields in the same version-dependent
/// order. Only the values the extractor needs are kept.
/// </remarks>
internal sealed class InnoSetupInfo {
  public required InnoVersion Version { get; init; }
  public string AppName { get; private set; } = "";
  public string AppVersion { get; private set; } = "";
  public string BaseFilename { get; private set; } = "";
  public InnoCompression Compression { get; private set; } = InnoCompression.Zlib;
  public bool EncryptionUsed { get; set; }
  public uint SlicesPerDisk { get; private set; } = 1;
  public Encoding TextEncoding { get; private set; } = Encoding.Unicode;
  public List<InnoFileEntry> Files { get; } = [];
  public List<InnoDirectoryEntry> Directories { get; } = [];
  public List<InnoDataEntry> DataEntries { get; } = [];
  public int LanguageCount { get; private set; }
  public string SevenZipLibraryName { get; private set; } = "";

  // header counts
  private uint _languages, _messages, _permissions, _types, _components, _tasks, _directories, _files,
    _dataEntries, _issigKeys, _icons, _ini, _registry, _delete, _uninstallDelete, _run, _uninstallRun;
  private byte[] _appNameRaw = [], _appVersionRaw = [], _baseFilenameRaw = [];

  static InnoSetupInfo() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

  private InnoVersion V => this.Version;

  /// <summary>Parses the primary header block (header, languages ... run entries, images).</summary>
  public void LoadPrimary(byte[] block, out int warnings, out bool trailing) {
    var c = new InnoCursor(block);
    this.LoadHeader(c);

    // Languages decide the text encoding of ANSI installers.
    var codepages = new List<int>();
    for (var i = 0; i < this._languages; ++i)
      codepages.Add(this.LoadLanguage(c));
    this.LanguageCount = codepages.Count;
    if (this.V.IsUnicode)
      this.TextEncoding = Encoding.Unicode;
    else {
      var cp = codepages.Count == 0 || codepages.Contains(1252) ? 1252 : codepages[0];
      try {
        this.TextEncoding = Encoding.GetEncoding(cp);
      } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) {
        this.TextEncoding = Encoding.GetEncoding(1252);
      }
    }

    this.AppName = this.TextEncoding.GetString(this._appNameRaw);
    this.AppVersion = this.TextEncoding.GetString(this._appVersionRaw);
    this.BaseFilename = this.TextEncoding.GetString(this._baseFilenameRaw);

    if (this.V.Below(4, 0, 0))
      this.LoadWizardAndDecompressor(c);

    for (var i = 0; i < this._messages; ++i) this.SkipMessage(c);
    for (var i = 0; i < this._permissions; ++i) c.SkipString();
    for (var i = 0; i < this._types; ++i) this.SkipType(c);
    for (var i = 0; i < this._components; ++i) this.SkipComponent(c);
    for (var i = 0; i < this._tasks; ++i) this.SkipTask(c);
    for (var i = 0; i < this._directories; ++i) this.Directories.Add(this.LoadDirectory(c));
    for (var i = 0; i < this._issigKeys; ++i) { c.SkipString(); c.SkipString(); c.SkipString(); }
    for (var i = 0; i < this._files; ++i) this.Files.Add(this.LoadFile(c));
    for (var i = 0; i < this._icons; ++i) this.SkipIcon(c);
    for (var i = 0; i < this._ini; ++i) this.SkipIni(c);
    for (var i = 0; i < this._registry; ++i) this.SkipRegistry(c);
    for (var i = 0; i < this._delete; ++i) this.SkipDelete(c);
    for (var i = 0; i < this._uninstallDelete; ++i) this.SkipDelete(c);
    for (var i = 0; i < this._run; ++i) this.SkipRun(c);
    for (var i = 0; i < this._uninstallRun; ++i) this.SkipRun(c);

    if (this.V.AtLeast(4, 0, 0))
      this.LoadWizardAndDecompressor(c);

    warnings = c.Warnings;
    trailing = !c.AtEnd;
  }

  /// <summary>Parses the secondary header block: the file locations.</summary>
  public void LoadData(byte[] block, out int warnings, out bool trailing) {
    var c = new InnoCursor(block);
    for (var i = 0; i < this._dataEntries; ++i)
      this.DataEntries.Add(this.LoadDataEntry(c));
    warnings = c.Warnings;
    trailing = !c.AtEnd;
  }

  // ── header (src/setup/header.cpp) ────────────────────────────────────

  private void LoadHeader(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    this._appNameRaw = c.BinString();
    c.SkipString(); // app_versioned_name
    if (v.AtLeast(1, 3, 0)) c.SkipString(); // app_id
    c.SkipString(); // copyright
    if (v.AtLeast(1, 3, 0)) { c.SkipString(); c.SkipString(); } // publisher, publisher url
    if (v.AtLeast(5, 1, 13)) c.SkipString(); // support phone
    if (v.AtLeast(1, 3, 0)) { c.SkipString(); c.SkipString(); this._appVersionRaw = c.BinString(); } // support url, updates url, version
    c.SkipString(); // default dir name
    c.SkipString(); // default group name
    if (v.Below(3, 0, 0)) c.SkipString(); // uninstall icon name
    this._baseFilenameRaw = c.BinString();
    if (v.AtLeast(1, 3, 0) && v.Below(5, 2, 5)) { c.SkipString(); c.SkipString(); c.SkipString(); } // license, info before/after
    if (v.AtLeast(1, 3, 3)) c.SkipString(); // uninstall files dir
    if (v.AtLeast(1, 3, 6)) { c.SkipString(); c.SkipString(); } // uninstall name, icon
    if (v.AtLeast(1, 3, 14)) c.SkipString(); // app mutex
    if (v.AtLeast(3, 0, 0)) { c.SkipString(); c.SkipString(); } // default user name, organisation
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 6, 1))) c.SkipString(); // default serial
    if ((v.AtLeast(4, 0, 0) && v.Below(5, 2, 5)) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.SkipString(); // compiled code
    if (v.AtLeast(4, 2, 4)) { c.SkipString(); c.SkipString(); c.SkipString(); c.SkipString(); } // readme, contact, comments, modify path
    if (v.AtLeast(5, 3, 8)) c.SkipString(); // create uninstall registry key
    if (v.AtLeast(5, 3, 10)) c.SkipString(); // uninstallable
    if (v.AtLeast(5, 5, 0)) c.SkipString(); // close applications filter
    if (v.AtLeast(5, 5, 6)) c.SkipString(); // setup mutex
    if (v.AtLeast(5, 6, 1)) { c.SkipString(); c.SkipString(); } // changes environment, associations
    if (v.AtLeast(6, 3, 0)) { c.SkipString(); c.SkipString(); } // architectures allowed / 64-bit mode expressions
    // From here on the layout follows Inno Setup's own Shared.Struct.pas (issrc), not innoextract.
    if (v.AtLeast(6, 4, 2)) c.SkipString(); // close applications filter excludes
    if (v.AtLeast(6, 5, 0)) this.SevenZipLibraryName = this.Str(c);
    if (v.AtLeast(6, 7, 0)) for (var i = 0; i < 5; ++i) c.SkipString(); // UsePrevious* expressions
    if (v.AtLeast(5, 2, 5)) { c.SkipString(); c.SkipString(); c.SkipString(); } // license, info before/after
    if (v.AtLeast(5, 2, 1) && v.Below(5, 3, 10)) c.SkipString(); // uninstaller signature
    if (v.AtLeast(5, 2, 5)) c.SkipString(); // compiled code

    if (v.AtLeast(2, 0, 6) && !v.IsUnicode) {
      c.Skip(32); // lead-byte set (256 bits); .NET's DBCS encodings know their lead bytes
    }

    this._languages = v.AtLeast(4, 0, 0) ? c.U32() : v.AtLeast(2, 0, 1) ? 1u : 0u;
    this._messages = v.AtLeast(4, 2, 1) ? c.U32() : 0;
    this._permissions = v.AtLeast(4, 1, 0) ? c.U32() : 0;
    if (v.AtLeast(2, 0, 0) || v.IsIsx) { this._types = c.U32(); this._components = c.U32(); }
    if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 17))) this._tasks = c.U32();
    this._directories = c.U32();
    if (v.AtLeast(6, 5, 0)) this._issigKeys = c.U32();
    this._files = c.U32();
    this._dataEntries = c.U32();
    this._icons = c.U32();
    this._ini = c.U32();
    this._registry = c.U32();
    this._delete = c.U32();
    this._uninstallDelete = c.U32();
    this._run = c.U32();
    this._uninstallRun = c.U32();
    if (v.AtLeast(7, 0, 0, 3)) c.U32(); // compiled code version

    int licenseSize = 0, infoBeforeSize = 0, infoAfterSize = 0;
    if (v.Below(1, 3, 0)) { licenseSize = c.I32(); infoBeforeSize = c.I32(); infoAfterSize = c.I32(); }

    this.SkipWindowsVersionRange(c);

    if (v.Below(6, 4, 0, 1)) c.U32(); // back color
    if (v.AtLeast(1, 3, 3) && v.Below(6, 4, 0, 1)) c.U32(); // back color 2
    if (v.Below(5, 5, 7)) c.U32(); // image back color
    if ((v.AtLeast(2, 0, 0) && v.Below(5, 0, 4)) || v.IsIsx) c.U32(); // small image back color

    if (v.AtLeast(6, 6, 0)) { c.U32(); c.U32(); c.Enum(3); } // resize percent x/y, dark style
    else if (v.AtLeast(6, 0, 0)) { c.Enum(2); c.U32(); c.U32(); } // wizard style, resize percent x/y
    if (v.AtLeast(5, 5, 7)) c.Enum(3); // image alpha format
    var colors = v.AtLeast(6, 7, 0) ? 3 : 2;
    if (v.AtLeast(6, 5, 2)) c.Skip(4 * colors); // image, small image (, wizard) back colors
    if (v.AtLeast(6, 6, 0)) c.Skip(4 * colors); // their dynamic-dark variants
    if (v.AtLeast(6, 6, 1)) c.Skip(v.AtLeast(6, 7, 0) ? 2 : 1); // image (, back image) opacity
    if (v.AtLeast(6, 7, 0)) c.Enum(3); // light control styling

    if (v.AtLeast(6, 5, 0)) { } // encryption moved to the header outside the blocks
    else if (v.AtLeast(6, 4, 0)) c.Skip(4); // password check (XChaCha20)
    else if (v.AtLeast(5, 3, 9)) c.Skip(20); // SHA-1
    else if (v.AtLeast(4, 2, 0)) c.Skip(16); // MD5
    else c.Skip(4); // CRC32
    if (v.AtLeast(6, 5, 0)) { }
    else if (v.AtLeast(6, 4, 0)) c.Skip(44); // PBKDF2 salt + iterations + base nonce
    else if (v.AtLeast(4, 2, 2)) c.Skip(8); // salt

    if (v.AtLeast(4, 0, 0)) { c.I64(); this.SlicesPerDisk = c.U32(); } // extra disk space, slices per disk
    else c.I32();

    if ((v.AtLeast(2, 0, 0) && v.Below(5, 0, 0)) || (v.IsIsx && v.AtLeast(1, 3, 4))) c.Enum(3); // install mode
    if (v.AtLeast(1, 3, 0)) c.Enum(3); // uninstall log mode
    if (v.AtLeast(5, 0, 0)) { } // uninstall style is implied
    else if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 13))) c.Enum(2);
    if (v.AtLeast(1, 3, 6)) c.Enum(3); // dir exists warning
    if (v.IsIsx && v.AtLeast(2, 0, 10) && v.Below(3, 0, 0)) c.I32(); // code line offset
    if (v.AtLeast(3, 0, 0) && v.Below(3, 0, 3)) c.Enum(3); // restart
    if (v.AtLeast(5, 3, 7)) c.Enum(4); // privileges required
    else if (v.AtLeast(3, 0, 4) || (v.IsIsx && v.AtLeast(3, 0, 3))) c.Enum(3);
    if (v.AtLeast(5, 7, 0)) c.Flags(2); // privileges required overrides
    if (v.AtLeast(4, 0, 10)) { c.Enum(3); c.Enum(3); } // show language dialog, language detection

    var compressionSet = false;
    if (v.AtLeast(5, 3, 9)) { this.Compression = c.Enum(5) switch { 0 => InnoCompression.Stored, 1 => InnoCompression.Zlib, 2 => InnoCompression.BZip2, 3 => InnoCompression.Lzma1, 4 => InnoCompression.Lzma2, _ => InnoCompression.Unknown }; compressionSet = true; }
    else if (v.AtLeast(4, 2, 6)) { this.Compression = c.Enum(4) switch { 0 => InnoCompression.Stored, 1 => InnoCompression.Zlib, 2 => InnoCompression.BZip2, 3 => InnoCompression.Lzma1, _ => InnoCompression.Unknown }; compressionSet = true; }
    else if (v.AtLeast(4, 2, 5)) { this.Compression = c.Enum(3) switch { 0 => InnoCompression.Stored, 1 => InnoCompression.BZip2, 2 => InnoCompression.Lzma1, _ => InnoCompression.Unknown }; compressionSet = true; }
    else if (v.AtLeast(4, 1, 5)) { this.Compression = c.Enum(3) switch { 0 => InnoCompression.Zlib, 1 => InnoCompression.BZip2, 2 => InnoCompression.Lzma1, _ => InnoCompression.Unknown }; compressionSet = true; }

    if (v.AtLeast(6, 3, 0)) { }
    else if (v.AtLeast(5, 6, 0)) { c.Flags(5); c.Flags(5); } // architectures allowed / 64-bit mode
    else if (v.AtLeast(5, 1, 0)) { c.Flags(4); c.Flags(4); }

    if (v.AtLeast(5, 2, 1) && v.Below(5, 3, 10)) { c.U32(); c.U32(); } // signed uninstaller size, checksum
    if (v.AtLeast(5, 3, 3)) { c.Enum(3); c.Enum(3); } // disable dir page, disable program group page
    if (v.AtLeast(5, 5, 0)) c.U64(); // uninstall display size
    else if (v.AtLeast(5, 3, 6)) c.U32();

    if (v.Value == InnoVersion.V(5, 3, 10, 1) || v.Value == InnoVersion.V(5, 4, 2, 1) || v.Value == InnoVersion.V(5, 5, 0, 1))
      c.U8(); // the modified variant's extra byte

    bool bzipUsed;
    if (v.AtLeast(6, 5, 0)) {
      c.Flags(v.AtLeast(6, 7, 0) ? 57 : v.AtLeast(6, 6, 0) ? 46 : 43);
      bzipUsed = false;
    } else
      bzipUsed = this.LoadHeaderFlags(c);
    if (!compressionSet)
      this.Compression = bzipUsed ? InnoCompression.BZip2 : InnoCompression.Zlib;

    if (v.Below(1, 3, 0))
      c.Skip(Math.Max(0, licenseSize) + Math.Max(0, infoBeforeSize) + Math.Max(0, infoAfterSize));
  }

  /// <summary>header::load_flags; returns the "bzip used" flag of pre-4.1.5 versions.</summary>
  private bool LoadHeaderFlags(InnoCursor c) {
    var v = this.V;
    var f = c.BeginFlags();
    f.Next(); // disable startup prompt
    f.NextIf(v.Below(5, 3, 10)); // uninstallable
    f.Next(); // create app dir
    f.NextIf(v.Below(5, 3, 3)); // disable dir page
    f.NextIf(v.Below(1, 3, 6)); // disable dir exists warning
    f.NextIf(v.Below(5, 3, 3)); // disable program group page
    f.Next(); // allow no icons
    f.NextIf(v.Below(3, 0, 0) || v.AtLeast(3, 0, 3)); // always restart
    f.NextIf(v.Below(1, 3, 3)); // back solid
    f.Next(); // always use personal group
    if (v.Below(6, 4, 0, 1)) { f.Next(); f.Next(); f.Next(); f.Next(); } // window visible/caption/resizable/maximized
    f.Next(); // enable dir doesn't exist warning
    f.NextIf(v.Below(4, 1, 2)); // disable append dir
    f.Next(); // password
    f.NextIf(v.AtLeast(1, 2, 6)); // allow root directory
    f.NextIf(v.AtLeast(1, 2, 14)); // disable finished page
    f.NextIf(v.Below(3, 0, 4)); // admin privileges required
    f.NextIf(v.Below(3, 0, 0)); // always create uninstall icon
    f.NextIf(v.Below(1, 3, 6)); // overwrite uninst reg entries
    f.NextIf(v.Below(5, 6, 1)); // changes associations
    f.NextIf(v.AtLeast(1, 3, 0) && v.Below(5, 3, 8)); // create uninstall reg key
    f.NextIf(v.AtLeast(1, 3, 1)); // use previous app dir
    f.NextIf(v.AtLeast(1, 3, 3) && v.Below(6, 4, 0, 1)); // back color horizontal
    f.NextIf(v.AtLeast(1, 3, 10)); // use previous group
    f.NextIf(v.AtLeast(1, 3, 20)); // update uninstall log app name
    f.NextIf(v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 10))); // use previous setup type
    if (v.AtLeast(2, 0, 0)) { f.Next(); f.Next(); f.Next(); f.Next(); f.Next(); f.Next(); } // ready memo .. disable ready page
    if (v.AtLeast(2, 0, 7)) { f.Next(); f.Next(); } // always show dir/group on ready page
    var bzipUsed = f.NextIf(v.AtLeast(2, 0, 17) && v.Below(4, 1, 5));
    f.NextIf(v.AtLeast(2, 0, 18)); // allow UNC path
    if (v.AtLeast(3, 0, 0)) { f.Next(); f.Next(); } // user info page, use previous user info
    f.NextIf(v.AtLeast(3, 0, 1)); // uninstall restart computer
    f.NextIf(v.AtLeast(3, 0, 3)); // restart if needed by run
    f.NextIf(v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 3))); // show tasks tree lines
    f.NextIf(v.AtLeast(4, 0, 0) && v.Below(4, 0, 10)); // show language dialog
    f.NextIf(v.AtLeast(4, 0, 1) && v.Below(4, 0, 10)); // detect language using locale
    f.NextIf(v.AtLeast(4, 0, 9)); // allow cancel during install
    f.NextIf(v.AtLeast(4, 1, 3)); // wizard image stretch
    if (v.AtLeast(4, 1, 8)) { f.Next(); f.Next(); } // append default dir/group name
    this.EncryptionUsed = f.NextIf(v.AtLeast(4, 2, 2));
    f.NextIf(v.AtLeast(5, 0, 4) && v.Below(5, 6, 1)); // changes environment
    f.NextIf(v.AtLeast(5, 1, 7) && !v.IsUnicode); // show undisplayable languages
    f.NextIf(v.AtLeast(5, 1, 13)); // setup logging
    f.NextIf(v.AtLeast(5, 2, 1)); // signed uninstaller
    f.NextIf(v.AtLeast(5, 3, 8)); // use previous language
    f.NextIf(v.AtLeast(5, 3, 9)); // disable welcome page
    if (v.AtLeast(5, 5, 0)) { f.Next(); f.Next(); f.Next(); } // close/restart applications, allow network drive
    f.NextIf(v.AtLeast(5, 5, 7)); // force close applications
    if (v.AtLeast(6, 0, 0)) { f.Next(); f.Next(); f.Next(); } // app name has consts, use previous privileges, wizard resizable
    f.NextIf(v.AtLeast(6, 3, 0)); // uninstall logging
    f.End();
    return bzipUsed;
  }

  private void SkipWindowsVersionRange(InnoCursor c) {
    for (var i = 0; i < 2; ++i) {
      // win version, nt version: [build u16] minor u8 major u8; then nt service pack
      if (this.V.AtLeast(1, 3, 19)) c.U16();
      c.U8(); c.U8();
      if (this.V.AtLeast(1, 3, 19)) c.U16();
      c.U8(); c.U8();
      if (this.V.AtLeast(1, 3, 19)) { c.U8(); c.U8(); }
    }
  }

  private void LoadWizardAndDecompressor(InnoCursor c) {
    var v = this.V;
    void SkipImages() {
      var count = v.AtLeast(5, 6, 0) ? c.U32() : 1;
      for (var i = 0; i < count; ++i)
        c.SkipString();
    }
    if (v.AtLeast(6, 6, 0)) {
      // Light and dynamic-dark sets of wizard, small (and from 6.7.0 background) images; -1 = none.
      var sets = v.AtLeast(6, 7, 0) ? 6 : 4;
      for (var s = 0; s < sets; ++s) {
        var count = c.I32();
        for (var i = 0; i < count; ++i)
          c.SkipString();
      }
    } else {
      SkipImages();
      if (v.AtLeast(2, 0, 0) || v.IsIsx)
        SkipImages();
    }
    if (this.Compression == InnoCompression.BZip2
        || (this.Compression == InnoCompression.Lzma1 && v.Value == InnoVersion.V(4, 1, 5))
        || (this.Compression == InnoCompression.Zlib && v.AtLeast(4, 2, 6)))
      c.SkipString(); // decompressor dll
    if (v.AtLeast(6, 5, 0) && this.SevenZipLibraryName.Length > 0)
      c.SkipString(); // 7-Zip library
    if (this.EncryptionUsed && v.Below(6, 4, 0))
      c.SkipString(); // decryption dll
  }

  private string Str(InnoCursor c) => this.TextEncoding.GetString(c.BinString());

  // ── entries (src/setup/*.cpp) ────────────────────────────────────────

  private int LoadLanguage(InnoCursor c) {
    var v = this.V;
    if (v.AtLeast(6, 6, 0)) {
      for (var i = 0; i < 8; ++i) c.SkipString(); // name, language name, dialog font, welcome font; data, license, info before/after
      c.U16(); // language id
      c.Skip(4 * 4); // dialog font size, base scale height, base scale width, welcome font size
      c.Bool(); // right to left
      return 1200;
    }
    if (v.AtLeast(4, 0, 0)) c.SkipString(); // name
    c.SkipString(); // language name
    if (v.Value == InnoVersion.V(5, 5, 7, 1)) c.SkipString();
    c.SkipString(); c.SkipString(); c.SkipString(); c.SkipString(); // dialog, title, welcome, copyright fonts
    if (v.AtLeast(4, 0, 0)) c.SkipString(); // data
    if (v.AtLeast(4, 0, 1)) { c.SkipString(); c.SkipString(); c.SkipString(); } // license, info before/after
    var languageId = c.U32();
    var codepage = 1252;
    if (v.Below(4, 2, 2))
      codepage = DefaultCodepage(languageId);
    else if (!v.IsUnicode) {
      codepage = (int)c.U32();
      if (codepage == 0) codepage = 1252;
    } else if (v.Below(5, 3, 0))
      c.U32();
    c.U32(); // dialog font size
    if (v.Below(4, 1, 0)) c.U32(); // dialog font standard height
    c.U32(); c.U32(); c.U32(); // title, welcome, copyright font sizes
    if (v.Value == InnoVersion.V(5, 5, 7, 1)) c.U32();
    if (v.AtLeast(5, 2, 3)) c.Bool(); // right to left
    return codepage;
  }

  private void SkipMessage(InnoCursor c) {
    c.SkipString(); c.SkipString(); c.I32();
  }

  private void SkipConditions(InnoCursor c) {
    var v = this.V;
    if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 8))) c.SkipString(); // components
    if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 17))) c.SkipString(); // tasks
    if (v.AtLeast(4, 0, 1)) c.SkipString(); // languages
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.SkipString(); // check
    if (v.AtLeast(4, 1, 0)) { c.SkipString(); c.SkipString(); } // after/before install
  }

  private string ReadConditionsLanguages(InnoCursor c) {
    var v = this.V;
    var languages = "";
    if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 8))) c.SkipString();
    if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 17))) c.SkipString();
    if (v.AtLeast(4, 0, 1)) languages = this.Str(c);
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.SkipString();
    if (v.AtLeast(4, 1, 0)) { c.SkipString(); c.SkipString(); }
    return languages;
  }

  private void SkipType(InnoCursor c) {
    var v = this.V;
    c.SkipString(); c.SkipString(); // name, description
    if (v.AtLeast(4, 0, 1)) c.SkipString(); // languages
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.SkipString(); // check
    this.SkipWindowsVersionRange(c);
    c.Flags(1);
    if (v.AtLeast(4, 0, 3)) c.Enum(4);
    if (v.AtLeast(4, 0, 0)) c.U64(); else c.U32();
  }

  private void SkipComponent(InnoCursor c) {
    var v = this.V;
    c.SkipString(); c.SkipString(); c.SkipString(); // name, description, types
    if (v.AtLeast(4, 0, 1)) c.SkipString();
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.SkipString();
    if (v.AtLeast(4, 0, 0)) c.U64(); else c.U32(); // extra disk space
    if (v.AtLeast(6, 7, 0)) c.U8(); // level
    else if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 3))) c.I32(); // level
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 4))) c.Bool(); // used
    this.SkipWindowsVersionRange(c);
    if (v.AtLeast(4, 2, 3)) c.Flags(5);
    else if (v.AtLeast(3, 0, 8) || (v.IsIsx && v.AtLeast(3, 0, 6, 1))) c.Flags(4);
    else c.Flags(3);
    if (v.AtLeast(4, 0, 0)) c.U64();
    else if (v.AtLeast(2, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.U32();
  }

  private void SkipTask(InnoCursor c) {
    var v = this.V;
    c.SkipString(); c.SkipString(); c.SkipString(); c.SkipString(); // name, description, group, components
    if (v.AtLeast(4, 0, 1)) c.SkipString();
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(1, 3, 24))) c.SkipString();
    if (v.AtLeast(6, 7, 0)) c.U8();
    else if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 3))) c.I32();
    if (v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 4))) c.Bool();
    this.SkipWindowsVersionRange(c);
    var f = c.BeginFlags();
    f.Next(); f.Next();
    f.NextIf(v.AtLeast(2, 0, 5));
    f.NextIf(v.AtLeast(2, 0, 6));
    f.NextIf(v.AtLeast(4, 2, 3));
    f.End();
  }

  private InnoDirectoryEntry LoadDirectory(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    var name = this.Str(c);
    this.SkipConditions(c);
    if (v.AtLeast(4, 0, 11) && v.Below(4, 1, 0)) c.SkipString(); // permissions
    if (v.AtLeast(2, 0, 11)) c.U32(); // attributes
    this.SkipWindowsVersionRange(c);
    if (v.AtLeast(4, 1, 0)) c.I16(); // permission index
    var options = v.AtLeast(5, 2, 0) ? c.Flags(5) : c.Flags(3);
    return new(name, (options & 2) != 0);
  }

  private InnoFileEntry LoadFile(InnoCursor c) {
    var v = this.V;
    if (v.AtLeast(6, 5, 0))
      return this.LoadModernFile(c);
    if (v.Below(1, 3, 0)) c.U32();
    var source = this.Str(c);
    var destination = this.Str(c);
    c.SkipString(); // install font name
    if (v.AtLeast(5, 2, 5)) c.SkipString(); // strong assembly name
    var languages = this.ReadConditionsLanguages(c);
    this.SkipWindowsVersionRange(c);
    var location = c.U32();
    c.U32(); // attributes
    var externalSize = v.AtLeast(4, 0, 0) ? c.U64() : c.U32();
    var copyModeOptions = (ignoreVersion: false, promptIfOlder: false);
    if (v.Below(3, 0, 5)) {
      switch (c.Enum(4)) {
        case 0: copyModeOptions = (false, true); break;
        case 1: copyModeOptions = (false, true); break;
        case 2: copyModeOptions = (true, true); break;
      }
    }
    if (v.AtLeast(4, 1, 0)) c.I16(); // permission

    var f = c.BeginFlags();
    f.Next(); // confirm overwrite
    f.Next(); // never uninstall
    f.Next(); // restart replace
    var deleteAfterInstall = f.Next();
    f.Next(); f.Next(); f.Next(); // register server, register type lib, shared file
    f.NextIf(v.Below(2, 0, 0) && !v.IsIsx); // is readme file
    var compareTimeStamp = f.Next();
    f.Next(); // font is not truetype
    f.NextIf(v.AtLeast(1, 2, 5)); // skip if source doesn't exist
    f.NextIf(v.AtLeast(1, 2, 6)); // overwrite read only
    var overwriteSameVersion = false;
    if (v.AtLeast(1, 3, 21)) { overwriteSameVersion = f.Next(); f.Next(); } // custom dest name
    f.NextIf(v.AtLeast(1, 3, 25)); // only if dest file exists
    f.NextIf(v.AtLeast(2, 0, 5)); // no reg error
    f.NextIf(v.AtLeast(3, 0, 1)); // unins restart delete
    var ignoreVersion = copyModeOptions.ignoreVersion;
    var promptIfOlder = copyModeOptions.promptIfOlder;
    if (v.AtLeast(3, 0, 5)) { f.Next(); ignoreVersion |= f.Next(); promptIfOlder |= f.Next(); } // only if doesn't exist, ignore version, prompt if older
    f.NextIf(v.AtLeast(4, 0, 0) || (v.IsIsx && v.AtLeast(3, 0, 6, 1))); // don't copy
    f.NextIf(v.AtLeast(4, 0, 5)); // unins remove read only
    f.NextIf(v.AtLeast(4, 1, 8)); // recurse subdirs external
    var replaceSameIfDiffer = f.NextIf(v.AtLeast(4, 2, 1));
    f.NextIf(v.AtLeast(4, 2, 5)); // don't verify checksum
    f.NextIf(v.AtLeast(5, 0, 3)); // unins no shared file prompt
    f.NextIf(v.AtLeast(5, 1, 0)); // create all subdirs
    bool bits32 = false, bits64 = false;
    if (v.AtLeast(5, 1, 2)) { bits32 = f.Next(); bits64 = f.Next(); }
    if (v.AtLeast(5, 2, 0)) { f.Next(); f.Next(); f.Next(); } // external size preset, set/unset ntfs compression
    f.NextIf(v.AtLeast(5, 2, 5)); // gac install
    f.End();
    var type = v.AtLeast(5, 0, 0) ? c.Enum(2) : c.Enum(3);
    return new(source, destination, languages, location, externalSize, deleteAfterInstall, bits32, bits64,
      ignoreVersion, promptIfOlder, compareTimeStamp, overwriteSameVersion, replaceSameIfDiffer, type == 1);
  }

  /// <summary>TSetupFileEntry as of 6.5.0 (issrc Shared.Struct.pas).</summary>
  private InnoFileEntry LoadModernFile(InnoCursor c) {
    var v = this.V;
    var source = this.Str(c);
    var destination = this.Str(c);
    c.SkipString(); c.SkipString(); // install font name, strong assembly name
    c.SkipString(); c.SkipString(); // components, tasks
    var languages = this.Str(c);
    for (var i = 0; i < 8; ++i) c.SkipString(); // check, after/before install, excludes, download source/user/password, archive password
    c.SkipString(); // ISSigAllowedKeys (AnsiString)
    c.Skip(32 + 1); // verification hash and type
    this.SkipWindowsVersionRange(c);
    var location = c.U32();
    c.U32(); // attributes
    var externalSize = c.U64();
    c.I16(); // permission
    var bitness = v.AtLeast(7, 0, 0, 3) ? c.U8() : 0;
    var flags = c.Flags(v.AtLeast(6, 7, 0) ? 57 : 34);
    bool Bit(int n) => (flags & (1UL << n)) != 0;
    var type = c.Enum(2);
    var bits32 = v.AtLeast(7, 0, 0, 3) ? bitness == 1 : Bit(26);
    var bits64 = v.AtLeast(7, 0, 0, 3) ? bitness == 2 : Bit(27);
    return new(source, destination, languages, location, externalSize, Bit(3), bits32, bits64,
      Bit(17), Bit(18), Bit(7), Bit(11), Bit(22), type == 1);
  }

  private void SkipIcon(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    for (var i = 0; i < 6; ++i) c.SkipString(); // name, filename, parameters, working dir, icon file, comment
    this.SkipConditions(c);
    if (v.AtLeast(5, 3, 5)) c.SkipString(); // app user model id
    if (v.AtLeast(6, 1, 0)) c.Skip(16); // toast activator CLSID
    this.SkipWindowsVersionRange(c);
    c.I32(); // icon index
    if (v.AtLeast(1, 3, 24)) c.I32(); // show command
    if (v.AtLeast(1, 3, 15)) c.Enum(3); // close on exit
    if (v.AtLeast(2, 0, 7)) c.U16(); // hotkey
    var f = c.BeginFlags();
    f.Next();
    f.NextIf(v.Below(1, 3, 26));
    f.Next();
    f.Next();
    f.NextIf(v.AtLeast(5, 0, 3) && v.Below(6, 3, 0));
    f.NextIf(v.AtLeast(5, 4, 2));
    f.NextIf(v.AtLeast(5, 5, 0));
    f.NextIf(v.AtLeast(6, 1, 0));
    f.End();
  }

  private void SkipIni(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    for (var i = 0; i < 4; ++i) c.SkipString(); // file, section, key, value
    this.SkipConditions(c);
    this.SkipWindowsVersionRange(c);
    c.Flags(5);
  }

  private void SkipRegistry(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    c.SkipString(); c.SkipString(); c.SkipString(); // key, name, value
    this.SkipConditions(c);
    if (v.AtLeast(4, 0, 11) && v.Below(4, 1, 0)) c.SkipString(); // permissions
    this.SkipWindowsVersionRange(c);
    c.U32(); // hive
    if (v.AtLeast(4, 1, 0)) c.I16(); // permission
    if (v.AtLeast(5, 2, 5)) c.Enum(7); else c.Enum(6); // type
    if (v.AtLeast(7, 0, 0, 3)) { c.U8(); c.Flags(10); return; } // bitness, options
    var f = c.BeginFlags();
    f.Next(); f.Next(); // create value if doesn't exist, unins delete value
    f.Next(); f.Next(); f.Next(); // unins clear value, delete entire key, delete entire key if empty
    f.NextIf(v.AtLeast(1, 2, 6));
    if (v.AtLeast(1, 3, 9)) { f.Next(); f.Next(); }
    f.NextIf(v.AtLeast(1, 3, 12));
    f.NextIf(v.AtLeast(1, 3, 16));
    if (v.AtLeast(5, 1, 0)) { f.Next(); f.Next(); }
    f.End();
  }

  private void SkipDelete(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    c.SkipString();
    this.SkipConditions(c);
    this.SkipWindowsVersionRange(c);
    c.Enum(3);
  }

  private void SkipRun(InnoCursor c) {
    var v = this.V;
    if (v.Below(1, 3, 0)) c.U32();
    c.SkipString(); c.SkipString(); c.SkipString(); // name, parameters, working dir
    if (v.AtLeast(1, 3, 9)) c.SkipString(); // run once id
    if (v.AtLeast(2, 0, 2)) c.SkipString(); // status message
    if (v.AtLeast(5, 1, 13)) c.SkipString(); // verb
    if (v.AtLeast(2, 0, 0) || v.IsIsx) c.SkipString(); // description
    this.SkipConditions(c);
    if (v.AtLeast(7, 0, 0, 3)) c.SkipString(); // OnLog
    this.SkipWindowsVersionRange(c);
    if (v.AtLeast(1, 3, 24)) c.I32(); // show command
    c.Enum(3); // wait
    if (v.AtLeast(7, 0, 0, 3)) { c.U8(); c.Flags(10); return; } // bitness, options
    var f = c.BeginFlags();
    f.NextIf(v.AtLeast(1, 2, 3));
    f.NextIf(v.AtLeast(1, 3, 9) || (v.IsIsx && v.AtLeast(1, 3, 8)));
    if (v.AtLeast(2, 0, 0)) { f.Next(); f.Next(); f.Next(); f.Next(); }
    f.NextIf(v.AtLeast(2, 0, 8));
    if (v.AtLeast(5, 1, 10)) { f.Next(); f.Next(); }
    f.NextIf(v.AtLeast(5, 2, 0));
    f.NextIf(v.AtLeast(6, 1, 0));
    f.NextIf(v.AtLeast(6, 3, 0));
    f.End();
  }

  private static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private InnoDataEntry LoadDataEntry(InnoCursor c) {
    var v = this.V;
    var firstSlice = c.U32();
    var lastSlice = c.U32();
    if (v.Below(4, 0, 0)) {
      if (firstSlice >= 1 && lastSlice >= 1) { --firstSlice; --lastSlice; } else ++c.Warnings;
    }
    ulong chunkOffset = v.AtLeast(6, 5, 2) ? c.U64() : c.U32();
    ulong fileOffset = v.AtLeast(4, 0, 1) ? c.U64() : 0;
    ulong fileSize, chunkSize;
    if (v.AtLeast(4, 0, 0)) { fileSize = c.U64(); chunkSize = c.U64(); } else { fileSize = c.U32(); chunkSize = c.U32(); }

    InnoChecksumKind kind;
    byte[] checksum;
    if (v.AtLeast(6, 4, 0)) { kind = InnoChecksumKind.Sha256; checksum = c.Bytes(32); }
    else if (v.AtLeast(5, 3, 9)) { kind = InnoChecksumKind.Sha1; checksum = c.Bytes(20); }
    else if (v.AtLeast(4, 2, 0)) { kind = InnoChecksumKind.Md5; checksum = c.Bytes(16); }
    else if (v.AtLeast(4, 0, 1)) { kind = InnoChecksumKind.Crc32; checksum = c.Bytes(4); }
    else { kind = InnoChecksumKind.Adler32; checksum = c.Bytes(4); }

    var filetime = c.I64();
    DateTime? timestamp = filetime > 0 && filetime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(filetime) : null;
    var versionMs = c.U32();
    var versionLs = c.U32();

    if (v.AtLeast(6, 4, 3))
      return this.FinishModernDataEntry(c, firstSlice, lastSlice, chunkOffset, fileOffset, fileSize, chunkSize, checksum, timestamp, versionMs, versionLs);

    var f = c.BeginFlags();
    var versionInfoValid = f.Next();
    f.Next(); // version info not valid
    var bzipped = f.NextIf(v.AtLeast(2, 0, 17) && v.Below(4, 0, 1));
    f.NextIf(v.AtLeast(4, 0, 10)); // timestamp in UTC
    f.NextIf(v.AtLeast(4, 1, 0)); // is uninstaller exe
    var callInstructionOptimized = f.NextIf(v.AtLeast(4, 1, 8));
    f.NextIf(v.AtLeast(4, 2, 0)); // touch
    var encrypted = f.NextIf(v.AtLeast(4, 2, 2));
    var compressed = v.AtLeast(4, 2, 5) ? f.Next() : true;
    f.NextIf(v.AtLeast(5, 1, 13)); // solid break
    if (v.AtLeast(5, 5, 7) && v.Below(6, 3, 0)) { f.Next(); f.Next(); } // sign, sign once
    f.End();
    if (v.AtLeast(6, 3, 0)) c.Enum(4); // sign mode

    var compression = compressed ? this.Compression : InnoCompression.Stored;
    if (bzipped)
      compression = InnoCompression.BZip2;
    var encryption = !encrypted ? InnoEncryption.None
      : v.AtLeast(6, 4, 0) ? InnoEncryption.XChaCha20
      : v.AtLeast(5, 3, 9) ? InnoEncryption.Arc4Sha1
      : InnoEncryption.Arc4Md5;
    var filter = !callInstructionOptimized ? InnoExeFilter.None
      : v.Below(5, 2, 0) ? InnoExeFilter.Filter4108
      : v.Below(5, 3, 9) ? InnoExeFilter.Filter5200
      : InnoExeFilter.Filter5309;

    return new(firstSlice, lastSlice, chunkOffset, fileOffset, fileSize, chunkSize, kind, checksum, timestamp,
      ((ulong)versionMs << 32) | versionLs, versionInfoValid, compression, encryption, filter);
  }

  // 6.4.3+: Flags = (VersionInfoValid, TimeStampInUTC, CallInstructionOptimized, ChunkEncrypted, ChunkCompressed), no Sign.
  private InnoDataEntry FinishModernDataEntry(InnoCursor c, uint firstSlice, uint lastSlice, ulong chunkOffset, ulong fileOffset,
      ulong fileSize, ulong chunkSize, byte[] checksum, DateTime? timestamp, uint versionMs, uint versionLs) {
    var flags = c.Flags(5);
    var compression = (flags & 16) != 0 ? this.Compression : InnoCompression.Stored;
    var encryption = (flags & 8) != 0 ? InnoEncryption.XChaCha20 : InnoEncryption.None;
    var filter = (flags & 4) != 0 ? InnoExeFilter.Filter5309 : InnoExeFilter.None;
    return new(firstSlice, lastSlice, chunkOffset, fileOffset, fileSize, chunkSize, InnoChecksumKind.Sha256, checksum, timestamp,
      ((ulong)versionMs << 32) | versionLs, (flags & 1) != 0, compression, encryption, filter);
  }

  private static int DefaultCodepage(uint languageId) => languageId switch {
    0x0401 or 0x0420 or 0x0429 or 0x0801 or 0x0c01 or 0x1001 or 0x1401 or 0x1801 or 0x1c01 or 0x2001 or 0x2401
      or 0x2801 or 0x2c01 or 0x3001 or 0x3401 or 0x3801 or 0x3c01 or 0x4001 => 1256,
    0x0402 or 0x0419 or 0x0422 or 0x0423 or 0x042f or 0x043f or 0x0440 or 0x0444 or 0x0450 or 0x082c or 0x0843 or 0x0c1a => 1251,
    0x0404 or 0x0c04 or 0x1404 => 950,
    0x0405 or 0x040e or 0x0415 or 0x0418 or 0x041a or 0x041b or 0x041c or 0x0424 or 0x081a => 1250,
    0x0408 => 1253,
    0x040d => 1255,
    0x0411 => 932,
    0x0412 => 949,
    0x041e => 874,
    0x041f or 0x042c or 0x0443 => 1254,
    0x0425 or 0x0426 or 0x0427 => 1257,
    0x042a => 1258,
    0x0492 => 28604,
    0x0804 or 0x1004 => 936,
    _ => 1252,
  };
}
