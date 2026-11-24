#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Crypto;

namespace FileFormat.Uif;

/// <summary>
/// Writes a version-1, type-8 UIF (an ISO image) the way MagicISO 5.5 writes one: the image cut into
/// 128-sector runs, all-zero sectors as zero-fill entries and everything else zlib-compressed, a
/// zlib-compressed <c>blhr</c> table and a <c>bbis</c> trailer carrying the MD5 of everything before it.
/// With a password the runs and the table are DES-encrypted and a <c>bsdr</c> block holds the encrypted
/// trailer, as MagicISO's password protection does. uif2iso and MagicISO 5.5.281 both decode what this
/// writes to the original image.
/// </summary>
public static class UifWriter {
  public const int SectorSize = 2048;
  /// <summary>MagicISO starts a new run at every 128th sector.</summary>
  public const int RunSectors = 128;

  /// <summary>Compresses <paramref name="length"/> bytes of <paramref name="image"/> into a UIF at <paramref name="output"/>.</summary>
  /// <exception cref="ArgumentException">The password is longer than MagicISO allows (31 bytes) or not Latin-1.</exception>
  public static void Write(Stream output, Stream image, long length, string? password = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(image);
    ArgumentOutOfRangeException.ThrowIfNegative(length);
    TripleDes? cipher = null;
    if (password is not null) {
      if (password.Length == 0) throw new ArgumentException("UIF: an empty password protects nothing.", nameof(password));
      if (password.Length > UifImage.MaxPasswordLength) throw new ArgumentException($"UIF: MagicISO takes at most {UifImage.MaxPasswordLength} password characters.", nameof(password));
      if (password.Any(c => c > 0xFF)) throw new ArgumentException("UIF: the password must be Latin-1 (MagicISO stores single bytes).", nameof(password));
      cipher = new TripleDes(UifImage.PasswordKey(password));
    }
    var sectors = (length + SectorSize - 1) / SectorSize;
    if (sectors > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "UIF: too many sectors.");
    var lastDiff = (uint)(length % SectorSize);

    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    var start = output.Position;
    void Emit(ReadOnlySpan<byte> bytes) {
      output.Write(bytes);
      md5.AppendData(bytes);
    }

    var entries = new List<byte[]>();
    void Entry(long offset, int stored, long sector, long count, uint type) {
      var e = new byte[UifImage.EntrySize];
      BinaryPrimitives.WriteUInt64LittleEndian(e, (ulong)offset);
      BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)stored);
      BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)sector);
      BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(16), (uint)count);
      BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(20), type);
      entries.Add(e);
    }

    var buffer = new byte[RunSectors * SectorSize];
    for (var first = 0L; first < sectors; first += RunSectors) {
      var count = (int)Math.Min(RunSectors, sectors - first);
      Array.Clear(buffer);
      var want = (int)Math.Min((long)count * SectorSize, length - first * SectorSize);
      image.ReadExactly(buffer, 0, want);
      // Split the run into stretches of all-zero sectors (zero-fill entries) and data (compressed entries).
      for (var s = 0; s < count;) {
        var zero = !buffer.AsSpan(s * SectorSize, SectorSize).ContainsAnyExcept((byte)0);
        var e = s + 1;
        while (e < count && !buffer.AsSpan(e * SectorSize, SectorSize).ContainsAnyExcept((byte)0) == zero) ++e;
        if (zero) Entry(0, 0, first + s, e - s, 3);
        else {
          var packed = FileFormat.Zlib.ZlibStream.Compress(buffer.AsSpan(s * SectorSize, (e - s) * SectorSize));
          if (cipher is not null) UifImage.Crypt(cipher, packed, decrypt: false);
          Entry(output.Position - start, packed.Length, first + s, e - s, 5);
          Emit(packed);
        }
        s = e;
      }
    }

    var table = new byte[entries.Count * UifImage.EntrySize];
    for (var i = 0; i < entries.Count; ++i) entries[i].CopyTo(table, i * UifImage.EntrySize);
    var packedTable = FileFormat.Zlib.ZlibStream.Compress(table);
    if (cipher is not null) UifImage.Crypt(cipher, packedTable, decrypt: false);

    var tableAt = output.Position - start;
    var trailerSpan = packedTable.Length + 16 + UifImage.TrailerSize;
    if (cipher is not null) {
      // The real trailer, encrypted, ahead of the table: marked protected, its size and hash left zero.
      var inner = Trailer((uint)sectors, lastDiff, tableAt, 0, new byte[16], protectedFlag: true);
      UifImage.Crypt(cipher, inner, decrypt: false);
      var bsdr = new byte[8];
      "bsdr"u8.CopyTo(bsdr);
      BinaryPrimitives.WriteUInt32LittleEndian(bsdr.AsSpan(4), UifImage.TrailerSize);
      Emit(bsdr);
      Emit(inner);
      trailerSpan += UifImage.PasswordBlockSize;
    }
    var head = new byte[16];
    "blhr"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)packedTable.Length + 8);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)entries.Count);
    Emit(head);
    Emit(packedTable);
    output.Write(Trailer((uint)sectors, lastDiff, tableAt, (uint)trailerSpan, md5.GetHashAndReset(), protectedFlag: cipher is not null));
  }

  private static byte[] Trailer(uint sectors, uint lastDiff, long tableAt, uint span, byte[] hash, bool protectedFlag) {
    var t = new byte[UifImage.TrailerSize];
    "bbis"u8.CopyTo(t);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), UifImage.TrailerSize);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(10), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(12), protectedFlag ? (ushort)1 : (ushort)0);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(16), sectors);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(20), SectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(24), lastDiff);
    BinaryPrimitives.WriteUInt64LittleEndian(t.AsSpan(28), (ulong)tableAt);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(36), span);
    hash.CopyTo(t, 40);
    return t;
  }
}
