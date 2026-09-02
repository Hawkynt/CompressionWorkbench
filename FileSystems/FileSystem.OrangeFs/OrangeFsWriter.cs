#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.OrangeFs;

/// <summary>Writes and edits standalone OrangeFS/PVFS2 DBPF storage objects.</summary>
internal static class OrangeFsWriter {
  internal const int HeaderSize = 16;

  public static void Create(Stream output, ReadOnlySpan<byte> payload,
      bool orangeFs = true, uint version = 1, uint datastreamType = 0) {
    ArgumentNullException.ThrowIfNull(output);
    Span<byte> header = stackalloc byte[HeaderSize];
    (orangeFs ? OrangeFsReader.OrangeFsTag : OrangeFsReader.PvfsTag).CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], version);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], datastreamType);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)payload.Length));
    output.Write(header);
    output.Write(payload);
  }

  public static void ReplacePayload(Stream image, ReadOnlySpan<byte> payload) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("OrangeFS mutation requires a seekable read/write stream.", nameof(image));

    // ReadOnlySpan<byte>.Length is an Int32, therefore it always fits the
    // DBPF object's unsigned 32-bit length field. The checked cast documents
    // the wire-format boundary without an impossible int > uint.MaxValue test.
    image.Position = 0;
    Span<byte> header = stackalloc byte[HeaderSize];
    image.ReadExactly(header);
    if (!header[..4].SequenceEqual(OrangeFsReader.PvfsTag) &&
        !header[..4].SequenceEqual(OrangeFsReader.OrangeFsTag))
      throw new InvalidDataException("OrangeFS/PVFS2 DBPF header is invalid.");

    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)payload.Length));
    image.Position = 0;
    image.Write(header);
    image.Write(payload);
    image.SetLength(HeaderSize + payload.Length);
  }
}
