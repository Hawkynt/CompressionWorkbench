#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Extended attributes as bcachefs keeps them: one <c>KEY_TYPE_xattr</c> per
/// attribute in the xattrs tree, at the owning inode and a hash of the name.
/// </summary>
/// <remarks>
/// <para>The value is <c>x_type</c> (the namespace), <c>x_name_len</c>,
/// <c>x_val_len</c> (little-endian 16 bits), then the name without its namespace
/// prefix and the value, padded to whole words. The position's offset is the
/// inode's string hash — SipHash-2-4 keyed by the inode's hash seed, over the
/// namespace byte followed by the name, shifted right by one — and a collision
/// takes the next free offset, as a directory entry's does.</para>
///
/// <para>A key may hold at most 255 words including its five-word header, so the
/// header, name and value of one attribute come to at most 2000 bytes.</para>
///
/// <para>POSIX ACLs (<c>system.posix_acl_*</c>) are stored in bcachefs's own ACL
/// encoding rather than the xattr one, and are not written here.</para>
/// </remarks>
internal static class BcacheFsXattrCodec {

  /// <summary>Namespaces in the order of their <c>x_type</c> numbers that carry a name.</summary>
  private static readonly (byte Type, string Prefix)[] Namespaces = [
    (0, "user."),
    (3, "trusted."),
    (4, "security."),
  ];

  private const byte TypePosixAclAccess = 1;
  private const byte TypePosixAclDefault = 2;

  /// <summary>The largest header + name + value one key holds.</summary>
  internal const int MaxValueBytes = (255 - BkeyU64s) * 8;

  /// <summary>Splits a full attribute name into its namespace number and the stored name.</summary>
  internal static (byte Type, byte[] Name) Split(string fullName) {
    ArgumentException.ThrowIfNullOrEmpty(fullName);
    foreach (var (type, prefix) in Namespaces) {
      if (!fullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
      var name = Encoding.UTF8.GetBytes(fullName[prefix.Length..]);
      if (name.Length is 0 or > 255)
        throw new ArgumentException($"bcachefs: xattr name '{fullName}' must be 1 to 255 bytes after its namespace.", nameof(fullName));
      return (type, name);
    }
    throw new NotSupportedException(
      $"bcachefs: xattr '{fullName}' is not in the user., trusted. or security. namespace this package writes.");
  }

  /// <summary>The full name an attribute is known by, or null for a namespace not read here.</summary>
  internal static string? FullName(byte type, ReadOnlySpan<byte> name) {
    foreach (var (t, prefix) in Namespaces)
      if (t == type) return prefix + Encoding.UTF8.GetString(name);
    return type switch {
      TypePosixAclAccess => "system.posix_acl_access",
      TypePosixAclDefault => "system.posix_acl_default",
      _ => null,
    };
  }

  /// <summary>Where an attribute hashes to, before any collision moves it.</summary>
  internal static ulong Hash(ulong hashSeed, byte type, ReadOnlySpan<byte> name) {
    var data = new byte[1 + name.Length];
    data[0] = type;
    name.CopyTo(data.AsSpan(1));
    return SipHash24(hashSeed, 0, data) >> 1;
  }

  /// <summary>The value of one xattr key.</summary>
  internal static byte[] Value(byte type, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) {
    var length = 4 + name.Length + value.Length;
    if (length > MaxValueBytes)
      throw new ArgumentException(
        $"bcachefs: an xattr's name and value come to {length} bytes with their header; one key holds {MaxValueBytes}.");
    var result = new byte[(length + 7) / 8 * 8];
    result[0] = type;
    result[1] = (byte)name.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), (ushort)value.Length);
    name.CopyTo(result.AsSpan(4));
    value.CopyTo(result.AsSpan(4 + name.Length));
    return result;
  }

  /// <summary>Reads one xattr value, or false when it is malformed.</summary>
  internal static bool TryParse(ReadOnlySpan<byte> value, out byte type, out byte[] name, out byte[] data) {
    type = 0;
    name = data = [];
    if (value.Length < 4) return false;
    type = value[0];
    var nameLength = value[1];
    var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(value[2..]);
    if (4 + nameLength + valueLength > value.Length) return false;
    name = value.Slice(4, nameLength).ToArray();
    data = value.Slice(4 + nameLength, valueLength).ToArray();
    return true;
  }

  /// <summary>
  /// The keys for one inode's attributes, each at its hash or the next offset free
  /// among <paramref name="used" />.
  /// </summary>
  internal static IEnumerable<Key> Keys(ulong inode, ulong hashSeed, IEnumerable<KeyValuePair<string, byte[]>> attributes,
      HashSet<ulong>? used = null) {
    used ??= [];
    foreach (var (fullName, data) in attributes) {
      var (type, name) = Split(fullName);
      var offset = Hash(hashSeed, type, name);
      while (!used.Add(offset)) ++offset;
      yield return new Key(KeyXattr, new Bpos(inode, offset, SnapshotIdMax), 0, Value(type, name, data));
    }
  }
}
