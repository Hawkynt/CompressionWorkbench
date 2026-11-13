using Compression.Core.DiskImage;

namespace FileSystem.Zfs;

/// <summary>
/// A node of the pool's vdev tree that can read a range of its own address space
/// (offsets counted from the 4 MiB data start of its leaves). The mapping rules are the
/// ones in <c>docs/ZFS-ON-DISK.md</c>.
/// </summary>
internal abstract class ZfsVdev {
  /// <summary>Reads <paramref name="length"/> bytes at <paramref name="offset"/>, or throws when the copy is unreachable.</summary>
  public abstract byte[] Read(long offset, int length);

  /// <summary>A short description for diagnostics.</summary>
  public abstract string Describe();

  /// <summary>Builds the vdev described by a label's <c>vdev_tree</c>, wiring its leaves to the devices supplied.</summary>
  public static ZfsVdev Build(XdrNvList tree, IReadOnlyDictionary<ulong, ImageAccessor> leavesByGuid, int ashift) {
    var type = tree.String("type") ?? "";
    var children = tree.Lists("children");
    var shift = (int?)tree.UInt64("ashift") ?? ashift;
    switch (type) {
      case "disk" or "file": {
        var guid = tree.UInt64("guid") ?? 0;
        return leavesByGuid.TryGetValue(guid, out var device)
          ? new ZfsLeafVdev(device, guid)
          : new ZfsMissingVdev($"{type} device {guid}");
      }
      case "mirror" or "replacing" or "spare":
        return new ZfsMirrorVdev(type, children.Select(c => Build(c, leavesByGuid, shift)).ToArray());
      case "raidz": {
        var parity = (int)(tree.UInt64("nparity") ?? 1);
        if (parity is < 1 or > 3 || children.Length <= parity)
          throw new InvalidDataException($"ZFS: RAID-Z with {children.Length} children and parity {parity} is malformed.");
        return new ZfsRaidzVdev(children.Select(c => Build(c, leavesByGuid, shift)).ToArray(), parity, shift);
      }
      default:
        return new ZfsUnsupportedVdev(type);
    }
  }
}

/// <summary>One device image.</summary>
internal sealed class ZfsLeafVdev(ImageAccessor device, ulong guid) : ZfsVdev {
  public override byte[] Read(long offset, int length) {
    var at = ZfsConstants.DataStart + offset;
    if (offset < 0 || at + length > device.Length)
      throw new InvalidDataException($"ZFS: block at {offset} + {length} lies past the end of device {guid}.");
    return device.Read(at, length);
  }

  public override string Describe() => $"device {guid}";
}

/// <summary>A device named by the labels but not supplied.</summary>
internal sealed class ZfsMissingVdev(string what) : ZfsVdev {
  public override byte[] Read(long offset, int length)
    => throw new ZfsDeviceMissingException($"ZFS: {what} is not among the supplied images.");

  public override string Describe() => what + " (missing)";
}

/// <summary>A vdev type the reader does not map (dRAID, indirect after device removal, holes).</summary>
internal sealed class ZfsUnsupportedVdev(string type) : ZfsVdev {
  public override byte[] Read(long offset, int length)
    => throw new NotSupportedException($"ZFS: '{type}' vdevs are not supported.");

  public override string Describe() => type + " (unsupported)";
}

/// <summary>Children holding the same bytes; the first readable one wins.</summary>
internal sealed class ZfsMirrorVdev(string type, ZfsVdev[] children) : ZfsVdev {
  public override byte[] Read(long offset, int length) {
    Exception? last = null;
    foreach (var child in children) {
      try {
        return child.Read(offset, length);
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException or ZfsDeviceMissingException) {
        last = e;
      }
    }
    throw last ?? new InvalidDataException($"ZFS: {type} has no children.");
  }

  public override string Describe() => $"{type}({string.Join(", ", children.Select(c => c.Describe()))})";
}

/// <summary>
/// RAID-Z: a block is striped over the children in columns, parity first. Single parity
/// recovers one missing data column; with more parity every data column must be present.
/// </summary>
internal sealed class ZfsRaidzVdev(ZfsVdev[] children, int parity, int ashift) : ZfsVdev {
  public override byte[] Read(long offset, int length) {
    var columns = Map(offset, length, children.Length, parity, ashift);
    var data = new byte[columns.Where(c => c.IsData).Sum(c => c.Size)];
    var pieces = new byte[columns.Length][];
    var missing = -1;
    for (var c = parity; c < columns.Length; ++c) {
      if (columns[c].Size == 0) {
        pieces[c] = [];
        continue;
      }
      try {
        pieces[c] = children[columns[c].Child].Read(columns[c].Offset, columns[c].Size);
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException or ZfsDeviceMissingException) {
        if (missing >= 0) throw;
        missing = c;
      }
    }

    if (missing >= 0) {
      if (columns[0].Size == 0) throw new InvalidDataException("ZFS: RAID-Z parity column is empty.");
      // P is the XOR of the data columns, each zero-padded to the parity column's length.
      var p = children[columns[0].Child].Read(columns[0].Offset, columns[0].Size);
      for (var c = parity; c < columns.Length; ++c) {
        if (c == missing) continue;
        var piece = pieces[c];
        for (var i = 0; i < piece.Length; ++i) p[i] ^= piece[i];
      }
      pieces[missing] = p.AsSpan(0, columns[missing].Size).ToArray();
    }

    var at = 0;
    for (var c = parity; c < columns.Length; ++c) {
      pieces[c].CopyTo(data, at);
      at += pieces[c].Length;
    }
    return data.Length == length ? data : data.AsSpan(0, Math.Min(length, data.Length)).ToArray();
  }

  public override string Describe() => $"raidz{parity}({string.Join(", ", children.Select(c => c.Describe()))})";

  /// <summary>One column of a RAID-Z block: which child, where on it, how many bytes.</summary>
  internal readonly record struct Column(int Child, long Offset, int Size, bool IsData);

  /// <summary>The column map of a block (<c>docs/ZFS-ON-DISK.md</c>, "RAID-Z").</summary>
  internal static Column[] Map(long offset, int length, int childCount, int parity, int ashift) {
    var sector = 1L << ashift;
    var b = offset >> ashift;
    var n = (length + sector - 1) >> ashift;
    var f = (int)(b % childCount);
    var baseOffset = (b / childCount) << ashift;
    var dataColumns = childCount - parity;
    var q = n / dataColumns;
    var r = n - q * dataColumns;
    var bc = r == 0 ? 0 : r + parity;
    var count = q == 0 ? (int)bc : childCount;

    var columns = new Column[count];
    for (var c = 0; c < count; ++c) {
      var child = f + c;
      var at = baseOffset;
      if (child >= childCount) {
        child -= childCount;
        at += sector;
      }
      var sectors = c < bc ? q + 1 : q;
      columns[c] = new(child, at, (int)(sectors << ashift), c >= parity);
    }

    if (parity == 1 && count > 1 && (offset & (1L << 20)) != 0) {
      (columns[0], columns[1]) = (columns[0] with { Child = columns[1].Child, Offset = columns[1].Offset },
                                  columns[1] with { Child = columns[0].Child, Offset = columns[0].Offset });
    }
    return columns;
  }

  /// <summary>
  /// The space a block of <paramref name="physicalSize"/> bytes takes on the RAID-Z vdev: its data
  /// sectors, a parity sector per data row, rounded up to a multiple of <c>parity + 1</c> sectors.
  /// </summary>
  internal static long AllocatedSize(long physicalSize, int childCount, int parity, int ashift) {
    var sectors = ((physicalSize - 1) >> ashift) + 1;
    sectors += parity * ((sectors + childCount - parity - 1) / (childCount - parity));
    var unit = parity + 1;
    return (sectors + unit - 1) / unit * unit << ashift;
  }

  /// <summary>
  /// What one allocated byte is charged to datasets on this vdev, in 1/512 units:
  /// <c>2^17 / (allocated sectors of a 128 KiB block)</c> (512 = no parity overhead).
  /// </summary>
  internal static long DeflateRatio(int childCount, int parity, int ashift) =>
    (1L << 17) / (AllocatedSize(1L << 17, childCount, parity, ashift) >> ZfsConstants.SectorShift);

  /// <summary>
  /// The bytes of every column of a block about to be written: data columns are consecutive
  /// slices of <paramref name="physical"/> (zero-padded to whole sectors); parity columns are P (XOR),
  /// Q (<c>Σ 2^k·D</c>) and R (<c>Σ 4^k·D</c>) over GF(2^8) with polynomial 0x11D, accumulated in data-column
  /// order with short columns counted as zeros.
  /// </summary>
  internal static byte[][] Encode(byte[] physical, Column[] columns, int parity) {
    var pieces = new byte[columns.Length][];
    var at = 0;
    for (var c = parity; c < columns.Length; ++c) {
      pieces[c] = new byte[columns[c].Size];
      var n = Math.Max(0, Math.Min(columns[c].Size, physical.Length - at));
      physical.AsSpan(at, n).CopyTo(pieces[c]);
      at += columns[c].Size;
    }
    var length = columns[0].Size;
    for (var p = 0; p < parity; ++p) pieces[p] = new byte[length];
    for (var c = parity; c < columns.Length; ++c) {
      var data = pieces[c];
      for (var i = 0; i < length; ++i) {
        var d = i < data.Length ? data[i] : (byte)0;
        pieces[0][i] ^= d;
        if (parity > 1) pieces[1][i] = (byte)(Times2(pieces[1][i]) ^ d);
        if (parity > 2) pieces[2][i] = (byte)(Times2(Times2(pieces[2][i])) ^ d);
      }
    }
    return pieces;
  }

  private static byte Times2(byte x) => (byte)((x << 1) ^ ((x & 0x80) != 0 ? 0x1D : 0));
}

/// <summary>A block's only copies sit on devices that were not supplied.</summary>
internal sealed class ZfsDeviceMissingException(string message) : IOException(message);
