namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 256 x 7 class 1.
/// </summary>
public class Line256X7Class1 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        5, 6, 8, 15, 6, 9, 10, 15, 10, 11, 12, 15, 15, 15, 15, 15,
        4, 6, 7, 15, 6, 7, 8, 15, 9, 8, 9, 15, 15, 15, 15, 15,
        6, 8, 9, 15, 7, 7, 8, 15, 10, 9, 10, 15, 15, 15, 15, 15,
        15, 13, 15, 15, 15, 10, 11, 15, 15, 13, 13, 15, 15, 15, 15, 15,
        4, 6, 7, 15, 6, 8, 9, 15, 10, 10, 12, 15, 15, 15, 15, 15,
        2, 5, 6, 15, 5, 6, 7, 15, 8, 6, 7, 15, 15, 15, 15, 15,
        5, 6, 8, 15, 5, 6, 7, 15, 9, 6, 7, 15, 15, 15, 15, 15,
        14, 12, 13, 15, 12, 10, 11, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        7, 8, 9, 15, 9, 10, 10, 15, 15, 14, 14, 15, 15, 15, 15, 15,
        5, 6, 7, 15, 7, 8, 9, 15, 12, 9, 10, 15, 15, 15, 15, 15,
        7, 7, 9, 15, 7, 7, 8, 15, 12, 8, 9, 15, 15, 15, 15, 15,
        13, 13, 14, 15, 12, 11, 12, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        13, 13, 13, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 12, 13, 15, 15, 12, 13, 15, 15, 14, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 13, 15, 15, 15, 15, 15, 15, 15, 15, 15
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
