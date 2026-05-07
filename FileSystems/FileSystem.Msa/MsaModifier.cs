#pragma warning disable CS1591
using FileSystem.Fat;

namespace FileSystem.Msa;

/// <summary>
/// Modifier for Atari ST <b>MSA (Magic Shadow Archiver)</b> disk images. MSA is a
/// <em>two-level container</em>: an outer RLE-compressed track wrapper around an
/// inner FAT12 filesystem image (track-aligned dump of a standard ST floppy).
///
/// <para>Because each track is RLE-compressed independently, true byte-level
/// in-place modification is impossible — any change to the inner FAT image
/// shifts the compressed lengths of one or more tracks and therefore the entire
/// rest of the file. The honest path is <b>decode → modify FAT → re-encode</b>,
/// which is what this class does:</para>
/// <list type="number">
///   <item>Read MSA header, decompress all tracks into a flat FAT12 image
///         (size = (end-start+1) × sides × SPT × 512 bytes).</item>
///   <item>Apply the FAT modifier op (<see cref="FatRemover"/> for remove,
///         <see cref="FatReader"/> + <see cref="FatWriter"/> rebuild for add)
///         on the flat image, preserving total-sectors.</item>
///   <item>Re-encode the flat image back to MSA tracks via
///         <see cref="MsaWriter.Write"/>, preserving the <em>original geometry</em>
///         (SPT, sides, start/end track) so subsequent round-trips remain stable.</item>
/// </list>
///
/// <para>Operates on the entire image — there is no streaming sector access
/// because the RLE codec is per-track and forces a full track decode anyway.
/// For a 1.44 MB ST floppy that's ~1.4 MB of memory traffic per call, which is
/// fine for the use case (interactive disk editing, test harnesses).</para>
/// </summary>
public static class MsaModifier {

  /// <summary>
  /// Adds a file to the FAT12 filesystem inside an MSA image. The image is
  /// fully decoded, the FAT layer is rebuilt with the existing entries plus
  /// the new one (matching <c>FatFormatDescriptor.Add</c>'s strategy — FatWriter
  /// is build-from-scratch by design), and the result is re-compressed track
  /// by track with the original MSA geometry.
  /// </summary>
  /// <exception cref="ArgumentNullException">Any argument is null.</exception>
  /// <exception cref="InvalidDataException">Outer MSA header is malformed or the
  /// inner FAT12 layer cannot be parsed.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (flat, geom) = DecodeToFlatImage(image);

    // Rebuild the FAT image with the existing entries plus the new file.
    // We can't call into FatWriter for "add to existing" because the writer is
    // build-from-scratch — mirror what FatFormatDescriptor.Add does (read all
    // entries, append the new one, rebuild preserving total_sectors).
    using var flatStream = new MemoryStream(flat, writable: false);
    var reader = new FatReader(flatStream);
    var combined = new FatWriter();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      combined.AddFile(entry.Name, reader.Extract(entry));
    }
    combined.AddFile(name, data);
    var totalSectors = flat.Length / 512;
    var rebuilt = combined.Build(totalSectors: totalSectors);

    // Pad / truncate to the exact original flat-image size so the MSA geometry
    // stays correct on re-encode. (FatWriter.Build returns totalSectors*512.)
    if (rebuilt.Length != flat.Length) {
      var sized = new byte[flat.Length];
      Array.Copy(rebuilt, sized, Math.Min(rebuilt.Length, sized.Length));
      rebuilt = sized;
    }

    EncodeFromFlatImage(image, rebuilt, geom);
  }

  /// <summary>
  /// Removes the named file from the FAT12 filesystem inside an MSA image.
  /// Uses <see cref="FatRemover.Remove"/> for the inner-layer wipe (zeros
  /// cluster bytes + cluster-tip slack + directory entry + FAT entries in every
  /// FAT copy, leaving no forensic trace of the filename or content), then
  /// re-encodes the modified flat image back to MSA tracks with the original
  /// geometry.
  /// </summary>
  /// <returns>True if the file existed and was removed; false otherwise.</returns>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var (flat, geom) = DecodeToFlatImage(image);

    // FatRemover.Remove throws FileNotFoundException if the entry is absent;
    // catch that to honor the bool-return contract callers expect from the
    // sibling D64 / Mfs / CpcDsk modifiers.
    try {
      FatRemover.Remove(flat, name);
    } catch (FileNotFoundException) {
      return false;
    }

    _ = wipeData; // FatRemover always wipes — there is no "leave bytes" mode in FAT today.

    EncodeFromFlatImage(image, flat, geom);
    return true;
  }

  // ── MSA codec wrapping ────────────────────────────────────────────────

  /// <summary>
  /// Geometry captured from the original MSA header. Re-used on encode so the
  /// output preserves SPT / sides / start-track / end-track (and therefore the
  /// flat-image size) exactly.
  /// </summary>
  private readonly record struct Geometry(ushort SectorsPerTrack, ushort Sides, ushort StartTrack, ushort EndTrack);

  private static (byte[] Flat, Geometry Geom) DecodeToFlatImage(Stream image) {
    image.Position = 0;
    var reader = new MsaReader(image);
    if (reader.Entries.Count == 0)
      throw new InvalidDataException("MSA: no decoded disk payload.");
    var flat = reader.Extract(reader.Entries[0]);
    return (flat, new Geometry(reader.SectorsPerTrack, reader.Sides, reader.StartTrack, reader.EndTrack));
  }

  private static void EncodeFromFlatImage(Stream image, byte[] flat, Geometry geom) {
    using var ms = new MemoryStream();
    MsaWriter.Write(ms, flat, geom.SectorsPerTrack, geom.Sides);
    var rebuilt = ms.ToArray();
    image.Position = 0;
    image.Write(rebuilt, 0, rebuilt.Length);
    image.SetLength(rebuilt.Length);
  }
}
