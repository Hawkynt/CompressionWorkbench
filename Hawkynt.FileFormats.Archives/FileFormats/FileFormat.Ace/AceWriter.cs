using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Crypto;
using Compression.Core.Dictionary.Ace;
using CoreAceConstants = Compression.Core.Dictionary.Ace.AceConstants;

namespace FileFormat.Ace;

/// <summary>
/// Creates ACE archives.
/// </summary>
public sealed class AceWriter {
  private readonly List<(string name, byte[] data)> _files = [];
  private readonly int _dictBits;
  private readonly string? _password;
  private readonly bool _solid;
  private readonly bool _recoveryRecord;
  private readonly int _compressionType;
  private readonly int _subMode;

  /// <summary>Gets or sets the archive comment.</summary>
  public string? Comment { get; set; }

  /// <summary>
  /// Initializes a new <see cref="AceWriter"/>.
  /// </summary>
  /// <param name="dictionaryBits">Dictionary size in bits (10-22, default 15 = 32KB).</param>
  /// <param name="password">Optional password for Blowfish encryption.</param>
  /// <param name="solid">Whether to create a solid archive (shared dictionary across files).</param>
  /// <param name="recoveryRecord">Whether to include a recovery record (XOR parity).</param>
  /// <param name="compressionType">Compression type: 1=ACE 1.0, 2=ACE 2.0. Default 1.</param>
  /// <param name="subMode">ACE 2.0 sub-mode: 0=LZ77, 1=EXE, 2=DELTA, 3=SOUND, 4=PIC. Default 0.</param>
  public AceWriter(int dictionaryBits = CoreAceConstants.DefaultDictBits, string? password = null,
      bool solid = false, bool recoveryRecord = false,
      int compressionType = CoreAceConstants.CompAce10, int subMode = 0) {
    this._dictBits = Math.Clamp(dictionaryBits, CoreAceConstants.MinDictBits, CoreAceConstants.MaxDictBits);
    this._password = password;
    this._solid = solid;
    this._recoveryRecord = recoveryRecord;
    this._compressionType = compressionType;
    this._subMode = subMode;
  }

  /// <summary>
  /// Adds a file to the archive.
  /// </summary>
  /// <param name="name">The file name.</param>
  /// <param name="data">The file data.</param>
  public void AddFile(string name, byte[] data) {
    this._files.Add((name, data));
  }

  /// <summary>
  /// Writes the archive to a stream.
  /// </summary>
  /// <param name="output">The stream to write to.</param>
  public void WriteTo(Stream output) {
    var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

    // Write archive header
    WriteArchiveHeader(writer);

    // Write file entries, collecting compressed data for recovery
    var compressedBlocks = this._recoveryRecord ? new List<byte[]>() : null;
    var solidEncoder = this._solid ? new AceEncoder(this._dictBits) : null;
    var isFirstFile = true;
    foreach (var (name, data) in this._files) {
      var compData = WriteFileEntry(writer, name, data, solidEncoder, isFirstFile);
      compressedBlocks?.Add(compData ?? []);
      isFirstFile = false;
    }

    // Write recovery record if requested
    if (this._recoveryRecord && compressedBlocks != null)
      WriteRecoveryBlock(writer, compressedBlocks);
  }

  /// <summary>
  /// Creates an ACE archive as a byte array.
  /// </summary>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    WriteTo(ms);
    return ms.ToArray();
  }

  private void WriteArchiveHeader(BinaryWriter writer) {
    using var headerMs = new MemoryStream();
    using var hw = new BinaryWriter(headerMs, Encoding.ASCII, leaveOpen: true);

    hw.Write((byte)AceConstants.HeaderTypeArchive); // type
    ushort archiveFlags = 0;
    if (this._solid)
      archiveFlags |= AceConstants.FlagSolid;
    if (this._recoveryRecord)
      archiveFlags |= AceConstants.FlagRecovery;
    hw.Write(archiveFlags); // flags
    hw.Write(Encoding.ASCII.GetBytes(AceConstants.Magic)); // magic
    hw.Write((byte)20); // extract version
    hw.Write((byte)20); // create version
    hw.Write((byte)0); // host OS (generic)
    hw.Write((byte)0); // volume number
    hw.Write(DosTimestamp(DateTime.Now)); // timestamp
    hw.Write(new byte[8]); // reserved

    // Comment
    var commentBytes = this.Comment != null ? Encoding.ASCII.GetBytes(this.Comment) : null;
    hw.Write((ushort)(commentBytes?.Length ?? 0));
    if (commentBytes != null)
      hw.Write(commentBytes);

    hw.Flush();
    var headerPayload = headerMs.ToArray();

    // CRC-16 of header payload (lower 16 bits of CRC-32)
    var crc32 = Crc32.Compute(headerPayload);
    var crc16 = (ushort)(crc32 & 0xFFFF);

    writer.Write(crc16);
    writer.Write((ushort)headerPayload.Length);
    writer.Write(headerPayload);
  }

  private byte[] WriteFileEntry(BinaryWriter writer, string name, byte[] data,
      AceEncoder? solidEncoder, bool isFirstFile) {
    var dataCrc = Crc32.Compute(data);

    byte[] compressed;
    byte compType;

    if (data.Length == 0) {
      compressed = data;
      compType = CoreAceConstants.CompStore;
    }
    else {
      var encoder = solidEncoder ?? new AceEncoder(this._dictBits);
      if (this._compressionType == CoreAceConstants.CompAce20) {
        compressed = encoder.Encode20(data, this._subMode);
        compType = CoreAceConstants.CompAce20;
      } else {
        compressed = encoder.Encode(data);
        compType = CoreAceConstants.CompAce10;
      }
      if (compressed.Length >= data.Length) {
        compressed = data;
        compType = CoreAceConstants.CompStore;
      }
    }

    // Set solid flag for all files after the first in solid mode
    ushort fileFlags = 0;
    if (this._solid && !isFirstFile)
      fileFlags |= AceConstants.FileFlagSolid;
    if (this._password != null && compressed.Length > 0) {
      compressed = EncryptData(compressed);
      fileFlags |= AceConstants.FileFlagEncrypted;
    }

    var nameBytes = Encoding.ASCII.GetBytes(name);

    // Build file header payload
    using var headerMs = new MemoryStream();
    using var hw = new BinaryWriter(headerMs, Encoding.ASCII, leaveOpen: true);

    hw.Write((byte)AceConstants.HeaderTypeFile); // type
    hw.Write(fileFlags); // flags
    hw.Write((uint)compressed.Length); // packed size
    hw.Write((uint)data.Length); // original size
    hw.Write(DosTimestamp(DateTime.Now)); // timestamp
    hw.Write((uint)0); // attributes
    hw.Write(dataCrc); // CRC-32

    // TECH_INFO
    hw.Write(compType); // compression type
    hw.Write((byte)0); // quality
    hw.Write((ushort)(this._dictBits - CoreAceConstants.MinDictBits)); // tech params

    hw.Write((ushort)nameBytes.Length);
    hw.Write(nameBytes);

    hw.Flush();
    var headerPayload = headerMs.ToArray();

    var headerCrc32 = Crc32.Compute(headerPayload);
    var headerCrc16 = (ushort)(headerCrc32 & 0xFFFF);

    writer.Write(headerCrc16);
    writer.Write((ushort)headerPayload.Length);
    writer.Write(headerPayload);

    // Write compressed data
    writer.Write(compressed);

    return compressed;
  }

  private void WriteRecoveryBlock(BinaryWriter writer, List<byte[]> compressedBlocks) {
    // Compute XOR parity across all compressed blocks
    var maxLen = 0;
    foreach (var block in compressedBlocks)
      maxLen = Math.Max(maxLen, block.Length);

    var parity = new byte[maxLen];
    foreach (var block in compressedBlocks) {
      for (var i = 0; i < block.Length; ++i)
        parity[i] ^= block[i];
    }

    // Build recovery header payload
    using var headerMs = new MemoryStream();
    using var hw = new BinaryWriter(headerMs, Encoding.ASCII, leaveOpen: true);

    hw.Write(AceConstants.HeaderTypeRecovery); // type
    hw.Write((ushort)0); // flags
    hw.Write((uint)parity.Length); // recovery data size
    hw.Write((uint)compressedBlocks.Count); // number of sectors (file count)

    hw.Flush();
    var headerPayload = headerMs.ToArray();

    var headerCrc32 = Crc32.Compute(headerPayload);
    var headerCrc16 = (ushort)(headerCrc32 & 0xFFFF);

    writer.Write(headerCrc16);
    writer.Write((ushort)headerPayload.Length);
    writer.Write(headerPayload);

    // Write recovery parity data
    writer.Write(parity);
  }

  private byte[] EncryptData(byte[] data) {
    var key = DeriveKey(this._password!);
    var bf = new Blowfish(key);
    var iv = new byte[8]; // ACE uses zero IV
    // Blowfish CBC requires 8-byte aligned data
    var paddedLen = (data.Length + 7) & ~7;
    byte[] padded;
    if (paddedLen != data.Length) {
      padded = new byte[paddedLen];
      data.AsSpan().CopyTo(padded);
    }
    else {
      padded = data;
    }
    return bf.EncryptCbc(padded, iv);
  }

  internal static byte[] DeriveKey(string password) {
    // ACE key derivation: SHA-1 of the password bytes, take first 16 bytes as Blowfish key
    var passBytes = Encoding.ASCII.GetBytes(password);
    var hash = Sha1.Compute(passBytes);
    return hash[..16];
  }

  /// <summary>
  /// Creates an ACE archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="password">Optional password for encryption.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      string? password = null) {
    var writer = new AceWriter(password: password);
    foreach (var (name, data) in entries)
      writer.AddFile(name, data);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(writer.ToArray(), maxVolumeSize);
  }

  private static uint DosTimestamp(DateTime dt) {
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }
}
