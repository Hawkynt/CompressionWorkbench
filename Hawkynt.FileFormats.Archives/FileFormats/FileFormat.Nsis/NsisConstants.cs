namespace FileFormat.Nsis;

/// <summary>
/// Values from NSIS's <c>Source/exehead/fileform.h</c> (zlib/libpng licence).
/// </summary>
internal static class NsisConstants {
  /// <summary><c>FH_SIG</c>: the second word of <c>firstheader</c>.</summary>
  internal const uint FirstHeaderSignature = 0xDEADBEEF;

  /// <summary><c>FH_INT1..3</c>: "NullsoftInst" as three little-endian words.</summary>
  internal static ReadOnlySpan<byte> Magic => "NullsoftInst"u8;

  /// <summary>flags, siginfo, nsinst[3], length_of_header, length_of_all_following_data.</summary>
  internal const int FirstHeaderSize = 28;

  /// <summary><c>FH_FLAGS_MASK</c>: every other flag bit must be clear.</summary>
  internal const int FirstHeaderFlagsMask = 15;

  /// <summary>The exehead looks for <c>firstheader</c> only at multiples of 512 bytes.</summary>
  internal const int FirstHeaderAlignment = 512;

  /// <summary>Bit 31 of a non-solid block's length word: the block is compressed.</summary>
  internal const uint CompressedFlag = 0x80000000u;

  /// <summary>One <c>entry</c>: <c>which</c> plus <c>MAX_ENTRY_OFFSETS</c> (6) parameters.</summary>
  internal const int EntrySize = 28;

  // Block indices into header.blocks (NB_*). Pages are always compiled in, so entries and strings
  // keep these indices whether or not the optional background-font block follows.
  internal const int BlockEntries = 2;
  internal const int BlockStrings = 3;
  internal const int BlockLangTables = 4;

  // Opcodes that precede every optional one in the EW_* enum, so they hold in every NSIS 2/3 build.
  internal const int OpCreateDir = 11;   // EW_CREATEDIR: [path, ?SetOutPath, ?restrictAcl]
  internal const int OpExtractFile = 20; // EW_EXTRACTFILE: [overwrite, name, data offset, time low, time high, allow ignore]

  // String escape codes. NSIS 3 (ANSI and Unicode) uses NS_*_CODE 1..4; NSIS 2 ANSI used 252..255.
  internal const int Nsis3LangCode = 1, Nsis3ShellCode = 2, Nsis3VarCode = 3, Nsis3SkipCode = 4;
  internal const int Nsis2SkipCode = 252, Nsis2VarCode = 253, Nsis2ShellCode = 254, Nsis2LangCode = 255;

  // Variable indices (NSIS 2.26+ and 3.x): $0-$9 are 0-9, $R0-$R9 10-19, then these.
  internal const int VarInstDir = 21;
  internal const int VarOutDir = 22;
  internal const int VarExeDir = 23;
  internal const int VarTemp = 25;
  internal const int VarPluginsDir = 26;

  internal static readonly string[] InternalVariables = [
    "CMDLINE", "INSTDIR", "OUTDIR", "EXEDIR", "LANGUAGE", "TEMP", "PLUGINSDIR", "EXEPATH", "EXEFILE",
    "HWNDPARENT", "_CLICK", "_OUTDIR",
  ];

  /// <summary>
  /// NSIS constant names by CSIDL value, as the compiler's shell-folder table assigns them
  /// (CSIDL numbering per the Windows SDK's shlobj.h). Unused slots are null.
  /// </summary>
  internal static readonly string?[] ShellFolders = [
    "DESKTOP", "INTERNET", "SMPROGRAMS", "CONTROLS", "PRINTERS", "DOCUMENTS", "FAVORITES", "SMSTARTUP",
    "RECENT", "SENDTO", "BITBUCKET", "STARTMENU", null, "MUSIC", "VIDEOS", null,
    "DESKTOP", "DRIVES", "NETWORK", "NETHOOD", "FONTS", "TEMPLATES", "STARTMENU", "SMPROGRAMS",
    "SMSTARTUP", "DESKTOP", "APPDATA", "PRINTHOOD", "LOCALAPPDATA", "ALTSTARTUP", "ALTSTARTUP", "FAVORITES",
    "INTERNET_CACHE", "COOKIES", "HISTORY", "APPDATA", "WINDIR", "SYSDIR", "PROGRAM_FILES", "PICTURES",
    "PROFILE", "SYSTEMX86", "PROGRAM_FILESX86", "PROGRAM_FILES_COMMON", "PROGRAM_FILES_COMMONX8", "TEMPLATES", "DOCUMENTS", "ADMINTOOLS",
    "ADMINTOOLS", "CONNECTIONS", null, null, null, "MUSIC", "PICTURES", "VIDEOS",
    "RESOURCES", "RESOURCES_LOCALIZED", "COMMON_OEM_LINKS", "CDBURN_AREA", null, "COMPUTERSNEARME",
  ];
}
