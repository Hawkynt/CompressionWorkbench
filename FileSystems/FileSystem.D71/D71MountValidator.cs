#pragma warning disable CS1591
using System.Text;

namespace FileSystem.D71;

/// <summary>
/// Strict mount-time validation for the ordinary 1571 CBM DOS namespace that
/// the writable session can mutate without inventing REL, extended-directory,
/// or non-standard allocation semantics.
/// </summary>
public static class D71MountValidator {
  private const int SectorSize = 256;
  private const int TotalTracks = 70;
  private const int DirectoryTrack = 18;
  private const int Side2BamTrack = 53;
  private const int BamSector = 0;
  private const int DirectoryStartSector = 1;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  public sealed record ValidationResult(bool CanRead, bool CanWrite, IReadOnlyList<string> Limitations);

  public static ValidationResult Validate(ReadOnlySpan<byte> image) {
    var limitations = new List<string>();
    if (image.Length < D71BlockDevice.DataLength)
      return new ValidationResult(false, false, ["Image is shorter than the 70-track D71 data area."]);

    var claimed = new HashSet<(int Track, int Sector)> {
      (DirectoryTrack, BamSector),
      (Side2BamTrack, BamSector),
    };
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var writable = true;

    try {
      var bam1Offset = SectorOffset(DirectoryTrack, BamSector);
      if (image[bam1Offset + 2] is not (0x00 or 0x41)) {
        writable = false;
        limitations.Add($"BAM DOS-version byte is 0x{image[bam1Offset + 2]:X2}; 1571 DOS treats values other than 00/41 as soft write protection.");
      }
      if (image[bam1Offset + 3] != 0x80) {
        writable = false;
        limitations.Add($"D71 double-sided BAM flag is 0x{image[bam1Offset + 3]:X2}; canonical 1571 media uses 80.");
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
          limitations.Add("The directory extends off track 18; real 1571 DOS can read such images but cannot safely update them.");
        }

        var offset = SectorOffset(track, sector);
        for (var slot = 0; slot < 8; ++slot) {
          var entry = offset + slot * 32;
          var fileType = image[entry + 2];
          var baseType = fileType & 0x0F;
          if (baseType == 0) continue;
          if (baseType > 4) {
            writable = false;
            limitations.Add($"Directory entry {slot} has illegal 1571 file type {baseType}.");
          } else if (baseType == 4) {
            writable = false;
            limitations.Add("REL files use side-sector metadata that the writable session does not model.");
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
            if (fileTrack is DirectoryTrack or Side2BamTrack) {
              writable = false;
              limitations.Add($"File '{name}' uses reserved metadata track {fileTrack}; the native allocator will not mutate that layout.");
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

      // Native 1571 DOS dedicates track 53 to the side-2 BAM. The remaining
      // sectors are intentionally wasted and marked allocated in the BAM.
      var expectedAllocated = new HashSet<(int Track, int Sector)>(claimed);
      for (var sector53 = 0; sector53 < SectorsPerTrack[Side2BamTrack]; ++sector53)
        expectedAllocated.Add((Side2BamTrack, sector53));

      var bam2Offset = SectorOffset(Side2BamTrack, BamSector);
      for (var t = 1; t <= TotalTracks; ++t) {
        var freeCount = 0;
        var recordedCount = t <= 35
          ? image[bam1Offset + 4 + (t - 1) * 4]
          : image[bam1Offset + 0xDD + (t - 36)];

        for (var s = 0; s < SectorsPerTrack[t]; ++s) {
          bool isFree;
          if (t <= 35) {
            var entry = bam1Offset + 4 + (t - 1) * 4;
            isFree = (image[entry + 1 + s / 8] & (1 << (s & 7))) != 0;
          } else {
            var entry = bam2Offset + (t - 36) * 3;
            isFree = (image[entry + s / 8] & (1 << (s & 7))) != 0;
          }
          if (isFree) ++freeCount;
          var shouldBeFree = !expectedAllocated.Contains((t, s));
          if (isFree != shouldBeFree) {
            writable = false;
            limitations.Add(
              $"BAM ownership mismatch at track {t}, sector {s} (BAM says {(isFree ? "free" : "allocated")}, namespace says {(shouldBeFree ? "free" : "used/reserved")}).");
          }
        }

        if (recordedCount != freeCount) {
          writable = false;
          limitations.Add($"BAM free-count mismatch on track {t}: header={recordedCount}, bitmap={freeCount}.");
        }
      }
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) {
      return new ValidationResult(false, false, [ex.Message]);
    }

    return new ValidationResult(true, writable, limitations.Distinct().ToArray());
  }

  private static void ValidateSector(int track, int sector) {
    if (track < 1 || track > TotalTracks || sector < 0 || sector >= SectorsPerTrack[track])
      throw new InvalidDataException($"Invalid 1571 track/sector link {track}/{sector}.");
  }

  private static int SectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; ++t) offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0xA0);
    if (end < 0) end = bytes.Length;
    return Encoding.ASCII.GetString(bytes[..end]);
  }
}
