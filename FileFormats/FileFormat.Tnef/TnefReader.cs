#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Tnef;

/// <summary>
/// Reads MS-TNEF (Transport Neutral Encapsulation Format) files, commonly
/// known as winmail.dat. Extracts file attachments from the TNEF stream.
/// </summary>
public sealed class TnefReader : IDisposable {
  // TNEF signature: 0x223E9F78 (LE)
  public const uint TnefSignature = 0x223E9F78;

  // Attribute levels
  private const byte LvlMessage    = 0x01;
  private const byte LvlAttachment = 0x02;

  // Attribute IDs (level | id | type combined)
  private const uint AttAttachTitle    = 0x00018010; // Attachment filename
  private const uint AttAttachData     = 0x0001800F; // Attachment data
  private const uint AttAttachRendData = 0x00019002; // Attachment rendering data (marks new attachment)

  private readonly byte[] _data;
  private readonly List<TnefEntry> _entries = [];

  public IReadOnlyList<TnefEntry> Entries => _entries;
  public ushort Key { get; }

  public TnefReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 6)
      throw new InvalidDataException("TNEF: file too small.");

    var sig = BinaryPrimitives.ReadUInt32LittleEndian(_data);
    if (sig != TnefSignature)
      throw new InvalidDataException("TNEF: invalid signature.");

    var pos = 6; // skip signature (4) + key (2)

    string? currentName = null;
    byte[]? currentData = null;

    while (pos + 5 <= _data.Length) {
      var level = _data[pos++];
      if (level != LvlMessage && level != LvlAttachment) break;

      if (pos + 8 > _data.Length) break;
      var attrId = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;
      var attrLen = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;

      if (attrLen < 0 || pos + attrLen > _data.Length) break;
      var attrData = _data.AsSpan(pos, attrLen).ToArray();
      pos += attrLen;

      // Skip 2-byte checksum
      pos += 2;
      if (pos > _data.Length) break;

      if (level == LvlAttachment) {
        // AttachRendData marks the start of a new attachment
        if (attrId == AttAttachRendData) {
          // Flush previous attachment if any
          if (currentData != null) {
            _entries.Add(new TnefEntry {
              Name = currentName ?? $"attachment_{_entries.Count}",
              Size = currentData.Length,
              Data = currentData,
            });
          }
          currentName = null;
          currentData = null;
        } else if (attrId == AttAttachTitle) {
          // Null-terminated filename
          var nameEnd = Array.IndexOf(attrData, (byte)0);
          currentName = nameEnd >= 0
            ? Encoding.ASCII.GetString(attrData, 0, nameEnd)
            : Encoding.ASCII.GetString(attrData);
        } else if (attrId == AttAttachData) {
          currentData = attrData;
        }
      }
    }

    // Flush last attachment
    if (currentData != null) {
      _entries.Add(new TnefEntry {
        Name = currentName ?? $"attachment_{_entries.Count}",
        Size = currentData.Length,
        Data = currentData,
      });
    }
  }

  public byte[] Extract(TnefEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data ?? [];
  }

  public void Dispose() { }
}
