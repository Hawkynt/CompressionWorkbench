/*-----------------------------------------------------------------------*
 *                         RESIDU.C                     *
 *-----------------------------------------------------------------------*
 * Compute the LPC residual by filtering the input speech through A(z)   *
 *-----------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    private unsafe void Residu(
         short* a,                           /* (i) Q12 : prediction coefficients                     */
         short m,                             /* (i)     : order of LP filter                          */
         short* x,                           /* (i)     : speech (values x[-m..-1] are needed         */
         short* y,                           /* (o) x2  : residual signal                             */
         short lg                             /* (i)     : size of filtering                           */
    )
    {
        short i, j;
        int s;

        for (i = 0; i < lg; i++)
        {
            s = L_mult(x[i], a[0]);

            for (j = 1; j <= m; j++)
                s = L_mac(s, a[j], x[i - j]);

            s = L_shl(s, 3 + 1);               /* saturation can occur here */
            y[i] = round(s);
        }

        return;
    }

}
