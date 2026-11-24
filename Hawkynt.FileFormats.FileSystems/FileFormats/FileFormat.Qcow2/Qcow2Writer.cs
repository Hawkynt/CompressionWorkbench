#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Qcow2;

/// <summary>
/// Writes canonical self-contained QCOW2 v2 images with 64 KiB clusters and
/// 16-bit eager refcounts. Sparse mode leaves all-zero guest clusters
/// unallocated; dense mode physically allocates every guest cluster without
/// changing guest bytes. With a password the guest data is LUKS-encrypted (crypt_method 2): a
/// LUKS1 header (aes-256 xts-plain64, sha256, key slot 0) in its own clusters behind the
/// refcount blocks, each data cluster encrypted with the IV of its host sector; with a legacy AES
/// password (crypt_method 1) each sector is AES-128-CBC encrypted with the IV of its guest sector.
/// With an external data file the image is a version 3 image of cluster tables only and the guest
/// disk goes, raw, to the data file.
/// </summary>
public sealed class Qcow2Writer {
  private const int ClusterBits = 16;
  private const int ClusterSize = 1 << ClusterBits;
  private const int L2EntriesPerCluster = ClusterSize / sizeof(ulong);
  private const int RefcountEntriesPerBlock = ClusterSize / sizeof(ushort);
  private const int RefcountTableEntriesPerCluster = ClusterSize / sizeof(ulong);
  private const uint FullDiskEncryptionExtension = 0x0537BE77;
  private byte[]? _diskData;
  private string? _password;
  private string? _dataFileName;
  private bool _dataFileRaw;
  private bool _legacyAes;
  private int _keySlotIterations = DefaultKeySlotIterations;

  /// <summary>PBKDF2 iterations of the passphrase in the LUKS key slot unless <see cref="SetPassword"/> says otherwise.</summary>
  public const int DefaultKeySlotIterations = 500_000;

  /// <summary>Sets the raw guest disk image to wrap.</summary>
  public void SetDiskImage(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _diskData = data;
  }

  /// <summary>Encrypts the guest data with LUKS under <paramref name="password"/> (null: no encryption).</summary>
  /// <param name="password">The passphrase of LUKS key slot 0.</param>
  /// <param name="keySlotIterations">PBKDF2 iterations of the passphrase; the master-key digest uses an eighth (at least 1000).</param>
  public void SetPassword(string? password, int keySlotIterations = DefaultKeySlotIterations) {
    ArgumentOutOfRangeException.ThrowIfLessThan(keySlotIterations, 1000);
    _password = password;
    _keySlotIterations = keySlotIterations;
    _legacyAes = false;
  }

  /// <summary>
  /// Encrypts the guest data with QCOW2's legacy AES scheme (crypt_method 1) under
  /// <paramref name="password"/>: AES-128-CBC per 512-byte sector with the guest sector number as
  /// IV, the key being the password's first 16 UTF-8 bytes (zero padded). The scheme has no key
  /// check and no key derivation and QEMU only keeps it for reading old images; it is offered for
  /// interoperability with tools that need it, <see cref="SetPassword"/> (LUKS) otherwise.
  /// </summary>
  public void SetLegacyAesPassword(string password) {
    ArgumentException.ThrowIfNullOrEmpty(password);
    _password = password;
    _legacyAes = true;
  }

  /// <summary>
  /// Keeps the guest data in an external data file (qcow2.txt, incompatible feature bit 2) that
  /// the image's header names <paramref name="name"/> and QEMU looks for next to the image. The
  /// image becomes a version 3 image holding only the cluster tables; with <paramref name="raw"/>
  /// the data file is also declared a consistent raw image of the guest (autoclear bit 1). Write
  /// both with <see cref="WriteTo(Stream, Stream, bool)"/>.
  /// </summary>
  public void SetExternalDataFile(string name, bool raw = true) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    if (System.Text.Encoding.UTF8.GetByteCount(name) > 1023)
      throw new ArgumentException("QCOW2: the data file name is longer than 1023 bytes.", nameof(name));
    _dataFileName = name;
    _dataFileRaw = raw;
  }

  /// <summary>Writes a canonical sparse QCOW2 image.</summary>
  public void WriteTo(Stream output) => WriteTo(output, sparse: true);

  /// <summary>
  /// Writes an image whose guest data goes to <paramref name="dataFile"/>: the whole guest disk as
  /// a raw image, each cluster at its own guest offset (see <see cref="SetExternalDataFile"/>).
  /// </summary>
  /// <param name="output">Destination of the image itself (header and cluster tables).</param>
  /// <param name="dataFile">Destination of the external data file.</param>
  /// <param name="sparse">When true, all-zero guest clusters stay unallocated in the tables.</param>
  public void WriteTo(Stream output, Stream dataFile, bool sparse = true) {
    ArgumentNullException.ThrowIfNull(dataFile);
    if (_dataFileName == null)
      throw new InvalidOperationException("No external data file was set; call SetExternalDataFile first.");
    if (!dataFile.CanWrite)
      throw new ArgumentException("QCOW2 data file output must be writable.", nameof(dataFile));
    WriteImage(output, dataFile, sparse);
  }

  /// <summary>
  /// Writes a canonical QCOW2 image.
  /// </summary>
  /// <param name="output">Destination stream.</param>
  /// <param name="sparse">When true, all-zero guest clusters have zero L2 entries.
  /// When false, every guest cluster receives physical storage.</param>
  public void WriteTo(Stream output, bool sparse) {
    if (_dataFileName != null)
      throw new InvalidOperationException("The image keeps its guest data in an external data file; write it with WriteTo(output, dataFile).");
    WriteImage(output, null, sparse);
  }

  private void WriteImage(Stream output, Stream? dataFile, bool sparse) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite)
      throw new ArgumentException("QCOW2 output must be writable.", nameof(output));
    var data = _diskData ?? throw new InvalidOperationException("No disk image set.");

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }

    var virtualSize = (long)data.Length;
    var guestClusterCount = checked((int)((virtualSize + ClusterSize - 1) / ClusterSize));
    var l1Size = Math.Max(1, (guestClusterCount + L2EntriesPerCluster - 1) / L2EntriesPerCluster);
    if (l1Size > L2EntriesPerCluster)
      throw new NotSupportedException("QCOW2 byte-array writer requires more than one L1 table cluster.");

    var physicalDataIndex = new int[guestClusterCount];
    Array.Fill(physicalDataIndex, -1);
    var allocatedGuestClusters = 0;
    for (var cluster = 0; cluster < guestClusterCount; ++cluster) {
      if (sparse && IsGuestClusterZero(data, cluster))
        continue;
      physicalDataIndex[cluster] = allocatedGuestClusters++;
    }

    const int headerClusters = 1;
    const int l1Clusters = 1;
    const int refcountTableClusters = 1;
    byte[]? luks = null;
    Qcow2Encryption? encryption = null;
    if (_password != null && dataFile != null)
      throw new NotSupportedException("QCOW2 writer: an encrypted image with an external data file is not written.");
    if (_password != null && _legacyAes) {
      encryption = Qcow2Encryption.ForLegacyAes(_password);
    } else if (_password != null) {
      var masterKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
      luks = Compression.Core.Crypto.Luks1Header.Create(masterKey, System.Text.Encoding.UTF8.GetBytes(_password),
        "xts-plain64", "sha256", _keySlotIterations, Math.Max(1000, _keySlotIterations / 8), Guid.NewGuid());
      encryption = Qcow2Encryption.ForLuks(Compression.Core.Crypto.Luks1SectorCipher.Create("aes", "xts-plain64", masterKey));
    }
    using var _ = encryption;
    var luksClusters = luks == null ? 0 : (luks.Length + ClusterSize - 1) / ClusterSize;
    if (l1Size > RefcountTableEntriesPerCluster)
      throw new NotSupportedException("QCOW2 writer L1 geometry exceeds the supported table layout.");

    var structuralWithoutRefcountBlocks =
      headerClusters + l1Clusters + l1Size + refcountTableClusters + luksClusters;
    // Clusters in an external data file are not refcounted.
    var refcountedGuestClusters = dataFile == null ? allocatedGuestClusters : 0;
    var refcountBlockClusters = 1;
    while (true) {
      var total = checked(structuralWithoutRefcountBlocks + refcountBlockClusters + refcountedGuestClusters);
      var needed = Math.Max(1, (total + RefcountEntriesPerBlock - 1) / RefcountEntriesPerBlock);
      if (needed == refcountBlockClusters)
        break;
      refcountBlockClusters = needed;
    }
    if (refcountBlockClusters > RefcountTableEntriesPerCluster)
      throw new NotSupportedException("QCOW2 writer requires more than one refcount table cluster.");

    var l1TableOffset = (long)ClusterSize;
    var l2TablesStart = 2L * ClusterSize;
    var refcountTableOffset = checked(l2TablesStart + (long)l1Size * ClusterSize);
    var refcountBlocksStart = checked(refcountTableOffset + ClusterSize);
    var luksStart = checked(refcountBlocksStart + (long)refcountBlockClusters * ClusterSize);
    var dataStart = checked(luksStart + (long)luksClusters * ClusterSize);
    var totalPhysicalClusters = checked(structuralWithoutRefcountBlocks + refcountBlockClusters + refcountedGuestClusters);

    if (dataFile == null)
      WriteHeader(output, virtualSize, l1Size, l1TableOffset, refcountTableOffset, luks == null ? 0 : luksStart, luks?.Length ?? 0,
        luks != null ? Qcow2Structures.CryptLuks : encryption != null ? Qcow2Structures.CryptAes : Qcow2Structures.CryptNone);
    else
      WriteExternalDataHeader(output, virtualSize, l1Size, l1TableOffset, refcountTableOffset, _dataFileName!, _dataFileRaw);
    WriteL1(output, l1Size, l2TablesStart);
    WriteL2Tables(output, guestClusterCount, l1Size, physicalDataIndex, dataStart, external: dataFile != null);
    WriteRefcountTable(output, refcountBlockClusters, refcountBlocksStart);
    WriteRefcountBlocks(output, refcountBlockClusters, totalPhysicalClusters);
    if (luks != null) {
      output.Write(luks);
      WriteZeroPadding(output, luksClusters * ClusterSize - luks.Length);
    }
    if (dataFile == null) {
      WriteGuestData(output, data, guestClusterCount, physicalDataIndex, dataStart, encryption);
      return;
    }
    if (dataFile.CanSeek) {
      dataFile.Position = 0;
      dataFile.SetLength(0);
    }
    dataFile.Write(data);
  }

  /// <summary>
  /// A version 3 header (104 bytes, 16-bit refcounts) with the external-data-file feature, the
  /// raw-data-file autoclear bit when asked, and the data-file-name header extension.
  /// </summary>
  private static void WriteExternalDataHeader(Stream output, long virtualSize, int l1Size, long l1TableOffset,
      long refcountTableOffset, string dataFileName, bool raw) {
    const int headerLength = 104;
    var header = new byte[ClusterSize];
    Qcow2Structures.Magic.CopyTo(header.AsSpan());
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 3);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)ClusterBits);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(24), (ulong)virtualSize);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), (uint)l1Size);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), (ulong)l1TableOffset);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), (ulong)refcountTableOffset);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 1);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(72), Qcow2Structures.IncompatibleExternalData);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(88), raw ? Qcow2Structures.AutoclearDataFileRaw : 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(96), 4);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(100), headerLength);
    var name = System.Text.Encoding.UTF8.GetBytes(dataFileName);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(headerLength), Qcow2Structures.ExtensionDataFileName);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(headerLength + 4), (uint)name.Length);
    name.CopyTo(header.AsSpan(headerLength + 8));
    // The name is zero padded to 8 bytes; the zeros after it end the extension list.
    output.Write(header);
  }

  private static void WriteHeader(
      Stream output,
      long virtualSize,
      int l1Size,
      long l1TableOffset,
      long refcountTableOffset,
      long luksOffset,
      long luksLength,
      uint cryptMethod) {
    var header = new byte[ClusterSize];
    Qcow2Structures.Magic.CopyTo(header.AsSpan());
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)ClusterBits);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(24), (ulong)virtualSize);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), cryptMethod);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), (uint)l1Size);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), (ulong)l1TableOffset);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), (ulong)refcountTableOffset);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 1);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), 0);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(64), 0);
    if (luksLength > 0) {
      // Header extensions follow the 72-byte version 2 header; the zeros after this one end the list.
      BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(72), FullDiskEncryptionExtension);
      BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(76), 16);
      BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(80), (ulong)luksOffset);
      BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(88), (ulong)luksLength);
    }
    output.Write(header);
  }

  private static void WriteL1(Stream output, int l1Size, long l2TablesStart) {
    var l1 = new byte[ClusterSize];
    for (var i = 0; i < l1Size; ++i) {
      var l2Offset = checked(l2TablesStart + (long)i * ClusterSize);
      BinaryPrimitives.WriteUInt64BigEndian(
        l1.AsSpan(i * sizeof(ulong)),
        (ulong)l2Offset | Qcow2Structures.CopiedFlag);
    }
    output.Write(l1);
  }

  private static void WriteL2Tables(
      Stream output,
      int guestClusterCount,
      int l1Size,
      int[] physicalDataIndex,
      long dataStart,
      bool external) {
    var guestCluster = 0;
    for (var table = 0; table < l1Size; ++table) {
      var l2 = new byte[ClusterSize];
      for (var entry = 0; entry < L2EntriesPerCluster && guestCluster < guestClusterCount;
           ++entry, ++guestCluster) {
        var physicalIndex = physicalDataIndex[guestCluster];
        if (physicalIndex < 0)
          continue;
        // With an external data file every cluster lies at its own guest offset there.
        var hostOffset = external
          ? (long)guestCluster * ClusterSize
          : checked(dataStart + (long)physicalIndex * ClusterSize);
        BinaryPrimitives.WriteUInt64BigEndian(
          l2.AsSpan(entry * sizeof(ulong)),
          (ulong)hostOffset | Qcow2Structures.CopiedFlag);
      }
      output.Write(l2);
    }
  }

  private static void WriteRefcountTable(Stream output, int blockCount, long blocksStart) {
    var table = new byte[ClusterSize];
    for (var block = 0; block < blockCount; ++block) {
      var offset = checked(blocksStart + (long)block * ClusterSize);
      BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(block * sizeof(ulong)), (ulong)offset);
    }
    output.Write(table);
  }

  private static void WriteRefcountBlocks(Stream output, int blockCount, int totalPhysicalClusters) {
    for (var block = 0; block < blockCount; ++block) {
      var refcounts = new byte[ClusterSize];
      var globalStart = block * RefcountEntriesPerBlock;
      var count = Math.Clamp(totalPhysicalClusters - globalStart, 0, RefcountEntriesPerBlock);
      for (var entry = 0; entry < count; ++entry)
        BinaryPrimitives.WriteUInt16BigEndian(refcounts.AsSpan(entry * sizeof(ushort)), 1);
      output.Write(refcounts);
    }
  }

  private static void WriteGuestData(Stream output, byte[] data, int guestClusterCount, int[] physicalDataIndex,
      long dataStart, Qcow2Encryption? encryption) {
    var cluster = encryption == null ? null : new byte[ClusterSize];
    for (var guest = 0; guest < guestClusterCount; ++guest) {
      if (physicalDataIndex[guest] < 0)
        continue;

      var offset = guest * ClusterSize;
      var length = Math.Min(ClusterSize, data.Length - offset);
      if (cluster != null) {
        Array.Clear(cluster);
        data.AsSpan(offset, length).CopyTo(cluster);
        encryption!.Encrypt(dataStart + (long)physicalDataIndex[guest] * ClusterSize, offset, cluster);
        output.Write(cluster);
        continue;
      }
      output.Write(data.AsSpan(offset, length));
      if (length < ClusterSize)
        WriteZeroPadding(output, ClusterSize - length);
    }
  }

  private static bool IsGuestClusterZero(byte[] data, int clusterIndex) {
    var offset = clusterIndex * ClusterSize;
    var length = Math.Min(ClusterSize, data.Length - offset);
    foreach (var value in data.AsSpan(offset, Math.Max(0, length)))
      if (value != 0)
        return false;
    return true;
  }

  private static void WriteZeroPadding(Stream output, int length) {
    Span<byte> zeros = stackalloc byte[Math.Min(length, 4096)];
    var remaining = length;
    while (remaining > 0) {
      var chunk = Math.Min(remaining, zeros.Length);
      output.Write(zeros[..chunk]);
      remaining -= chunk;
    }
  }
}