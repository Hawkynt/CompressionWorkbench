/////////////////////////////////////////////////////////////////////////////////////
//  File:   IAudioEncoder.cs                                        3 Jan 23 PHR
/////////////////////////////////////////////////////////////////////////////////////

namespace SipLib.Media;

/// <summary>
/// Interface definition for an audio encoder
/// </summary>
public interface IAudioEncoder
{
    /// <summary>
    /// Encodes an input sample array of 16-bit PCMU audio samples into a byte array that can be sent as
    /// the payload of an RTP packet.
    /// </summary>
    /// <param name="InputSamples">Input linear 16-bit Mu-Law samples</param>
    /// <returns>Returns an array of encoded sample bytes</returns>
    byte[] Encode(short[] InputSamples);

    /// <summary>
    /// Gets the clock rate (samples/second)
    /// </summary>
    /// <value></value>
    int ClockRate { get; }

    /// <summary>
    /// Gets the sample rate in samples/second.
    /// </summary>
    /// <value></value>
    int SampleRate { get; }

    /// <summary>
    /// Amount to increment the RTP packet Time Stamp field by for each new packet.
    /// </summary>
    uint TimeStampIncrement { get; }

    /// <summary>
    /// Closes the encoder so that it can release any memory or resources it has been using.
    /// </summary>
    void CloseEncoder();
}
