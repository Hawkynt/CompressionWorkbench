namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter3;

/// <summary>
/// Represents a page 7 1.
/// </summary>
public class Page7_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 6, 6, 7, 6, 8, 7, 9, 8,10, 9,11,11, 4,
         7, 7, 8, 7, 9, 9,10,10,11,11,11,11,12,12, 4, 7,
         7, 7, 7, 9, 9,10,10,11,11,12,12,12,11, 6, 8, 8,
         9, 9,10,10,11,11,12,12,13,12,13,13, 6, 8, 8, 9,
         9,10,11,11,11,12,12,13,14,13,13, 8, 9, 9,11,11,
        12,12,12,13,14,13,14,14,14,15, 8, 9, 9,11,11,11,
        12,13,14,13,14,15,17,14,15, 9,10,10,12,12,13,13,
        13,14,15,15,15,16,16,16, 9,11,11,12,12,13,13,14,
        14,14,15,16,16,16,16,10,12,12,13,13,14,14,15,15,
        15,16,17,17,17,17,10,12,11,13,13,15,14,15,14,16,
        17,16,16,16,16,11,13,12,14,14,14,14,15,16,17,16,
        17,17,17,17,11,13,12,14,14,14,15,17,16,17,17,17,
        17,17,17,12,13,13,15,16,15,16,17,17,16,16,17,17,
        17,17,12,13,13,15,15,15,16,17,17,17,16,17,16,17,
        17,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -522338304;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1620115456;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        7,
        6,
        8,
        5,
        9,
        4,
        10,
        3,
        11,
        2,
        12,
        1,
        13,
        0,
        14,
    };
}
