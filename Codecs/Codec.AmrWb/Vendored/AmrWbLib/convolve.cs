/*------------------------------------------------------------------------*
 *                         CONVOLVE.C                                     *
 *------------------------------------------------------------------------*
 * Perform the convolution between two vectors x[] and h[] and            *
 * write the result in the vector y[].                                    *
 * All vectors are of length L.                                           *
 *------------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    private unsafe void Convolve(
         short* x,                           /* (i)        : input vector                           */
         short* h,                           /* (i) Q15    : impulse response                       */
         short* y,                           /* (o) 12 bits: output vector                          */
         short L                              /* (i)        : vector size                            */
    )
    {
        short i, n;
        int L_sum;

        for (n = 0; n < L; n++)
        {
            L_sum = 0;
            for (i = 0; i <= n; i++)
                L_sum = L_mac(L_sum, x[i], h[n - i]);

            y[n] = round(L_sum);
        }

        return;
    }

}
