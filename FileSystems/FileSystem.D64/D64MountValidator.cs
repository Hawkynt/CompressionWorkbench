#pragma warning disable CS1591
namespace FileSystem.D64;

/// <summary>
/// Strict mount-time validation for the subset of CBM DOS 2.6 that the
/// namespace driver can mutate losslessly. Read-only parsing may accept more;
/// writable mounting requires exact BAM ownership with no overlapping/orphaned
/// sectors, closed files, and no REL side-sector semantics.
/// </summary>
public static class D64MountValidator {
  private const int SectorSize = 256;
  private const int TotalTracks = 35;
  private const int DirectoryTrack = 18;
  private const int BamSector = 0;
  private const int DirectoryStartSector = 1;
  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
  ];

  public sealed record ValidationResult(bool CanRead, bool CanWrite, IReadOnlyList<string> Limitations);

  public static ValidationResult Validate(ReadOnlySpan<byte> image) {
    var limitations = new List<string>();
    if (image.Length < D64BlockDevice.DataLength)
      return new ValidationResult(false, false, ["Image is shorter than the 35-track D64 data area."]);

    var used = new HashSet<(int Track, int Sector)> { (DirectoryTrack, BamSector) };
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var writable = true;

    try {
      var track = DirectoryTrack;
      var sector = DirectoryStartSector;
      var visitedDirectory = new HashSet<(int, int)>();
      while (track != 0) {
        ValidateSector(track, sector);
        if (!visitedDirectory.Add((track, sector)))
          throw new InvalidDataException("Directory chain contains a loop.");
        if (!used.Add((track, sector)))
          throw new InvalidDataException("Directory sector overlaps another live object.");
        var offset = SectorOffset(track, sector);

        for (var slot = 0; slot < 8; ++slot) {
          var entry = offset + slot * 32;
          var fileType = image[entry + 2];
          var baseType = fileType & 0x07;
          if (baseType == 0) continue;
          if (baseType > 4) {
            writable = false;
            limitations.Add($"Directory entry {slot} has unsupported CBM DOS file type {baseType}.");
          }
          if (baseType == 4) {
            writable = false;
            limitations.Add("REL files use side-sector metadata that the writable session does not yet model.");
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
              limitations.Add($"File '{name}' allocates track 18, which the current allocator reserves for BAM/directory metadata.");
            }
            if (!visitedFile.Add((fileTrack, fileSector)))
              throw new InvalidDataException($"File '{name}' contains a sector-chain loop.");
            if (!used.Add((fileTrack, fileSector)))
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

      var bamOffset = SectorOffset(DirectoryTrack, BamSector);
      for (var t = 1; t <= TotalTracks; ++t) {
        var entry = bamOffset + 4 + (t - 1) * 4;
        var freeCount = 0;
        for (var s = 0; s < SectorsPerTrack[t]; ++s) {
          var isFree = (image[entry + 1 + s / 8] & (1 << (s & 7))) != 0;
          if (isFree) freeCount++;
          var shouldBeFree = !used.Contains((t, s));
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
    if (track < 1 || track > TotalTracks || sector < 0 || sector >= SectorsPerTrack[track])
      throw new InvalidDataException($"Invalid 1541 track/sector link {track}/{sector}.");
  }

  private static int SectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; ++t) offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  private static string DecodeName(ReadOnlySpan<byte> bytes) {
    var end = bytes.IndexOf((byte)0xA0);
    if (end < 0) end = bytes.Length;
    return System.Text.Encoding.ASCII.GetString(bytes[..end]);
  }
}
