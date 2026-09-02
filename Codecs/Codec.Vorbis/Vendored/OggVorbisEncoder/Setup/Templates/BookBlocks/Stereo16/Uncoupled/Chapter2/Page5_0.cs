namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Uncoupled.Chapter2;

/// <summary>
/// Represents a page 5 0.
/// </summary>
public class Page5_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 4;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 5, 7, 7, 5, 7, 7, 5, 8, 8, 7, 9, 9, 7,
         9,10, 5, 8, 8, 7,10, 9, 7,10, 9, 5, 8, 8, 8,11,
        10, 8,10,10, 7,10,10, 9, 9,12,10,12,12, 7,10,10,
         9,12,10,10,11,12, 5, 8, 8, 8,10,10, 8,11,11, 7,
        11,10,10,12,11, 9,10,12, 7,10,11,10,12,12, 9,12,
         9,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -529137664;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1618345984;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 2;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        1,
        0,
        2,
    };
}
