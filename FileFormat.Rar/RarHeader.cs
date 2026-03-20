using Compression.Core.Checksums;

namespace FileFormat.Rar;

/// <summary>
/// Represents a parsed RAR5 archive header.
/// </summary>
internal sealed class RarHeader {
  /// <summary>Gets or sets the header CRC-32.</summary>
  public uint HeaderCrc { get; set; }

  /// <summary>Gets or sets the header size (from the Type field to end of header, not including CRC or size fields).</summary>
  public int HeaderSize { get; set; }

  /// <summary>Gets or sets the header type.</summary>
  public int HeaderType { get; set; }

  /// <summary>Gets or sets the header flags.</summary>
  public int HeaderFlags { get; set; }

  /// <summary>Gets or sets the extra area size (0 if not present).</summary>
  public long ExtraSize { get; set; }

  /// <summary>Gets or sets the data area size (0 if not present).</summary>
  public long DataSize { get; set; }

  /// <summary>Gets a value indicating whether the header has an extra area.</summary>
  public bool HasExtraArea => (HeaderFlags & RarConstants.HeaderFlagExtraArea) != 0;

  /// <summary>Gets a value indicating whether the header has a data area.</summary>
  public bool HasDataArea => (HeaderFlags & RarConstants.HeaderFlagDataArea) != 0;

  /// <summary>Gets or sets the raw header body bytes (from Type field onward), used for file header parsing.</summary>
  public byte[]? RawHeaderData { get; set; }

  /// <summary>
  /// Reads a RAR5 header from the stream.
  /// </summary>
  /// <param name="stream">The stream positioned at the start of a header.</param>
  /// <returns>The parsed header.</returns>
  public static RarHeader Read(Stream stream) {
    var header = new RarHeader();

    // 1. Read CRC as vint
    header.HeaderCrc = (uint)RarVint.Read(stream, out _);

    // 2. Read header size as vint — remember the raw bytes for CRC verification
    var sizeStartPos = stream.Position;
    header.HeaderSize = (int)RarVint.Read(stream, out int sizeBytesRead);
    var sizeEndPos = stream.Position;

    // 3. Read the header body (headerSize bytes from Type field onward)
    byte[] body = new byte[header.HeaderSize];
    var totalRead = 0;
    while (totalRead < body.Length) {
      int read = stream.Read(body, totalRead, body.Length - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of RAR header data.");
      totalRead += read;
    }

    // 4. Verify CRC-32: covers size vint bytes + body bytes
    // Re-read the size vint bytes
    byte[] sizeVintBytes = new byte[sizeBytesRead];
    var savedPos = stream.Position;
    stream.Position = sizeStartPos;
    _ = stream.Read(sizeVintBytes, 0, sizeBytesRead);
    stream.Position = savedPos;

    byte[] crcData = new byte[sizeVintBytes.Length + body.Length];
    sizeVintBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeVintBytes.Length));

    uint computedCrc = Crc32.Compute(crcData);
    if (computedCrc != header.HeaderCrc)
      throw new InvalidDataException(
        $"RAR header CRC mismatch: expected 0x{header.HeaderCrc:X8}, computed 0x{computedCrc:X8}.");

    // 5. Parse type and flags from body
    ReadOnlySpan<byte> bodySpan = body;
    var offset = 0;

    header.HeaderType = (int)RarVint.Read(bodySpan[offset..], out int consumed);
    offset += consumed;

    header.HeaderFlags = (int)RarVint.Read(bodySpan[offset..], out consumed);
    offset += consumed;

    if (header.HasExtraArea) {
      header.ExtraSize = (long)RarVint.Read(bodySpan[offset..], out consumed);
      offset += consumed;
    }

    if (header.HasDataArea) {
      header.DataSize = (long)RarVint.Read(bodySpan[offset..], out consumed);
      offset += consumed;
    }

    header.RawHeaderData = body;

    return header;
  }

  /// <summary>
  /// Writes this header to the stream. Used only for testing.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  public void Write(Stream stream) {
    // Build the body
    var bodyMs = new MemoryStream();
    RarVint.Write(bodyMs, (ulong)HeaderType);
    RarVint.Write(bodyMs, (ulong)HeaderFlags);

    if (HasExtraArea)
      RarVint.Write(bodyMs, (ulong)ExtraSize);
    if (HasDataArea)
      RarVint.Write(bodyMs, (ulong)DataSize);

    byte[] body = bodyMs.ToArray();

    // Size vint
    var sizeMs = new MemoryStream();
    RarVint.Write(sizeMs, (ulong)body.Length);
    byte[] sizeBytes = sizeMs.ToArray();

    // CRC covers sizeBytes + body
    byte[] crcData = new byte[sizeBytes.Length + body.Length];
    sizeBytes.AsSpan().CopyTo(crcData);
    body.AsSpan().CopyTo(crcData.AsSpan(sizeBytes.Length));

    uint crc = Crc32.Compute(crcData);

    RarVint.Write(stream, crc);
    stream.Write(crcData);
  }
}
