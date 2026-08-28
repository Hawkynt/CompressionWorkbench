/*-------------------------------------------------------------------*
 *                         G_PITCH.C                 *
 *-------------------------------------------------------------------*
 * Compute the gain of pitch. Result in Q12                          *
 *  if (gain < 0)  gain =0                                           *
 *  if (gain > 1.2) gain =1.2                                        *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    private unsafe short G_pitch(                            /* (o) Q14 : Gain of pitch lag saturated to 1.2   */
         short* xn,                          /* (i)     : Pitch target.                        */
         short* y1,                          /* (i)     : filtered adaptive codebook.          */
         short* g_coeff,                     /* : Correlations need for gain quantization.     */
         short L_subfr                        /* : Length of subframe.                          */
    )
    {
        short i;
        short xy, yy, exp_xy, exp_yy, gain;

        /* Compute scalar product <y1[],y1[]> */
        exp_yy = 0;
        yy = extract_h(Dot_product12(y1, y1, L_subfr, ref exp_yy));

        /* Compute scalar product <xn[],y1[]> */
        exp_xy = 0;
        xy = extract_h(Dot_product12(xn, y1, L_subfr, ref exp_xy));

        g_coeff[0] = yy;
        g_coeff[1] = exp_yy;
        g_coeff[2] = xy;
        g_coeff[3] = exp_xy;

        /* If (xy < 0) gain = 0 */
        if (xy < 0)
            return ((short)0);

        /* compute gain = xy/yy */

        xy = shr(xy, 1);                       /* Be sure xy < yy */
        gain = div_s(xy, yy);

        i = add(exp_xy, 1 - 1);                /* -1 -> gain in Q14 */
        i = sub(i, exp_yy);

        gain = shl(gain, i);                   /* saturation can occur here */

        /* if (gain > 1.2) gain = 1.2  in Q14 */
        if (sub(gain, 19661) > 0)
        {
            gain = 19661;
        }
        return (gain);
    }
}
