namespace Compression.Lib;

/// <summary>
/// Identifies archive/compression formats by file extension and magic bytes.
/// </summary>
internal static class FormatDetector {

  internal enum Format {
    Unknown,
    // Archive formats (38)
    Zip, Rar, SevenZip, Tar, Cab, Lzh, Arj, Arc, Zoo, Ace, Sqx, Cpio, Ar, Wim, Rpm, Deb,
    Shar, Pak, Iso, Udf, Ha, Nsis, InnoSetup, SquashFs, CramFs, StuffIt, Zpaq,
    Sfx,
    Dms, LzxAmiga, CompactPro, Spark, Lbr, Uharc, Wad, Xar, AlZip,
    Vpk, Bsa, Mpq,
    // Stream compression formats (21)
    Gzip, Bzip2, Xz, Zstd, Lz4, Brotli, Snappy, Lzop, Compress, Lzma, Lzip, Zlib, Szdd, Kwaj,
    PowerPacker, Squeeze, IcePacker, Rzip,
    PackBits, Yaz0, BriefLz, Rnc, RefPack, ApLib, Lzfse, Freeze,
    UuEncoding, YEnc, Density,
    // Wrapper formats (2)
    MacBinary, BinHex,
    // Compound formats (7)
    TarGz, TarBz2, TarXz, TarZst, TarLz4, TarLzip, TarBr,
  }

  internal static bool IsArchive(Format f) => f switch {
    Format.Zip or Format.Rar or Format.SevenZip or Format.Tar or Format.Cab or Format.Lzh or
    Format.Arj or Format.Arc or Format.Zoo or Format.Ace or Format.Sqx or Format.Cpio or
    Format.Ar or Format.Wim or Format.Rpm or Format.Deb or
    Format.Shar or Format.Pak or Format.Iso or Format.Udf or
    Format.Ha or Format.Nsis or Format.InnoSetup or Format.SquashFs or Format.CramFs or
    Format.StuffIt or Format.Zpaq or Format.Sfx or
    Format.Dms or Format.LzxAmiga or Format.CompactPro or Format.Spark or Format.Lbr or
    Format.Uharc or Format.Wad or Format.Xar or Format.AlZip or
    Format.Vpk or Format.Bsa or Format.Mpq => true,
    Format.TarGz or Format.TarBz2 or Format.TarXz or Format.TarZst or Format.TarLz4 or Format.TarLzip or Format.TarBr => true,
    _ => false,
  };

  internal static bool IsStreamFormat(Format f) => f switch {
    Format.Gzip or Format.Bzip2 or Format.Xz or Format.Zstd or Format.Lz4 or Format.Brotli or
    Format.Snappy or Format.Lzop or Format.Compress or Format.Lzma or Format.Lzip or
    Format.Zlib or Format.Szdd or Format.Kwaj or
    Format.PowerPacker or Format.Squeeze or Format.IcePacker or Format.Rzip or
    Format.PackBits or Format.Yaz0 or Format.BriefLz or Format.Rnc or
    Format.RefPack or Format.ApLib or Format.Lzfse or Format.Freeze or
    Format.UuEncoding or Format.YEnc or Format.Density or
    Format.MacBinary or Format.BinHex => true,
    _ => false,
  };

  internal static bool IsCompoundTar(Format f) => f is
    Format.TarGz or Format.TarBz2 or Format.TarXz or Format.TarZst or Format.TarLz4 or Format.TarLzip or Format.TarBr;

  /// <summary>Returns the stream compression format wrapping a compound tar, or null if not compound.</summary>
  internal static Format? GetTarCompression(Format f) => f switch {
    Format.TarGz => Format.Gzip,
    Format.TarBz2 => Format.Bzip2,
    Format.TarXz => Format.Xz,
    Format.TarZst => Format.Zstd,
    Format.TarLz4 => Format.Lz4,
    Format.TarLzip => Format.Lzip,
    Format.TarBr => Format.Brotli,
    _ => null,
  };

  /// <summary>Returns the compound tar format for a given stream compression, or null.</summary>
  internal static Format? GetCompoundTar(Format compression) => compression switch {
    Format.Gzip => Format.TarGz,
    Format.Bzip2 => Format.TarBz2,
    Format.Xz => Format.TarXz,
    Format.Zstd => Format.TarZst,
    Format.Lz4 => Format.TarLz4,
    Format.Lzip => Format.TarLzip,
    Format.Brotli => Format.TarBr,
    _ => null,
  };

  internal static Format DetectByExtension(string path) {
    var lower = path.ToLowerInvariant();

    // Compound tar formats (check multi-extension first)
    if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")) return Format.TarGz;
    if (lower.EndsWith(".tar.bz2") || lower.EndsWith(".tbz2") || lower.EndsWith(".tbz")) return Format.TarBz2;
    if (lower.EndsWith(".tar.xz") || lower.EndsWith(".txz")) return Format.TarXz;
    if (lower.EndsWith(".tar.zst") || lower.EndsWith(".tzst")) return Format.TarZst;
    if (lower.EndsWith(".tar.lz4")) return Format.TarLz4;
    if (lower.EndsWith(".tar.lz") || lower.EndsWith(".tar.lzip")) return Format.TarLzip;
    if (lower.EndsWith(".tar.br") || lower.EndsWith(".tbr")) return Format.TarBr;

    return Path.GetExtension(lower) switch {
      ".zip" or ".jar" or ".war" or ".ear" or ".apk" or ".ipa" or ".xpi" or ".nupkg" or ".epub" => Format.Zip,
      ".rar" => Format.Rar,
      ".7z" => Format.SevenZip,
      ".tar" => Format.Tar,
      ".cab" => Format.Cab,
      ".lzh" or ".lha" => Format.Lzh,
      ".arj" => Format.Arj,
      ".arc" => Format.Arc,
      ".zoo" => Format.Zoo,
      ".ace" => Format.Ace,
      ".sqx" => Format.Sqx,
      ".cpio" => Format.Cpio,
      ".shar" => Format.Shar,
      ".pak" => Format.Pak,
      ".iso" => Format.Iso,
      ".udf" => Format.Udf,
      ".ha" => Format.Ha,
      ".exe" => DetectInstaller(path),
      ".squashfs" or ".sqfs" or ".snap" => Format.SquashFs,
      ".cramfs" => Format.CramFs,
      ".sit" or ".stuffit" => Format.StuffIt,
      ".zpaq" => Format.Zpaq,
      ".ar" or ".a" or ".deb" => DetectArOrDeb(lower),
      ".wim" or ".swm" or ".esd" => Format.Wim,
      ".rpm" => Format.Rpm,
      ".gz" or ".gzip" => Format.Gzip,
      ".bz2" or ".bzip2" => Format.Bzip2,
      ".xz" => Format.Xz,
      ".zst" or ".zstd" => Format.Zstd,
      ".lz4" => Format.Lz4,
      ".br" => Format.Brotli,
      ".sz" or ".snappy" => Format.Snappy,
      ".lzo" => Format.Lzop,
      ".z" => Format.Compress,
      ".lzma" => Format.Lzma,
      ".lz" or ".lzip" => Format.Lzip,
      ".zlib" => Format.Zlib,
      ".dms" => Format.Dms,
      ".lzx" => Format.LzxAmiga,
      ".pp" or ".pp20" => Format.PowerPacker,
      ".cpt" => Format.CompactPro,
      ".bin" or ".macbin" => Format.MacBinary,
      ".hqx" => Format.BinHex,
      ".spk" or ".spark" => Format.Spark,
      ".lbr" => Format.Lbr,
      ".sqz" => Format.Squeeze,
      ".ice" => Format.IcePacker,
      ".uha" => Format.Uharc,
      ".rz" or ".rzip" => Format.Rzip,
      ".wad" => Format.Wad,
      ".packbits" => Format.PackBits,
      ".yaz0" or ".szs" => Format.Yaz0,
      ".blz" => Format.BriefLz,
      ".rnc" => Format.Rnc,
      ".qfs" or ".refpack" => Format.RefPack,
      ".aplib" => Format.ApLib,
      ".lzfse" => Format.Lzfse,
      ".f" or ".freeze" => Format.Freeze,
      ".xar" => Format.Xar,
      ".alz" => Format.AlZip,
      ".vpk" => Format.Vpk,
      ".bsa" or ".ba2" => Format.Bsa,
      ".mpq" => Format.Mpq,
      ".uue" or ".uu" => Format.UuEncoding,
      ".yenc" => Format.YEnc,
      ".density" => Format.Density,
      _ when lower.EndsWith(".sz_") || lower.EndsWith("._") => Format.Szdd,
      _ => Format.Unknown,
    };
  }

  private static Format DetectArOrDeb(string lower)
    => lower.EndsWith(".deb") ? Format.Deb : Format.Ar;

  private static Format DetectInstaller(string path) {
    try {
      using var fs = File.OpenRead(path);
      var buf = new byte[2];
      // Quick check: must be a PE (MZ header)
      if (fs.Read(buf, 0, 2) < 2 || buf[0] != 'M' || buf[1] != 'Z') return Format.Unknown;

      // Check for NSIS/InnoSetup signatures in tail
      if (fs.Length > 65536) {
        fs.Seek(-65536, SeekOrigin.End);
        var tail = new byte[65536];
        var bytesRead = fs.Read(tail, 0, tail.Length);
        var tailSpan = tail.AsSpan(0, bytesRead);
        if (ContainsBytes(tailSpan, "NullsoftInst"u8)) return Format.Nsis;
        if (ContainsBytes(tailSpan, "Inno Setup"u8)) return Format.InnoSetup;
      }

      // Check for our own SFX trailer (SFX! magic at end)
      if (fs.Length >= 12) {
        fs.Seek(-4, SeekOrigin.End);
        Span<byte> magic = stackalloc byte[4];
        fs.ReadExactly(magic);
        if (magic[0] == 'S' && magic[1] == 'F' && magic[2] == 'X' && magic[3] == '!')
          return Format.Sfx;
      }

      // Check for third-party SFX: scan PE overlay for embedded archive
      var sfxInfo = PeOverlay.DetectEmbeddedArchive(path);
      if (sfxInfo != null)
        return Format.Sfx;
    }
    catch { /* ignore detection failure */ }
    return Format.Unknown;
  }

  private static bool ContainsBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    return haystack.IndexOf(needle) >= 0;
  }

  internal static Format DetectByMagic(ReadOnlySpan<byte> header) {
    if (header.Length < 4) return Format.Unknown;

    // ZIP: PK\x03\x04
    if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
      return Format.Zip;

    // RAR: Rar!\x1A\x07
    if (header.Length >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 &&
        header[3] == 0x21 && header[4] == 0x1A && header[5] == 0x07)
      return Format.Rar;

    // 7z: 7z\xBC\xAF\x27\x1C
    if (header.Length >= 6 && header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
        header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
      return Format.SevenZip;

    // Gzip: \x1F\x8B
    if (header[0] == 0x1F && header[1] == 0x8B)
      return Format.Gzip;

    // Bzip2: BZh
    if (header[0] == 0x42 && header[1] == 0x5A && header[2] == 0x68)
      return Format.Bzip2;

    // XZ: \xFD7zXZ\x00
    if (header.Length >= 6 && header[0] == 0xFD && header[1] == 0x37 && header[2] == 0x7A &&
        header[3] == 0x58 && header[4] == 0x5A && header[5] == 0x00)
      return Format.Xz;

    // Zstd: \x28\xB5\x2F\xFD
    if (header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
      return Format.Zstd;

    // LZ4: \x04\x22\x4D\x18
    if (header[0] == 0x04 && header[1] == 0x22 && header[2] == 0x4D && header[3] == 0x18)
      return Format.Lz4;

    // CAB: MSCF
    if (header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x43 && header[3] == 0x46)
      return Format.Cab;

    // LZOP: \x89LZO\x00\x0D\x0A\x1A\x0A
    if (header.Length >= 9 && header[0] == 0x89 && header[1] == 0x4C && header[2] == 0x5A && header[3] == 0x4F)
      return Format.Lzop;

    // Snappy: \xFF\x06\x00\x00sNaPpY
    if (header.Length >= 10 && header[0] == 0xFF && header[1] == 0x06 && header[2] == 0x00 && header[3] == 0x00 &&
        header[4] == 0x73 && header[5] == 0x4E && header[6] == 0x61 && header[7] == 0x50)
      return Format.Snappy;

    // Compress: \x1F\x9D
    if (header[0] == 0x1F && header[1] == 0x9D)
      return Format.Compress;

    // SZDD: SZDD\x88\xF0\x27\x33
    if (header.Length >= 8 && header[0] == 0x53 && header[1] == 0x5A && header[2] == 0x44 && header[3] == 0x44)
      return Format.Szdd;

    // KWAJ: KWAJ
    if (header[0] == 0x4B && header[1] == 0x57 && header[2] == 0x41 && header[3] == 0x4A)
      return Format.Kwaj;

    // Yaz0: "Yaz0"
    if (header[0] == 0x59 && header[1] == 0x61 && header[2] == 0x7A && header[3] == 0x30)
      return Format.Yaz0;

    // BriefLZ: "blz\x1A"
    if (header[0] == 0x62 && header[1] == 0x6C && header[2] == 0x7A && header[3] == 0x1A)
      return Format.BriefLz;

    // RNC: "RNC\x01" or "RNC\x02"
    if (header[0] == 0x52 && header[1] == 0x4E && header[2] == 0x43 && (header[3] == 0x01 || header[3] == 0x02))
      return Format.Rnc;

    // LZFSE: "bvx1", "bvx2", "bvxn", "bvx-"
    if (header[0] == 0x62 && header[1] == 0x76 && header[2] == 0x78 &&
        (header[3] == 0x31 || header[3] == 0x32 || header[3] == 0x6E || header[3] == 0x2D))
      return Format.Lzfse;

    // XAR: "xar!"
    if (header[0] == 0x78 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21)
      return Format.Xar;

    // ALZip: "ALZ\x01"
    if (header[0] == 0x41 && header[1] == 0x4C && header[2] == 0x5A && header[3] == 0x01)
      return Format.AlZip;

    // PackBits: "PKBT"
    if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x42 && header[3] == 0x54)
      return Format.PackBits;

    // aPLib: "AP32"
    if (header[0] == 0x41 && header[1] == 0x50 && header[2] == 0x33 && header[3] == 0x32)
      return Format.ApLib;

    // Freeze: \x1F\x9E or \x1F\x9F
    if (header[0] == 0x1F && (header[1] == 0x9E || header[1] == 0x9F))
      return Format.Freeze;

    // RefPack/QFS: 0x10 0xFB or at offset 4 (with compressed size prefix)
    if (header.Length >= 5 && ((header[0] & 0xFE) == 0x10) && header[1] == 0xFB)
      return Format.RefPack;
    if (header.Length >= 9 && ((header[4] & 0xFE) == 0x10) && header[5] == 0xFB)
      return Format.RefPack;

    // VPK: 0x55AA1234 (LE)
    if (header[0] == 0x34 && header[1] == 0x12 && header[2] == 0xAA && header[3] == 0x55)
      return Format.Vpk;

    // BSA TES4+: "BSA\0"
    if (header[0] == 0x42 && header[1] == 0x53 && header[2] == 0x41 && header[3] == 0x00)
      return Format.Bsa;

    // BA2: "BTDX"
    if (header[0] == 0x42 && header[1] == 0x54 && header[2] == 0x44 && header[3] == 0x58)
      return Format.Bsa;

    // MPQ: "MPQ\x1A"
    if (header[0] == 0x4D && header[1] == 0x50 && header[2] == 0x51 && header[3] == 0x1A)
      return Format.Mpq;
    // MPQ user data: "MPQ\x1B"
    if (header[0] == 0x4D && header[1] == 0x50 && header[2] == 0x51 && header[3] == 0x1B)
      return Format.Mpq;

    // Density: "DENS"
    if (header[0] == 'D' && header[1] == 'E' && header[2] == 'N' && header[3] == 'S')
      return Format.Density;

    // UUEncoding: "begin "
    if (header.Length >= 6 && header[0] == 'b' && header[1] == 'e' && header[2] == 'g' &&
        header[3] == 'i' && header[4] == 'n' && header[5] == ' ')
      return Format.UuEncoding;

    // yEnc: "=ybegin "
    if (header.Length >= 8 && header[0] == '=' && header[1] == 'y' && header[2] == 'b' &&
        header[3] == 'e' && header[4] == 'g' && header[5] == 'i' && header[6] == 'n' && header[7] == ' ')
      return Format.YEnc;

    // LZMA: byte 0 = properties (typically 0x5D), then 4-byte dict size LE
    if (header.Length >= 13 && header[0] < 0xE1 && header[0] % 9 < 9) {
      var dictSize = (uint)(header[1] | (header[2] << 8) | (header[3] << 16) | (header[4] << 24));
      if (dictSize > 0 && dictSize <= 0x40000000)
        return Format.Lzma;
    }

    // Lzip: LZIP
    if (header[0] == 0x4C && header[1] == 0x5A && header[2] == 0x49 && header[3] == 0x50)
      return Format.Lzip;

    // AR: !<arch>\n
    if (header.Length >= 8 && header[0] == 0x21 && header[1] == 0x3C && header[2] == 0x61 &&
        header[3] == 0x72 && header[4] == 0x63 && header[5] == 0x68 && header[6] == 0x3E && header[7] == 0x0A)
      return Format.Ar;

    // RPM: \xED\xAB\xEE\xDB
    if (header[0] == 0xED && header[1] == 0xAB && header[2] == 0xEE && header[3] == 0xDB)
      return Format.Rpm;

    // WIM: MSWIM\x00\x00\x00
    if (header.Length >= 8 && header[0] == 0x4D && header[1] == 0x53 && header[2] == 0x57 &&
        header[3] == 0x49 && header[4] == 0x4D && header[5] == 0x00 && header[6] == 0x00 && header[7] == 0x00)
      return Format.Wim;

    // ACE: check for **ACE** at offset 7
    if (header.Length >= 14 && header[7] == 0x2A && header[8] == 0x2A && header[9] == 0x41 &&
        header[10] == 0x43 && header[11] == 0x45 && header[12] == 0x2A && header[13] == 0x2A)
      return Format.Ace;

    // Shar: #!/bin/sh
    if (header.Length >= 10 && header[0] == '#' && header[1] == '!' && header[2] == '/' &&
        header[3] == 'b' && header[4] == 'i' && header[5] == 'n' && header[6] == '/')
      return Format.Shar;

    // PAK: same magic as ARC (0x1A at offset 0)
    // Cannot distinguish PAK from ARC by magic alone — extension required.

    // Ha: "HA" (0x48, 0x41)
    if (header[0] == 0x48 && header[1] == 0x41)
      return Format.Ha;

    // SquashFS: "hsqs" (0x73717368 LE)
    if (header[0] == 0x68 && header[1] == 0x73 && header[2] == 0x71 && header[3] == 0x73)
      return Format.SquashFs;

    // CramFS: 0x28CD3D45
    if (header[0] == 0x45 && header[1] == 0x3D && header[2] == 0xCD && header[3] == 0x28)
      return Format.CramFs;

    // StuffIt: "SIT!" (0x53495421)
    if (header[0] == 0x53 && header[1] == 0x49 && header[2] == 0x54 && header[3] == 0x21)
      return Format.StuffIt;
    // StuffIt 5: "StuffIt"
    if (header.Length >= 7 && header[0] == 0x53 && header[1] == 0x74 && header[2] == 0x75 &&
        header[3] == 0x66 && header[4] == 0x66 && header[5] == 0x49 && header[6] == 0x74)
      return Format.StuffIt;

    // ZPAQ: "zPQ"
    if (header[0] == 0x7A && header[1] == 0x50 && header[2] == 0x51)
      return Format.Zpaq;

    // DMS: "DMS!" (0x444D5321)
    if (header[0] == 0x44 && header[1] == 0x4D && header[2] == 0x53 && header[3] == 0x21)
      return Format.Dms;

    // Amiga LZX: "LZX" (0x4C5A58)
    if (header[0] == 0x4C && header[1] == 0x5A && header[2] == 0x58)
      return Format.LzxAmiga;

    // PowerPacker: "PP20" (0x50503230)
    if (header[0] == 0x50 && header[1] == 0x50 && header[2] == 0x32 && header[3] == 0x30)
      return Format.PowerPacker;

    // ICE Packer: "Ice!" or "ICE!"
    if (header[0] == 0x49 && (header[1] == 0x63 || header[1] == 0x43) &&
        (header[2] == 0x65 || header[2] == 0x45) && header[3] == 0x21)
      return Format.IcePacker;

    // Squeeze: 0xFF76 (LE)
    if (header[0] == 0x76 && header[1] == 0xFF)
      return Format.Squeeze;

    // UHARC: "UHA"
    if (header[0] == 0x55 && header[1] == 0x48 && header[2] == 0x41)
      return Format.Uharc;

    // RZIP: "RZIP"
    if (header[0] == 0x52 && header[1] == 0x5A && header[2] == 0x49 && header[3] == 0x50)
      return Format.Rzip;

    // WAD: "IWAD" or "PWAD"
    if ((header[0] == 0x49 || header[0] == 0x50) && header[1] == 0x57 && header[2] == 0x41 && header[3] == 0x44)
      return Format.Wad;

    // BinHex: check for "(This file" text
    if (header.Length >= 20 && header[0] == '(' && header[1] == 'T' && header[2] == 'h' && header[3] == 'i')
      return Format.BinHex;

    // TAR: "ustar" at offset 257
    if (header.Length >= 263 &&
        header[257] == 'u' && header[258] == 's' && header[259] == 't' &&
        header[260] == 'a' && header[261] == 'r')
      return Format.Tar;

    return Format.Unknown;
  }

  internal static Format Detect(string path) {
    var byExt = DetectByExtension(path);
    if (byExt != Format.Unknown) return byExt;

    if (!File.Exists(path)) return Format.Unknown;

    var header = new byte[512];
    using var fs = File.OpenRead(path);
    var read = fs.Read(header, 0, header.Length);
    return DetectByMagic(header.AsSpan(0, read));
  }

  /// <summary>
  /// For SFX files, returns the embedded archive's format and offset.
  /// Handles both our own SFX format (SFX! trailer) and third-party SFX (PE overlay scan).
  /// Returns null if the file is not an SFX or contains no detectable archive.
  /// </summary>
  internal static (Format ArchiveFormat, long Offset, long Length)? GetSfxArchiveInfo(string path) {
    // First check our own SFX trailer
    var ownSfx = SfxBuilder.ReadTrailer(path);
    if (ownSfx != null)
      return (ownSfx.Value.Format, ownSfx.Value.Offset, ownSfx.Value.Length);

    // Then check for third-party SFX via PE overlay
    var peInfo = PeOverlay.DetectEmbeddedArchive(path);
    if (peInfo != null) {
      var fileLen = new FileInfo(path).Length;
      return (peInfo.Value.Format, peInfo.Value.Offset, fileLen - peInfo.Value.Offset);
    }

    return null;
  }

  internal static string GetDefaultExtension(Format format) => format switch {
    Format.Zip => ".zip",
    Format.Rar => ".rar",
    Format.SevenZip => ".7z",
    Format.Tar => ".tar",
    Format.Cab => ".cab",
    Format.Lzh => ".lzh",
    Format.Arj => ".arj",
    Format.Arc => ".arc",
    Format.Zoo => ".zoo",
    Format.Ace => ".ace",
    Format.Sqx => ".sqx",
    Format.Cpio => ".cpio",
    Format.Ar => ".ar",
    Format.Wim => ".wim",
    Format.Gzip => ".gz",
    Format.Bzip2 => ".bz2",
    Format.Xz => ".xz",
    Format.Zstd => ".zst",
    Format.Lz4 => ".lz4",
    Format.Brotli => ".br",
    Format.Snappy => ".sz",
    Format.Lzop => ".lzo",
    Format.Compress => ".Z",
    Format.Lzma => ".lzma",
    Format.Lzip => ".lz",
    Format.Zlib => ".zlib",
    Format.Szdd => ".sz_",
    Format.TarGz => ".tar.gz",
    Format.TarBz2 => ".tar.bz2",
    Format.TarXz => ".tar.xz",
    Format.TarZst => ".tar.zst",
    Format.Shar => ".shar",
    Format.Pak => ".pak",
    Format.Iso => ".iso",
    Format.Udf => ".udf",
    Format.Ha => ".ha",
    Format.Nsis => ".exe",
    Format.InnoSetup => ".exe",
    Format.SquashFs => ".squashfs",
    Format.CramFs => ".cramfs",
    Format.StuffIt => ".sit",
    Format.Zpaq => ".zpaq",
    Format.Sfx => ".exe",
    Format.Dms => ".dms",
    Format.LzxAmiga => ".lzx",
    Format.PowerPacker => ".pp",
    Format.CompactPro => ".cpt",
    Format.MacBinary => ".bin",
    Format.BinHex => ".hqx",
    Format.Spark => ".spk",
    Format.Lbr => ".lbr",
    Format.Squeeze => ".sqz",
    Format.IcePacker => ".ice",
    Format.Uharc => ".uha",
    Format.Rzip => ".rz",
    Format.Wad => ".wad",
    Format.PackBits => ".packbits",
    Format.Yaz0 => ".yaz0",
    Format.BriefLz => ".blz",
    Format.Rnc => ".rnc",
    Format.RefPack => ".qfs",
    Format.ApLib => ".aplib",
    Format.Lzfse => ".lzfse",
    Format.Freeze => ".freeze",
    Format.Xar => ".xar",
    Format.AlZip => ".alz",
    Format.TarLz4 => ".tar.lz4",
    Format.TarLzip => ".tar.lz",
    Format.TarBr => ".tar.br",
    Format.Vpk => ".vpk",
    Format.Bsa => ".bsa",
    Format.Mpq => ".mpq",
    Format.UuEncoding => ".uue",
    Format.YEnc => ".yenc",
    Format.Density => ".density",
    _ => "",
  };
}
