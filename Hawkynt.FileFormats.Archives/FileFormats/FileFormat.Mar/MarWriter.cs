using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mar;

/// <summary>
/// Writes an unsigned Mozilla ARchive. Without an additional-section list it writes the original
/// layout (header, data, index); with one (even empty) — e.g. a product-information block — it writes
/// the signed-era layout with an empty signature block, exactly as Mozilla's <c>mar -H channel -V version -c</c>
/// and mardor do for unsigned packages.
/// </summary>
public static class MarWriter {

  /// <summary>Default permission bits for a member with none supplied (rw-r--r--).</summary>
  public const uint DefaultFlags = 0x1A4;

  /// <summary>Builds the product-information section payload (channel and version, each NUL-terminated).</summary>
  public static MarAdditionalSection ProductInformation(string channel, string productVersion)
    => new(MarAdditionalSection.ProductInformationId, Encoding.ASCII.GetBytes(channel + "\0" + productVersion + "\0"));

  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data, uint Flags)> files,
      IReadOnlyList<MarAdditionalSection>? additionalSections = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    var signedEra = additionalSections != null;
    var sections = additionalSections ?? [];

    using var ms = new MemoryStream();
    ms.Write("MAR1"u8);
    WriteUInt32(ms, 0); // index offset, patched below
    if (signedEra) {
      WriteUInt64(ms, 0); // total file size, patched below
      WriteUInt32(ms, 0); // no signatures
      WriteUInt32(ms, (uint)sections.Count);
      foreach (var s in sections) {
        WriteUInt32(ms, checked((uint)s.Data.Length + 8));
        WriteUInt32(ms, s.Id);
        ms.Write(s.Data);
      }
    }

    var index = new MemoryStream();
    foreach (var (name, data, flags) in files) {
      if (string.IsNullOrEmpty(name) || name.Contains('\0'))
        throw new ArgumentException($"Invalid MAR member name '{name}'.", nameof(files));
      var offset = checked((uint)ms.Position);
      ms.Write(data);
      WriteUInt32(index, offset);
      WriteUInt32(index, checked((uint)data.Length));
      WriteUInt32(index, flags);
      index.Write(Encoding.UTF8.GetBytes(name));
      index.WriteByte(0);
    }

    var indexOffset = checked((uint)ms.Position);
    WriteUInt32(ms, checked((uint)index.Length));
    index.Position = 0;
    index.CopyTo(ms);

    var image = ms.GetBuffer().AsSpan(0, (int)ms.Length);
    BinaryPrimitives.WriteUInt32BigEndian(image[4..], indexOffset);
    if (signedEra)
      BinaryPrimitives.WriteUInt64BigEndian(image[8..], (ulong)image.Length);
    output.Write(image);
  }

  private static void WriteUInt32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v);
    s.Write(b);
  }

  private static void WriteUInt64(Stream s, ulong v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(b, v);
    s.Write(b);
  }
}
