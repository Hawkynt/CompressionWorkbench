namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 128 x 11 0 sub 0.
/// </summary>
public class Line128X11_0Sub0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        5, 5, 5, 5, 5, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 5,
        6, 5, 6, 5, 6, 5, 6, 5, 6, 5, 6, 6, 6, 6, 7, 6,
        7, 6, 7, 6, 7, 6, 7, 6, 7, 6, 8, 6, 8, 6, 8, 7,
        8, 7, 8, 7, 8, 7, 9, 7, 9, 8, 9, 8, 9, 8, 10, 8,
        10, 9, 10, 9, 10, 9, 11, 9, 11, 9, 10, 10, 11, 10, 11, 10,
        11, 11, 11, 11, 11, 11, 12, 13, 14, 14, 14, 15, 15, 16, 16, 16,
        17, 15, 16, 15, 16, 16, 17, 17, 16, 17, 17, 17, 17, 17, 17, 17,
        17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = CodeBookMapType.None;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;
    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}
