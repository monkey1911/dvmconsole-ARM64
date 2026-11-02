// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
*
* AmbeHost - 32-bit x86 out-of-process host for the external AMBE.DLL vocoder.
*
* AMBE.DLL is a 32-bit x86 library, so x64 and ARM64 builds of the console cannot load it.
* This small x86 program loads AMBE.DLL from its own folder and serves encode/decode requests
* from the console over a named pipe. On Windows on ARM it runs under the built-in x86 emulation.
*
* Usage: AmbeHost.exe <pipe name> [parent process id]
*
* Wire protocol (little endian, one request at a time):
*   request : u8 op, u8 reserved[3], u32 id, u32 payloadLength, payload
*   response: i32 status (0 = ok), u32 payloadLength, payload
*
*   OP_PING         payload: none                                    -> none
*   OP_CREATE       payload: u8 fullRate                             -> u32 id
*   OP_DESTROY      payload: none                                    -> none
*   OP_GET_DEC_MODE payload: none                                    -> i16 mode
*   OP_GET_ENC_MODE payload: none                                    -> i16 mode
*   OP_DECODE       payload: u16 dcmode, u16 nbits, i16 bits[nbits]  -> i16 samples[160]
*   OP_ENCODE       payload: u16 ecmode, i16 unk, u16 nbits,
*                            i16 samples[160]                         -> i16 bits[nbits]
*
* Exit codes: 0 = client disconnected, 2 = AMBE.DLL missing/invalid, 3 = pipe error, 4 = bad arguments.
*/

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define OP_PING         0
#define OP_CREATE       1
#define OP_DESTROY      2
#define OP_GET_DEC_MODE 3
#define OP_GET_ENC_MODE 4
#define OP_DECODE       5
#define OP_ENCODE       6

#define ST_OK           0
#define ST_BAD_OP       1
#define ST_BAD_ID       2
#define ST_BAD_PAYLOAD  3
#define ST_NO_SLOTS     4

#define MBE_SAMPLES     160
#define HALF_SAMPLES    (MBE_SAMPLES / 2)
#define DECSTATE_SIZE   2048
#define ENCSTATE_SIZE   6144
#define MAX_BITS        512
#define MAX_PAYLOAD     4096
#define MAX_INSTANCES   1024

/* AMBE.DLL exports use the cdecl calling convention */
typedef void     (__cdecl *pfn_init_dec)(void *state, int16_t mode);
typedef int16_t  (__cdecl *pfn_get_dec_mode)(void *state);
typedef uint32_t (__cdecl *pfn_voice_dec)(int16_t *samples, int16_t sampleLength, int16_t *codeword,
                                          int16_t bitSteal, uint16_t cmode, int16_t n, void *state);
typedef void     (__cdecl *pfn_init_enc)(void *state, int16_t mode, int16_t initialize);
typedef int16_t  (__cdecl *pfn_get_enc_mode)(void *state);
typedef uint32_t (__cdecl *pfn_voice_enc)(int16_t *codeword, int16_t bitSteal, int16_t *samples,
                                          int16_t sampleLength, uint16_t cmode, int16_t n, int16_t unk, void *state);

static pfn_init_dec     ambe_init_dec;
static pfn_get_dec_mode ambe_get_dec_mode;
static pfn_voice_dec    ambe_voice_dec;
static pfn_init_enc     ambe_init_enc;
static pfn_get_enc_mode ambe_get_enc_mode;
static pfn_voice_enc    ambe_voice_enc;

typedef struct {
    int used;
    uint8_t *decState;
    uint8_t *encState;
} instance_t;

static instance_t instances[MAX_INSTANCES];

static DWORD WINAPI parentWatchdog(LPVOID param)
{
    HANDLE parent = (HANDLE)param;
    WaitForSingleObject(parent, INFINITE);
    ExitProcess(0);
    return 0;
}

static int loadAmbe(void)
{
    char path[MAX_PATH];
    DWORD len = GetModuleFileNameA(NULL, path, MAX_PATH);
    if (len == 0 || len >= MAX_PATH)
        return 0;

    char *slash = strrchr(path, '\\');
    if (slash == NULL)
        return 0;
    slash[1] = '\0';
    if (strlen(path) + strlen("AMBE.DLL") >= MAX_PATH)
        return 0;
    strcat(path, "AMBE.DLL");

    HMODULE lib = LoadLibraryExA(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (lib == NULL) {
        fprintf(stderr, "AmbeHost: cannot load %s (error %lu)\n", path, GetLastError());
        return 0;
    }

    ambe_init_dec     = (pfn_init_dec)GetProcAddress(lib, "ambe_init_dec");
    ambe_get_dec_mode = (pfn_get_dec_mode)GetProcAddress(lib, "ambe_get_dec_mode");
    ambe_voice_dec    = (pfn_voice_dec)GetProcAddress(lib, "ambe_voice_dec");
    ambe_init_enc     = (pfn_init_enc)GetProcAddress(lib, "ambe_init_enc");
    ambe_get_enc_mode = (pfn_get_enc_mode)GetProcAddress(lib, "ambe_get_enc_mode");
    ambe_voice_enc    = (pfn_voice_enc)GetProcAddress(lib, "ambe_voice_enc");

    if (!ambe_init_dec || !ambe_get_dec_mode || !ambe_voice_dec ||
        !ambe_init_enc || !ambe_get_enc_mode || !ambe_voice_enc) {
        fprintf(stderr, "AmbeHost: %s is missing required exports\n", path);
        return 0;
    }

    return 1;
}

static int readExact(HANDLE pipe, void *buf, DWORD len)
{
    uint8_t *p = (uint8_t *)buf;
    while (len > 0) {
        DWORD got = 0;
        if (!ReadFile(pipe, p, len, &got, NULL) || got == 0)
            return 0;
        p += got;
        len -= got;
    }
    return 1;
}

static int writeExact(HANDLE pipe, const void *buf, DWORD len)
{
    const uint8_t *p = (const uint8_t *)buf;
    while (len > 0) {
        DWORD put = 0;
        if (!WriteFile(pipe, p, len, &put, NULL) || put == 0)
            return 0;
        p += put;
        len -= put;
    }
    return 1;
}

static int respond(HANDLE pipe, int32_t status, const void *payload, uint32_t len)
{
    uint8_t hdr[8];
    memcpy(hdr, &status, 4);
    memcpy(hdr + 4, &len, 4);
    if (!writeExact(pipe, hdr, sizeof(hdr)))
        return 0;
    if (len > 0 && !writeExact(pipe, payload, len))
        return 0;
    return 1;
}

static instance_t *lookup(uint32_t id)
{
    if (id == 0 || id > MAX_INSTANCES)
        return NULL;
    instance_t *inst = &instances[id - 1];
    return inst->used ? inst : NULL;
}

static int serve(HANDLE pipe)
{
    static uint8_t payload[MAX_PAYLOAD];
    static uint8_t out[MAX_PAYLOAD];

    for (;;) {
        uint8_t hdr[12];
        if (!readExact(pipe, hdr, sizeof(hdr)))
            return 0;

        uint8_t op = hdr[0];
        uint32_t id, len;
        memcpy(&id, hdr + 4, 4);
        memcpy(&len, hdr + 8, 4);

        if (len > MAX_PAYLOAD)
            return 0; /* protocol violation; drop the connection */
        if (len > 0 && !readExact(pipe, payload, len))
            return 0;

        int ok = 1;
        switch (op) {
        case OP_PING:
            ok = respond(pipe, ST_OK, NULL, 0);
            break;

        case OP_CREATE: {
            if (len != 1) { ok = respond(pipe, ST_BAD_PAYLOAD, NULL, 0); break; }
            uint32_t slot;
            for (slot = 0; slot < MAX_INSTANCES && instances[slot].used; slot++)
                ;
            if (slot == MAX_INSTANCES) { ok = respond(pipe, ST_NO_SLOTS, NULL, 0); break; }

            instance_t *inst = &instances[slot];
            inst->decState = (uint8_t *)calloc(1, DECSTATE_SIZE);
            inst->encState = (uint8_t *)calloc(1, ENCSTATE_SIZE);
            if (!inst->decState || !inst->encState) {
                free(inst->decState); free(inst->encState);
                inst->decState = inst->encState = NULL;
                ok = respond(pipe, ST_NO_SLOTS, NULL, 0);
                break;
            }

            int16_t mode = payload[0] ? 0 : 1; /* FULL_RATE = 0, HALF_RATE = 1 */
            ambe_init_dec(inst->decState, mode);
            ambe_init_enc(inst->encState, mode, 1);
            inst->used = 1;

            uint32_t newId = slot + 1;
            ok = respond(pipe, ST_OK, &newId, sizeof(newId));
            break;
        }

        case OP_DESTROY: {
            instance_t *inst = lookup(id);
            if (inst) {
                free(inst->decState); free(inst->encState);
                memset(inst, 0, sizeof(*inst));
            }
            ok = respond(pipe, ST_OK, NULL, 0);
            break;
        }

        case OP_GET_DEC_MODE:
        case OP_GET_ENC_MODE: {
            instance_t *inst = lookup(id);
            if (!inst) { ok = respond(pipe, ST_BAD_ID, NULL, 0); break; }
            int16_t mode = (op == OP_GET_DEC_MODE) ? ambe_get_dec_mode(inst->decState)
                                                   : ambe_get_enc_mode(inst->encState);
            ok = respond(pipe, ST_OK, &mode, sizeof(mode));
            break;
        }

        case OP_DECODE: {
            instance_t *inst = lookup(id);
            if (!inst) { ok = respond(pipe, ST_BAD_ID, NULL, 0); break; }
            if (len < 4) { ok = respond(pipe, ST_BAD_PAYLOAD, NULL, 0); break; }

            uint16_t dcmode, nbits;
            memcpy(&dcmode, payload, 2);
            memcpy(&nbits, payload + 2, 2);
            if (nbits > MAX_BITS || len != 4u + nbits * 2u) { ok = respond(pipe, ST_BAD_PAYLOAD, NULL, 0); break; }

            int16_t bits[MAX_BITS];
            memcpy(bits, payload + 4, nbits * 2u);

            int16_t samples[MBE_SAMPLES];
            memset(samples, 0, sizeof(samples));
            ambe_voice_dec(samples, HALF_SAMPLES, bits, 0, dcmode, 0, inst->decState);
            ambe_voice_dec(samples + HALF_SAMPLES, HALF_SAMPLES, bits, 0, dcmode, 1, inst->decState);

            ok = respond(pipe, ST_OK, samples, sizeof(samples));
            break;
        }

        case OP_ENCODE: {
            instance_t *inst = lookup(id);
            if (!inst) { ok = respond(pipe, ST_BAD_ID, NULL, 0); break; }
            if (len != 6u + MBE_SAMPLES * 2u) { ok = respond(pipe, ST_BAD_PAYLOAD, NULL, 0); break; }

            uint16_t ecmode, nbits;
            int16_t unk;
            memcpy(&ecmode, payload, 2);
            memcpy(&unk, payload + 2, 2);
            memcpy(&nbits, payload + 4, 2);
            if (nbits > MAX_BITS) { ok = respond(pipe, ST_BAD_PAYLOAD, NULL, 0); break; }

            int16_t samples[MBE_SAMPLES];
            memcpy(samples, payload + 6, sizeof(samples));

            int16_t bits[MAX_BITS];
            memset(bits, 0, sizeof(bits));
            ambe_voice_enc(bits, 0, samples, HALF_SAMPLES, ecmode, 0, unk, inst->encState);
            ambe_voice_enc(bits, 0, samples + HALF_SAMPLES, HALF_SAMPLES, ecmode, 1, unk, inst->encState);

            memcpy(out, bits, nbits * 2u);
            ok = respond(pipe, ST_OK, out, nbits * 2u);
            break;
        }

        default:
            ok = respond(pipe, ST_BAD_OP, NULL, 0);
            break;
        }

        if (!ok)
            return 0;
    }
}

int main(int argc, char **argv)
{
    if (argc < 2 || strlen(argv[1]) > 200) {
        fprintf(stderr, "usage: AmbeHost.exe <pipe name> [parent pid]\n");
        return 4;
    }

    /* exit automatically when the console that started us goes away */
    if (argc >= 3) {
        DWORD pid = (DWORD)strtoul(argv[2], NULL, 10);
        HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, pid);
        if (parent == NULL)
            return 0;
        HANDLE t = CreateThread(NULL, 0, parentWatchdog, parent, 0, NULL);
        if (t) CloseHandle(t);
    }

    if (!loadAmbe())
        return 2;

    char pipeName[256];
    snprintf(pipeName, sizeof(pipeName), "\\\\.\\pipe\\%s", argv[1]);

    HANDLE pipe = CreateNamedPipeA(pipeName,
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 65536, 65536, 0, NULL);
    if (pipe == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "AmbeHost: cannot create pipe %s (error %lu)\n", pipeName, GetLastError());
        return 3;
    }

    if (!ConnectNamedPipe(pipe, NULL) && GetLastError() != ERROR_PIPE_CONNECTED) {
        CloseHandle(pipe);
        return 3;
    }

    serve(pipe);

    CloseHandle(pipe);
    return 0;
}
