namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Coupled.Chapter2;

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
         1, 4, 4, 5, 7, 7, 6, 7, 7, 4, 6, 6,10,11,10,10,
        10,11, 4, 6, 6,10,10,11,10,11,10, 5,10,10, 9,12,
        11,10,12,12, 7,10,10,12,12,12,12,13,13, 7,11,10,
        11,12,12,12,13,13, 6,11,10,10,12,12,11,12,12, 7,
        11,10,12,13,13,12,12,12, 7,10,11,12,13,13,12,12,
        12,
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
