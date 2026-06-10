#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Hog;

/// <summary>
/// In-place HOG archive modifier. The Descent I/II HOG container has the
/// trivial structure {3-byte "DHF" magic, then sequence of [13-byte name +
/// 4-byte LE size + size bytes of data]} — no directory, no offsets, just
/// a chain. That makes Add an O(touched bytes) pure append at EOF, and
/// Remove a contiguous-shift operation over the tail.
///
/// <para><b>Byte-identity contract:</b> AddFile preserves
/// <c>[0, oldLength)</c> byte-identical — it only writes new bytes at
/// <c>oldLength</c> and beyond. RemoveFile shifts everything after the
/// removed entry forward, then truncates.</para>
/// </summary>
public static class HogModifier {

  // HOG record layout: 13-byte ASCII null-padded name + 4-byte LE size.
  private const int NameLength = 13;
  private const int SizeFieldLength = 4;
  private const int RecordHeaderLength = NameLength + SizeFieldLength; // 17
  private static readonly byte[] Magic = "DHF"u8.ToArray();
  private const int MagicLength = 3;

  /// <summary>
  /// Appends a file to a HOG archive in place. Writes a 13-byte name + 4-byte
  /// LE size + data at EOF; bytes <c>[0, oldLength)</c> are byte-identical
  /// afterwards (pure append).
  /// </summary>
  /// <exception cref="ArgumentException">Empty stream (no "DHF" magic).</exception>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    EnsureValidHogMagic(archive);

    archive.Position = archive.Length;

    Span<byte> header = stackalloc byte[RecordHeaderLength];
    header.Clear();

    // 13-byte name field, null-padded; truncate to fit on overflow.
    var truncated = name.Length > NameLength ? name[..NameLength] : name;
    var nameBytes = Encoding.ASCII.GetBytes(truncated);
    nameBytes.AsSpan().CopyTo(header[..NameLength]);

    BinaryPrimitives.WriteUInt32LittleEndian(header[NameLength..], (uint)data.Length);

    archive.Write(header);
    if (data.Length > 0) archive.Write(data);
  }

  /// <summary>
  /// Removes the first entry matching <paramref name="name"/> from the HOG
  /// archive. Walks the record chain, then shifts everything after the
  /// matched record's data block toward offset 0 and truncates.
  /// Returns false if no such entry is present.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);

    EnsureValidHogMagic(archive);

    // Load the whole image into memory — HOG archives are small (Descent ships
    // ≈3-15 MB max). This keeps the shift operation simple and atomic.
    archive.Position = 0;
    var image = new byte[archive.Length];
    archive.ReadExactly(image);

    var pos = MagicLength;
    while (pos + RecordHeaderLength <= image.Length) {
      // Parse the name from the 13-byte name field.
      var nameSpan = image.AsSpan(pos, NameLength);
      var nullIdx = nameSpan.IndexOf((byte)0);
      var nameLen = nullIdx < 0 ? NameLength : nullIdx;
      var entryName = Encoding.ASCII.GetString(nameSpan[..nameLen]);

      var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pos + NameLength));
      var dataStart = pos + RecordHeaderLength;
      var dataEnd = dataStart + (int)size;
      if (dataEnd > image.Length) return false; // truncated record — corrupt image

      if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        var tailLength = image.Length - dataEnd;
        if (tailLength > 0)
          Array.Copy(image, dataEnd, image, pos, tailLength);
        var newLength = pos + tailLength;
        archive.Position = 0;
        archive.Write(image, 0, newLength);
        archive.SetLength(newLength);
        return true;
      }

      pos = dataEnd;
    }

    return false;
  }

  /// <summary>
  /// Creates a fresh empty HOG archive — emits the 3-byte "DHF" magic with no
  /// entries. Used when modifying an empty stream.
  /// </summary>
  public static void InitializeEmpty(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    archive.Write(Magic);
    archive.SetLength(MagicLength);
  }

  private static void EnsureValidHogMagic(Stream archive) {
    if (archive.Length < MagicLength) {
      InitializeEmpty(archive);
      return;
    }
    archive.Position = 0;
    Span<byte> magic = stackalloc byte[MagicLength];
    archive.ReadExactly(magic);
    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException(
        $"Stream is not a HOG archive (expected \"DHF\" magic, got \"{Encoding.ASCII.GetString(magic)}\").");
  }
}
