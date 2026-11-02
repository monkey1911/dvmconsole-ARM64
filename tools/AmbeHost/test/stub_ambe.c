// SPDX-License-Identifier: AGPL-3.0-only
/**
* Test-only stand-in for AMBE.DLL, used by CI to exercise AmbeHost and the console's
* AmbeHostClient on real Windows without the proprietary vocoder.
*
* It exports the same six functions with deterministic, trivial behavior:
*  - init_* writes a marker and the mode into the state buffer
*  - get_*_mode returns that mode
*  - voice_dec fills each half-frame with (n * 1000 + i + first codeword bit)
*  - voice_enc writes (samples[0] + n) into the first two codeword bits and counts calls
* It contains no vocoder code and is never shipped.
*/

#include <stdint.h>

#define EXPORT __declspec(dllexport)

EXPORT void __cdecl ambe_init_dec(void *state, int16_t mode)
{
    int16_t *s = (int16_t *)state;
    s[0] = 0x5A5A; s[1] = mode;
}

EXPORT int16_t __cdecl ambe_get_dec_mode(void *state)
{
    return ((int16_t *)state)[1];
}

EXPORT uint32_t __cdecl ambe_voice_dec(int16_t *samples, int16_t sampleLength, int16_t *codeword,
                                       int16_t bitSteal, uint16_t cmode, int16_t n, void *state)
{
    (void)bitSteal; (void)cmode;
    for (int i = 0; i < sampleLength; i++)
        samples[i] = (int16_t)(n * 1000 + i + codeword[0]);
    ((int16_t *)state)[2]++;
    return 0;
}

EXPORT void __cdecl ambe_init_enc(void *state, int16_t mode, int16_t initialize)
{
    int16_t *s = (int16_t *)state;
    s[0] = 0x5A5A; s[1] = mode; s[2] = initialize;
}

EXPORT int16_t __cdecl ambe_get_enc_mode(void *state)
{
    return ((int16_t *)state)[1];
}

EXPORT uint32_t __cdecl ambe_voice_enc(int16_t *codeword, int16_t bitSteal, int16_t *samples,
                                       int16_t sampleLength, uint16_t cmode, int16_t n, int16_t unk, void *state)
{
    (void)bitSteal; (void)sampleLength; (void)cmode; (void)unk;
    codeword[n] = (int16_t)(samples[0] + n);
    ((int16_t *)state)[3]++;
    return 0;
}
