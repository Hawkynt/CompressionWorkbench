namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 2048 x 27 0 sub 0.
/// </summary>
public class Line2048X27_0Sub0 : IStaticCodeBook
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
        6, 5, 7, 5, 7, 5, 7, 5, 8, 5, 8, 5, 8, 5, 9, 5,
        9, 6, 10, 6, 10, 6, 11, 6, 11, 6, 11, 6, 11, 6, 11, 6,
        11, 6, 11, 6, 12, 7, 11, 7, 11, 7, 11, 7, 11, 7, 10, 7,
        11, 7, 11, 7, 12, 7, 11, 8, 11, 8, 11, 8, 11, 8, 13, 8,
        12, 9, 11, 9, 11, 9, 11, 10, 12, 10, 12, 9, 12, 10, 12, 11,
        14, 12, 16, 12, 12, 11, 14, 16, 17, 17, 17, 17, 17, 17, 17, 17,
        17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 16, 16, 16, 16
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
