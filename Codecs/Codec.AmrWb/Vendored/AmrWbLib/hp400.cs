/*-----------------------------------------------------------------------*
 *                         HP400.C                     *
 *-----------------------------------------------------------------------*
 * 2nd order high pass filter with cut off frequency at 400 Hz.          *
 * Designed with cheby2 function in MATLAB.                              *
 * Optimized for fixed-point to get the following frequency response:    *
 *                                                                       *
 *  frequency:     0Hz   100Hz  200Hz  300Hz  400Hz  630Hz  1.5kHz  3kHz *
 *  dB loss:     -infdB  -30dB  -20dB  -10dB  -3dB   +6dB    +1dB    0dB *
 *                                                                       *
 * Algorithm:                                                            *
 *                                                                       *
 *  y[i] = b[0]*x[i] + b[1]*x[i-1] + b[2]*x[i-2]                         *
 *                   + a[1]*y[i-1] + a[2]*y[i-2];                        *
 *                                                                       *
 *  short b[3] = {3660, -7320,  3660};       in Q12                     *
 *  short a[3] = {4096,  7320, -3540};       in Q12                     *
 *                                                                       *
 *  float -->   b[3] = {0.893554687, -1.787109375,  0.893554687};        *
 *              a[3] = {1.000000000,  1.787109375, -0.864257812};        *
 *-----------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    /* filter coefficients  */

    private static short[] hp400_b = new short[3] { 915, -1830, 915 };         /* Q12 (/4) */
    private static short[] hp400_a = new short[3] { 16384, 29280, -14160 };    /* Q12 (x4) */


    /* Initialization of static values */

    void Init_HP400_12k8(short[] mem)
    {
        Set_zero(mem, 6);
    }

    void HP400_12k8(
         short[] signal,                      /* input signal / output is divided by 16 */
         short lg,                            /* lenght of signal    */
         short[] mem                          /* filter memory [6]   */
)
    {
        short i, x2;
        short y2_hi, y2_lo, y1_hi, y1_lo, x0, x1;
        int L_tmp;

        y2_hi = mem[0];
        y2_lo = mem[1];
        y1_hi = mem[2];
        y1_lo = mem[3];
        x0 = mem[4];
        x1 = mem[5];

        for (i = 0; i < lg; i++)
        {
            x2 = x1;
            x1 = x0;
            x0 = signal[i];

            /* y[i] = b[0]*x[i] + b[1]*x[i-1] + b140[2]*x[i-2]  */
            /* + a[1]*y[i-1] + a[2] * y[i-2];  */

            L_tmp = 16384;                    /* rounding to maximise precision */
            L_tmp = L_mac(L_tmp, y1_lo, hp400_a[1]);
            L_tmp = L_mac(L_tmp, y2_lo, hp400_a[2]);
            L_tmp = L_shr(L_tmp, 15);
            L_tmp = L_mac(L_tmp, y1_hi, hp400_a[1]);
            L_tmp = L_mac(L_tmp, y2_hi, hp400_a[2]);
            L_tmp = L_mac(L_tmp, x0, hp400_b[0]);
            L_tmp = L_mac(L_tmp, x1, hp400_b[1]);
            L_tmp = L_mac(L_tmp, x2, hp400_b[2]);

            L_tmp = L_shl(L_tmp, 1);           /* coeff Q12 --> Q13 */

            y2_hi = y1_hi;
            y2_lo = y1_lo;
            L_Extract(L_tmp, ref y1_hi, ref y1_lo);

            /* signal is divided by 16 to avoid overflow in energy computation */
            signal[i] = round(L_tmp);
        }

        mem[0] = y2_hi;
        mem[1] = y2_lo;
        mem[2] = y1_hi;
        mem[3] = y1_lo;
        mem[4] = x0;
        mem[5] = x1;
    }


}
