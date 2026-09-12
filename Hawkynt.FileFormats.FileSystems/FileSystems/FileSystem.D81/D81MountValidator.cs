#pragma warning disable CS1591
using System.Text;

namespace FileSystem.D81;

/// <summary>
/// Strict mount-time validation for the ordinary 1581 CBM DOS namespace that
/// the writable session can mutate without REL, partition/subdirectory, or
/// extended-directory semantics.
/// </summary>
public static class D81MountValidator {
  private const int SectorSize = 256;
  private const int SectorsPerTrack = 40;
  private const int TotalTracks = 80;
  private const int DirectoryTrack = 40;
  private const int HeaderSector = 0;
  private const int Bam1Sector = 1;
  private const int Bam2Sector = 2;
  private const int DirectoryStartSector = 3;
  private const int BamEntriesStart = 16;
  private const int BamEntrySize = 6;

  public sealed record ValidationResult(bool CanRead, bool CanWrite, IReadOnlyList<string> Limitations);

  public static ValidationResult Validate(ReadOnlySpan<byte> image) {
    var limitations = new List<string>();
    if (image.Length < D81BlockDevice.DataLength)
      return new ValidationResult(false, false, ["Image is shorter than the 80-track D81 data area."]);

    var claimed = new HashSet<(int Track, int Sector)> {
      (DirectoryTrack, HeaderSector),
      (DirectoryTrack, Bam1Sector),
      (DirectoryTrack, Bam2Sector),
    };
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var writable = true;

    try {
      var headerOffset = SectorOffset(DirectoryTrack, HeaderSector);
      var bam1Offset = SectorOffset(DirectoryTrack, Bam1Sector);
      var bam2Offset = SectorOffset(DirectoryTrack, Bam2Sector);

      if (image[headerOffset + 2] is not (0x00 or 0x44)) {
        writable = false;
        limitations.Add($"Header DOS-version byte is 0x{image[headerOffset + 2]:X2}; canonical 1581 media uses 00/44.");
      }
      if (image[headerOffset + 3] != 0x00) {
        writable = false;
        limitations.Add($"Header reserved byte 03 is 0x{image[headerOffset + 3]:X2}; canonical 1581 media uses 00.");
      }
      if (image[bam1Offset] != DirectoryTrack || image[bam1Offset + 1] != Bam2Sector) {
        writable = false;
        limitations.Add("First BAM sector does not link to 40/2.");
      }
      if (image[bam2Offset] != 0x00 || image[bam2Offset + 1] != 0xFF) {
        writable = false;
        limitations.Add("Second BAM sector does not terminate with 00/FF.");
      }
      if (image[bam1Offset + 2] != 0x44 || image[bam1Offset + 3] != 0xBB ||
          image[bam2Offset + 2] != 0x44 || image[bam2Offset + 3] != 0xBB) {
        writable = false;
        limitations.Add("BAM version/complement bytes are not the canonical 44/BB pair.");
      }
      var id0 = image[headerOffset + 0x16];
      var id1 = image[headerOffset + 0x17];
      if (image[bam1Offset + 4] != id0 || image[bam1Offset + 5] != id1 ||
          image[bam2Offset + 4] != id0 || image[bam2Offset + 5] != id1) {
        writable = false;
        limitations.Add("Header and BAM disk IDs disagree.");
      }

      var track = DirectoryTrack;
      var sector = DirectoryStartSector;
      var visitedDirectory = new HashSet<(int, int)>();
      while (track != 0) {
        ValidateSector(track, sector);
        if (!visitedDirectory.Add((track, sector)))
          throw new InvalidDataException("Directory chain contains a loop.");
        if (!claimed.Add((track, sector)))
          throw new InvalidDataException("Directory sector overlaps another live object.");
        if (track != DirectoryTrack) {
          writable = false;
          limitations.Add("The directory extends off track 40; writable mounting is refused for non-standard directory placement.");
        }

        var offset = SectorOffset(track, sector);
        for (var slot = 0; slot < 8; ++slot) {
          var entry = offset + slot * 32;
          var fileType = image[entry + 2];
          var baseType = fileType & 0x0F;
          if (baseType == 0) continue;
          if (baseType > 5) {
            writable = false;
            limitations.Add($"Directory entry {slot} has illegal 1581 file type {baseType}.");
          } else if (baseType == 4) {
            writable = false;
            limitations.Add("REL files use 1581 super-side-sector metadata that the writable session does not model.");
          } else if (baseType == 5) {
            throw new InvalidDataException(
              "D81 contains a CBM partition/subdirectory entry; the current flat mounted namespace cannot represent it losslessly.");
          }
          if ((fileType & 0x80) == 0) {
            writable = false;
            limitations.Add("An unclosed/splat file is present; writable mounting is refused until recovery semantics are modeled.");
          }

          var name = DecodeName(image.Slice(entry + 5, 16));
          if (!names.Add(name))
            throw new InvalidDataException($"Duplicate directory name '{name}' makes lookup ambiguous.");

          var fileTrack = image[entry + 3];
          var fileSector = image[entry + 4];
          var visitedFile = new HashSet<(int, int)>();
          while (fileTrack != 0) {
            ValidateSector(fileTrack, fileSector);
            if (fileTrack == DirectoryTrack) {
              writable = false;
              limitations.Add($"File '{name}' uses directory track 40; the native allocator reserves that track for filesystem metadata.");
            }
            if (!visitedFile.Add((fileTrack, fileSector)))
              throw new InvalidDataException($"File '{name}' contains a sector-chain loop.");
            if (!claimed.Add((fileTrack, fileSector)))
              throw new InvalidDataException($"File '{name}' overlaps another live sector.");
            var fileOffset = SectorOffset(fileTrack, fileSector);
            fileTrack = image[fileOffset];
            fileSector = image[fileOffset + 1];
          }
        }

        var nextTrack = image[offset];
        var nextSector = image[offset + 1];
        if (nextTrack == 0) break;
        track = nextTrack;
        sector = nextSector;
      }

      for (var t = 1; t <= TotalTracks; ++t) {
        var bamOffset = t <= 40 ? bam1Offset : bam2Offset;
        var localTrack = t <= 40 ? t - 1 : t - 41;
        var entry = bamOffset + BamEntriesStart + localTrack * BamEntrySize;
        var freeCount = 0;
        for (var s = 0; s < SectorsPerTrack; ++s) {
          var isFree = (image[entry + 1 + s / 8] & (1 << (s & 7))) != 0;
          if (isFree) ++freeCount;
          var shouldBeFree = !claimed.Contains((t, s));
          if (isFree != shouldBeFree) {
            writable = false;
            limitations.Add(
              $"BAM ownership mismatch at track {t}, sector {s} (BAM says {(isFree ? "free" : "allocated")}, namespace says {(shouldBeFree ? "free" : "used")}).");
          }
        }
        if (image[entry] != freeCount) {
          writable = false;
          limitations.Add($"BAM free-count mismatch on track {t}: header={image[entry]}, bitmap={freeCount}.");
        }
      }
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) {
      return new ValidationResult(false, false, [ex.Message]);
    }

    return new ValidationResult(true, writable, limitations.Distinct().ToArray());
  }

  private static void ValidateSector(int track, int sector) {
    if (track is < 1 or > TotalTracks || sector is < 0 or >= SectorsPerTrack)
      throw new InvalidDataException($"Invalid 1581 track/sector link {track}/{sector}.");
  }

  private static int SectorOffset(int track, int sector)
    => ((track - 1) * SectorsPerTrack + sector) * SectorSize;

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0xA0);
    if (end < 0) end = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..end]);
  }
}
