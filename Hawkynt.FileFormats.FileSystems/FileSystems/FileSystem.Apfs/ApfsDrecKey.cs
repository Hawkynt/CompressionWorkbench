#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// The key of a directory record: which directory, and the name in it.
/// </summary>
/// <remarks>
/// <para>APFS has two of these. A volume that folds names — case-insensitively, or
/// by Unicode normalisation — stores the name's length and a hash of it together in
/// four bytes, and orders the directory by that hash. A volume that keeps names as
/// given stores the length alone, in two, and orders by the bytes.</para>
///
/// <para>Which one is on disk is not a choice a writer gets to make per record: it
/// follows from the volume's incompatible-features word, and a reader takes the
/// header length from that word alone. Writing the hashed form on a volume that
/// declares neither feature leaves every name two bytes longer than the key says it
/// is, and a directory that reads as corrupt.</para>
///
/// <para>This writes the plain form, because the volumes here fold nothing.</para>
/// </remarks>
internal static class ApfsDrecKey {

  /// <summary>Bytes before the name in a key that carries only its length.</summary>
  internal const int HeaderLength = 10;

  /// <summary>Builds the key naming <paramref name="name" /> inside a directory.</summary>
  internal static byte[] Build(ulong parentOid, string name) {
    ArgumentNullException.ThrowIfNull(name);

    // The stored length counts the terminator, which the name carries on disk.
    var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
    var key = new byte[HeaderLength + nameBytes.Length];
    BinaryPrimitives.WriteUInt64LittleEndian(key, parentOid | ((ulong)APFS_TYPE_DIR_REC << 60));
    BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(8), (ushort)nameBytes.Length);
    nameBytes.CopyTo(key, HeaderLength);
    return key;
  }

  /// <summary>Reads the name out of a key, or returns false when the key is not one.</summary>
  internal static bool TryReadName(ReadOnlySpan<byte> key, out string name) {
    name = string.Empty;
    if (key.Length < HeaderLength) return false;

    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(key[8..]);
    if (nameLength <= 0 || HeaderLength + nameLength > key.Length) return false;

    name = Encoding.UTF8.GetString(key.Slice(HeaderLength, nameLength)).TrimEnd('\0');
    return true;
  }
}
