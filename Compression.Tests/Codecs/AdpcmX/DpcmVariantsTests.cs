#pragma warning disable CS1591
using Sdx2 = Codec.AdpcmX.Sdx2;
using Derf = Codec.AdpcmX.Derf;
using Gremlin = Codec.AdpcmX.Gremlin;
using XanDpcm = Codec.AdpcmX.XanDpcm;
using InterplayDpcm = Codec.AdpcmX.InterplayDpcm;

namespace Compression.Tests.Codecs.AdpcmX;

[TestFixture]
public class DpcmVariantsTests {

  // SDX2: byte value n (signed). Odd low bit ⇒ differential, even ⇒ reset-then-add.
  // SquareTable[n] = sign(n) * n*n*2. Byte 3 (odd): pred 0 + 3*3*2=18 ⇒ 18.
  // Byte 5 (odd): 18 + 5*5*2=50 ⇒ 68. Byte 4 (even): reset ⇒ 0 + 4*4*2=32 ⇒ 32.
  [Test]
  public void Sdx2_Mono_SquaresAndResets() {
    var pcm = Sdx2.Decode(new byte[] { 3, 5, 4 }, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 18, 68, 32 }));
  }

  // SDX2 negative byte: 0xFD = -3 (odd) ⇒ -(3*3*2) = -18.
  [Test]
  public void Sdx2_NegativeDelta() {
    var pcm = Sdx2.Decode(new byte[] { 0xFD }, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { -18 }));
  }

  // DERF: byte sign bit 0x80, magnitude indexes derf_steps. Byte 5 ⇒ +steps[5]=5 ⇒5.
  // Byte 0x85 ⇒ -steps[5]=-5 ⇒ 0. Byte 0x0F ⇒ +steps[15]=16 ⇒16.
  [Test]
  public void Derf_StepTableDeltas() {
    var pcm = Derf.Decode(new byte[] { 5, 0x85, 0x0F }, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 5, 0, 16 }));
  }

  // DERF magnitude clamps to 95: byte 0x7F (magnitude 127 → clamped 95) ⇒ +steps[95]=32767.
  [Test]
  public void Derf_MagnitudeClampsTo95() {
    var pcm = Derf.Decode(new byte[] { 0x7F }, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 32767 }));
  }

  // Interplay MVE: byte indexes the 256-entry delta table added to the predictor.
  // Table[1]=1, Table[44]=47 (the irregular jump), Table[255]=-1. Seed 1000.
  [Test]
  public void Interplay_TableDeltas() {
    var pcm = InterplayDpcm.Decode(new byte[] { 1, 44, 255 }, channels: 1, startPredictors: [1000]);
    Assert.That(pcm, Is.EqualTo(new short[] { 1001, 1048, 1047 }));
  }

  [Test]
  public void Interplay_TableLengthIs256()
    => Assert.That(InterplayDpcm.DeltaTable.Length, Is.EqualTo(256));

  // Xan: shift starts at 4. Byte 0x00: n=0 ⇒ shift -= 0 ⇒4; diff=sign_extend((0)<<8)>>4=0 ⇒ pred 0.
  // Byte 0x04: n=0 (low 2 bits of 4 = 0); diff = (4 & ~3)=4, <<8=0x400, sign-extended=1024, >>4 = 64.
  // pred 0+64 ⇒ 64.
  [Test]
  public void Xan_ShiftBasedDelta() {
    var pcm = XanDpcm.Decode(new byte[] { 0x00, 0x04 }, channels: 1, startPredictors: [0]);
    Assert.That(pcm, Is.EqualTo(new short[] { 0, 64 }));
  }

  // Xan shift adjust: byte 0x03 ⇒ n=3 ⇒ shift++ (4→5); diff=(3&~3)=0 ⇒ 0 ⇒ pred unchanged.
  [Test]
  public void Xan_ShiftIncrementOnLowBitsThree() {
    var pcm = XanDpcm.Decode(new byte[] { 0x03 }, channels: 1, startPredictors: [500]);
    Assert.That(pcm, Is.EqualTo(new short[] { 500 }));
  }

  // Gremlin: procedurally generated table. array[0]=0, and the first generated delta:
  // delta += code(64)>>5 = 2 ⇒ array[1]=2, array[2]=-2. Predictor starts at 0.
  [Test]
  public void Gremlin_GeneratedTable() {
    Assert.That(Gremlin.DeltaTable[0], Is.EqualTo(0));
    Assert.That(Gremlin.DeltaTable[1], Is.EqualTo(2));
    Assert.That(Gremlin.DeltaTable[2], Is.EqualTo(-2));
    var pcm = Gremlin.Decode(new byte[] { 1, 2 }, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 2, 0 }));
  }

  // Stereo interleaving: bytes alternate channels. Gremlin with two channels.
  [Test]
  public void Dpcm_StereoInterleaves() {
    var pcm = Gremlin.Decode(new byte[] { 1, 1, 2, 2 }, channels: 2);
    // ch0 gets bytes 0,2 ; ch1 gets bytes 1,3. array[1]=2, array[2]=-2.
    // ch0: 0+2=2, then +(-2)=0 ; ch1: 0+2=2, then +(-2)=0. Interleaved: 2,2,0,0.
    Assert.That(pcm, Is.EqualTo(new short[] { 2, 2, 0, 0 }));
  }
}
