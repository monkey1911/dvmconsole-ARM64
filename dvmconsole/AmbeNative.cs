// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2023 Bryan Biedenkapp, N2PLL
*
*/

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using fnecore;

namespace dvmconsole
{
    /// <summary>
    /// Implements P/Invoke to callback into external AMBE encoder/decoder library.
    /// </summary>
    /// <remarks>
    /// AMBE.DLL is a 32-bit x86 software vocoder library. x86 builds call it in-process; x64 and ARM64
    /// builds call it through the AmbeHost.exe helper process (see <see cref="AmbeHostClient"/>).
    /// </remarks>
    public class AmbeVocoder
    {
        private const int MBE_SAMPLES_LENGTH = 160;
        private const short NO_BIT_STEAL = 0;
        private const short ENC_UNK = 8192;

        private const ushort ECMODE_NOISE_SUPPRESS = 0x40;
        private const ushort ECMODE_AGC = 0x2000;

        private const int DECSTATE_SIZE = 2048;
        private byte[] decoderState;
        private const int ENCSTATE_SIZE = 6144;
        private byte[] encoderState;

        private static readonly string appDirectory = AppContext.BaseDirectory;
        private static readonly Lazy<bool> inProcess = new Lazy<bool>(() =>
            IsLoadableInProcess(Path.Combine(appDirectory, "AMBE.DLL")));
        private static int hostFailureLogged;
        private static long hostRetryAtTicks;
        private const int HOST_RETRY_SECONDS = 30;

        private readonly bool fullRate;
        private AmbeHostClient host;
        private uint hostId;

        /// <summary>
        /// Returns true if the external AMBE vocoder can be used from this process: AMBE.DLL is present and
        /// either loads in-process or AmbeHost.exe is present to host it.
        /// </summary>
        /// <param name="directory">Folder containing AMBE.DLL (and AmbeHost.exe for x64/ARM64 builds).</param>
        public static bool IsAvailable(string directory)
        {
            string dllPath = Path.Combine(directory, "AMBE.DLL");
            if (!File.Exists(dllPath))
                return false;

            return IsLoadableInProcess(dllPath) || AmbeHostClient.IsHostPresent(directory);
        }

        /// <summary>
        /// Checks whether the given native DLL was built for the same CPU architecture as the running process.
        /// A mismatched DLL (e.g. the 32-bit x86 AMBE.DLL inside an ARM64 or x64 process) cannot be loaded.
        /// </summary>
        /// <param name="dllPath">Full path to the native DLL.</param>
        /// <returns>True if the DLL's PE machine type matches the process architecture.</returns>
        public static bool IsLoadableInProcess(string dllPath)
        {
            ushort expected = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => 0x014C,
                Architecture.X64 => 0x8664,
                Architecture.Arm64 => 0xAA64,
                _ => 0
            };

            try
            {
                using (FileStream fs = File.OpenRead(dllPath))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (br.ReadUInt16() != 0x5A4D) // "MZ"
                        return false;
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peOffset = br.ReadInt32();
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) // "PE\0\0"
                        return false;
                    return br.ReadUInt16() == expected;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public enum AmbeMode : short
        {
            FULL_RATE = 0x00,
            HALF_RATE = 0x01,

            NOT_VALID = 0x03
        } // public enum AmbeMode

        private AmbeMode mode;
        private int frameLengthInBytes;
        private int frameLengthInBits;

        private ushort dcmode;
        private ushort ecmode;

        private MBEInterleaver interleaver;

        /*
        ** Properties
        */

        /// <summary>
        /// Gets the currently operating decoder mode.
        /// </summary>
        public AmbeMode DecoderMode
        {
            get
            {
                if (!inProcess.Value)
                    return (AmbeMode)WithHost(h => h.GetDecoderMode(hostId), (short)AmbeMode.NOT_VALID);

                unsafe
                {
                    fixed (byte* state = decoderState)
                        return (AmbeMode)ambe_get_dec_mode((IntPtr)state);
                }
            }
        }

        /// <summary>
        /// Gets the currently operating encoder mode.
        /// </summary>
        public AmbeMode EncoderMode
        {
            get
            {
                if (!inProcess.Value)
                    return (AmbeMode)WithHost(h => h.GetEncoderMode(hostId), (short)AmbeMode.NOT_VALID);

                unsafe
                {
                    fixed (byte* state = encoderState)
                        return (AmbeMode)ambe_get_enc_mode((IntPtr)state);
                }
            }
        }

        /// <summary>
        /// Gets or sets whether the external AMBE encoder applies automatic gain control.
        /// </summary>
        public bool EncoderAgcEnabled
        {
            get => (ecmode & ECMODE_AGC) == ECMODE_AGC;
            set
            {
                ecmode = value
                    ? (ushort)(ecmode | ECMODE_AGC)
                    : (ushort)(ecmode & ~ECMODE_AGC);
            }
        }

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="AmbeVocoder"/> class.
        /// </summary>
        /// <param name="fullRate"></param>
        public AmbeVocoder(bool fullRate = true)
        {
            this.fullRate = fullRate;

            if (fullRate)
            {
                this.mode = AmbeMode.FULL_RATE;
                this.interleaver = new MBEInterleaver(MBE_MODE.IMBE_88BIT);
                this.frameLengthInBits = 88;
                this.frameLengthInBytes = 11;
            }
            else
            {
                this.mode = AmbeMode.HALF_RATE;
                this.interleaver = new MBEInterleaver(MBE_MODE.DMR_AMBE);
                this.frameLengthInBits = 49;
                this.frameLengthInBytes = 7;
            }

            this.decoderState = new byte[DECSTATE_SIZE];
            this.dcmode = 0;

            this.encoderState = new byte[ENCSTATE_SIZE];
            this.ecmode = ECMODE_NOISE_SUPPRESS | ECMODE_AGC;

            if (!inProcess.Value)
            {
                // AMBE.DLL cannot load in this process (x64/ARM64); the codec state lives in AmbeHost.exe
                this.decoderState = null;
                this.encoderState = null;

                // a failure here is retried on first use (see WithHost) so callers never see it
                WithHost(h => true, false);
                return;
            }

            unsafe
            {
                // initialize the AMBE decoder state
                fixed (byte* state = decoderState)
                    ambe_init_dec((IntPtr)state, (short)mode);

                // initialize the AMBE encoder state
                fixed (byte* state = encoderState)
                    ambe_init_enc((IntPtr)state, (short)mode, 1);
            }
        }

        /// <summary>
        /// Helper to unpack the codeword bytes into codeword bits for use with the AMBE decoder.
        /// </summary>
        /// <remarks><see cref="short"/> output bits array.</remarks>
        /// <param name="codewordBits">Codeword bits.</param>
        /// <param name="codeword">Codeword bytes.</param>
        /// <param name="lengthBytes">Length of codeword in bytes.</param>
        /// <param name="lengthBits">Length of codeword in bits.</param>
        private void unpackBytesToBits(out short[] codewordBits, byte[] codeword, int lengthBytes, int lengthBits)
        {
            codewordBits = new short[this.frameLengthInBits * 2];

            int processed = 0, bitPtr = 0, bytePtr = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                for (int j = 7; -1 < j; j--)
                {
                    if (processed < lengthBits)
                    {
                        codewordBits[bitPtr] = (short)((codeword[bytePtr] >> ((byte)j & 0x1F)) & 1);
                        bitPtr++;
                    }

                    processed++;
                }

                bytePtr++;
            }
        }

        /// <summary>
        /// Helper to unpack the codeword bytes into codeword bits for use with the AMBE decoder.
        /// </summary>
        /// <remarks><see cref="byte"/> output bits array.</remarks>
        /// <param name="codewordBits">Codeword bits.</param>
        /// <param name="codeword">Codeword bytes.</param>
        /// <param name="lengthBytes">Length of codeword in bytes.</param>
        /// <param name="lengthBits">Length of codeword in bits.</param>
        private void unpackBytesToBits(out byte[] codewordBits, byte[] codeword, int lengthBytes, int lengthBits)
        {
            codewordBits = new byte[this.frameLengthInBits * 2];

            int processed = 0, bitPtr = 0, bytePtr = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                for (int j = 7; -1 < j; j--)
                {
                    if (processed < lengthBits)
                    {
                        codewordBits[bitPtr] = (byte)((codeword[bytePtr] >> ((byte)j & 0x1F)) & 1);
                        bitPtr++;
                    }

                    processed++;
                }

                bytePtr++;
            }
        }

        /// <summary>
        /// Decodes the given MBE codewords to PCM samples using the decoder mode.
        /// </summary>
        /// <param name="codeword"></param>
        /// <param name="samples"></param>
        public int decode(byte[] codeword, out short[] samples)
        {
            samples = new short[MBE_SAMPLES_LENGTH];

            if (codeword == null)
                throw new NullReferenceException("codeword");

            // is this a DMR codeword?
            if (codeword.Length > frameLengthInBytes && mode == AmbeMode.HALF_RATE &&
                codeword.Length == 9)
            {
                // use the managed vocoder to retrieve the un-ECC'ed and uninterleaved AMBE bits
                byte[] bits = new byte[49];
                interleaver.Decode(codeword, bits);

                // repack bits into 7-byte array
                packBitsToBytes(bits, out codeword, frameLengthInBytes, frameLengthInBits);
            }

            if (codeword.Length > frameLengthInBytes)
                throw new ArgumentOutOfRangeException($"Codeword length is > {frameLengthInBytes}");

            if (codeword.Length < frameLengthInBytes)
                throw new ArgumentOutOfRangeException($"Codeword length is < {frameLengthInBytes}");

            // unpack codeword from bytes to bits for use with external library
            short[] codewordBits = null;
            unpackBytesToBits(out codewordBits, codeword, frameLengthInBytes, frameLengthInBits);

            if (!inProcess.Value)
            {
                // on helper failure this frame decodes to silence
                samples = WithHost(h => h.Decode(hostId, dcmode, codewordBits), new short[MBE_SAMPLES_LENGTH]);
                return 0;
            }

            short[] n0 = new short[MBE_SAMPLES_LENGTH / 2];
            short[] n1 = new short[MBE_SAMPLES_LENGTH / 2];

            // perform P/Invoke callback and pointer pinning and callback into external library
            unsafe
            {
                fixed (short* c = codewordBits)
                fixed (byte* state = decoderState)
                {
                    IntPtr codewordPtr = (IntPtr)c;

                    // sample segment 1
                    GCHandle pinnedN0 = GCHandle.Alloc(n0, GCHandleType.Pinned);
                    IntPtr n0Ptr = pinnedN0.AddrOfPinnedObject();
                    ambe_voice_dec(n0Ptr, MBE_SAMPLES_LENGTH / 2, codewordPtr, NO_BIT_STEAL, dcmode, 0, (IntPtr)state);
                    pinnedN0.Free();

                    // sample segment 2
                    GCHandle pinnedN1 = GCHandle.Alloc(n1, GCHandleType.Pinned);
                    IntPtr n1Ptr = pinnedN1.AddrOfPinnedObject();
                    ambe_voice_dec(n1Ptr, MBE_SAMPLES_LENGTH / 2, codewordPtr, NO_BIT_STEAL, dcmode, 1, (IntPtr)state);
                    pinnedN1.Free();
                }
            }

            // combine sample segments into contiguous samples
            for (int i = 0; i < MBE_SAMPLES_LENGTH / 2; i++)
                samples[i] = n0[i];
            for (int i = 0; i < MBE_SAMPLES_LENGTH / 2; i++)
                samples[i + (MBE_SAMPLES_LENGTH / 2)] = n1[i];

            return 0;
        }

        /// <summary>
        /// Calls ambe_init_dec() in the external DLL.
        /// </summary>
        /// <param name="state">Buffer containing the decoder state to initialize.</param>
        /// <param name="mode">AMBE mode; FULL (0) or HALF (1).</param>
        [DllImport("AMBE.DLL", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern void ambe_init_dec([Out] IntPtr state, [In] short mode);

        /// <summary>
        /// Calls ambe_get_dec_mode() in the external DLL.
        /// </summary>
        /// <param name="state">Buffer containing the decoder state to initialize.</param>
        [DllImport("AMBE.DLL", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern short ambe_get_dec_mode([In] IntPtr state);

        /// <summary>
        /// Calls ambe_voice_dec() in the external DLL.
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="sampleLength"></param>
        /// <param name="codeword"></param>
        /// <param name="bitSteal"></param>
        /// <param name="cmode"></param>
        /// <param name="n"></param>
        /// <param name="state">Buffer containing the decoder state.</param>
        /// <returns></returns>
        [DllImport("AMBE.DLL", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint ambe_voice_dec([Out] IntPtr samples, [In] short sampleLength, [In] IntPtr codeword, [In] short bitSteal, [In] ushort cmode, [In] short n, [In] IntPtr state);

        /// <summary>
        /// Helper to pack the codeword bits into codeword bytes for use with the AMBE encoder.
        /// </summary>
        /// <remarks><see cref="short"/> input bits array.</remarks>
        /// <param name="codewordBits">Codeword bits.</param>
        /// <param name="codeword">Codeword bytes.</param>
        /// <param name="lengthBytes">Length of codeword in bytes.</param>
        /// <param name="lengthBits">Length of codeword in bits.</param>
        private void packBitsToBytes(short[] codewordBits, out byte[] codeword, int lengthBytes, int lengthBits)
        {
            codeword = new byte[lengthBytes];

            int processed = 0, bitPtr = 0, bytePtr = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                codeword[i] = 0;
                for (int j = 7; -1 < j; j--)
                {
                    if (processed < lengthBits)
                    {
                        codeword[bytePtr] = (byte)(codeword[bytePtr] | (byte)((codewordBits[bitPtr] & 1) << ((byte)j & 0x1F)));
                        bitPtr++;
                    }

                    processed++;
                }

                bytePtr++;
            }
        }

        /// <summary>
        /// Helper to pack the codeword bits into codeword bytes for use with the AMBE encoder.
        /// </summary>
        /// <remarks><see cref="byte"/> input bits array.</remarks>
        /// <param name="codewordBits">Codeword bits.</param>
        /// <param name="codeword">Codeword bytes.</param>
        /// <param name="lengthBytes">Length of codeword in bytes.</param>
        /// <param name="lengthBits">Length of codeword in bits.</param>
        private void packBitsToBytes(byte[] codewordBits, out byte[] codeword, int lengthBytes, int lengthBits)
        {
            codeword = new byte[lengthBytes];

            int processed = 0, bitPtr = 0, bytePtr = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                codeword[i] = 0;
                for (int j = 7; -1 < j; j--)
                {
                    if (processed < lengthBits)
                    {
                        codeword[bytePtr] = (byte)(codeword[bytePtr] | (byte)((codewordBits[bitPtr] & 1) << ((byte)j & 0x1F)));
                        bitPtr++;
                    }

                    processed++;
                }

                bytePtr++;
            }
        }

        /// <summary>
        /// Encodes the given PCM samples using the encoder mode to MBE codewords.
        /// </summary>
        /// <param name="samples"></param>
        /// <param name="codeword"></param>
        /// <param name="encodeDMR"></param>
        public void encode(short[] samples, out byte[] codeword, bool encodeDMR = false)
        {
            codeword = new byte[frameLengthInBytes];

            if (samples == null)
                throw new NullReferenceException("samples");

            if (samples.Length > MBE_SAMPLES_LENGTH)
                throw new ArgumentOutOfRangeException($"Samples length is > {MBE_SAMPLES_LENGTH}");
            if (samples.Length < MBE_SAMPLES_LENGTH)
                throw new ArgumentOutOfRangeException($"Samples length is < {MBE_SAMPLES_LENGTH}");

            short[] codewordBits = new short[this.frameLengthInBits * 2];

            if (!inProcess.Value)
            {
                // on helper failure this frame encodes as all-zero bits
                int bitsLength = codewordBits.Length;
                codewordBits = WithHost(h => h.Encode(hostId, ecmode, ENC_UNK, samples, bitsLength), codewordBits);
            }
            else
            {

            // split samples into 2 segments
            short[] n0 = new short[MBE_SAMPLES_LENGTH / 2];
            for (int i = 0; i < MBE_SAMPLES_LENGTH / 2; i++)
                n0[i] = samples[i];

            short[] n1 = new short[MBE_SAMPLES_LENGTH / 2];
            for (int i = 0; i < MBE_SAMPLES_LENGTH / 2; i++)
                n1[i] = samples[i + (MBE_SAMPLES_LENGTH / 2)];

            // perform P/Invoke callback and pointer pinning and callback into external library
            unsafe
            {
                fixed (short* c = codewordBits)
                fixed (byte* state = encoderState)
                {
                    IntPtr codewordPtr = (IntPtr)c;

                    // sample segment 1
                    GCHandle pinnedN0 = GCHandle.Alloc(n0, GCHandleType.Pinned);
                    IntPtr n0Ptr = pinnedN0.AddrOfPinnedObject();
                    ambe_voice_enc(codewordPtr, NO_BIT_STEAL, n0Ptr, MBE_SAMPLES_LENGTH / 2, ecmode, 0, ENC_UNK, (IntPtr)state);
                    pinnedN0.Free();

                    // sample segment 2
                    GCHandle pinnedN1 = GCHandle.Alloc(n1, GCHandleType.Pinned);
                    IntPtr n1Ptr = pinnedN1.AddrOfPinnedObject();
                    ambe_voice_enc(codewordPtr, NO_BIT_STEAL, n1Ptr, MBE_SAMPLES_LENGTH / 2, ecmode, 1, ENC_UNK, (IntPtr)state);
                    pinnedN1.Free();
                }
            }

            } // in-process

            // is this to be a DMR codeword?
            if (mode == AmbeMode.HALF_RATE && encodeDMR)
            {
                codeword = new byte[FneSystemBase.AMBE_BUF_LEN];

                byte[] bits = new byte[49];
                for (int i = 0; i < 49; i++)
                    bits[i] = (byte)codewordBits[i];

                // use the managed vocoder to create the ECC'ed and interleaved AMBE bits
                interleaver.Encode(bits, codeword);
            }
            else
            {
                // pack codeword from bits to bytes for use with external library
                packBitsToBytes(codewordBits, out codeword, frameLengthInBytes, frameLengthInBits);
            }
        }

        /// <summary>
        /// Starts (or reuses) the AmbeHost.exe helper and creates this vocoder's state in it.
        /// </summary>
        private void ConnectHost()
        {
            long retryAt = Interlocked.Read(ref hostRetryAtTicks);
            if (retryAt != 0 && DateTime.UtcNow.Ticks < retryAt)
                throw new InvalidOperationException($"{AmbeHostClient.HostExeName} is unavailable; retrying later");

            try
            {
                host = AmbeHostClient.GetShared(appDirectory);
                hostId = host.Create(fullRate);
                Interlocked.Exchange(ref hostRetryAtTicks, 0);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // e.g. FileNotFoundException, Win32Exception; normalize so WithHost handles it
                Interlocked.Exchange(ref hostRetryAtTicks, DateTime.UtcNow.AddSeconds(HOST_RETRY_SECONDS).Ticks);
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref hostRetryAtTicks, DateTime.UtcNow.AddSeconds(HOST_RETRY_SECONDS).Ticks);
                throw;
            }
        }

        /// <summary>
        /// Runs a request against the helper. If the helper has died, it is restarted and the request is
        /// retried once with fresh codec state; if that also fails, <paramref name="fallback"/> is returned
        /// so audio paths never throw.
        /// </summary>
        private T WithHost<T>(Func<AmbeHostClient, T> request, T fallback)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (host == null || host.IsBroken)
                        ConnectHost();

                    return request(host);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
                {
                    if (Interlocked.Exchange(ref hostFailureLogged, 1) == 0)
                        Log.WriteWarning($"External AMBE vocoder helper ({AmbeHostClient.HostExeName}) failed: {ex.Message}");

                    host = null;
                }
            }

            return fallback;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="AmbeVocoder"/> class, releasing its helper-side state.
        /// </summary>
        ~AmbeVocoder()
        {
            host?.QueueDestroy(hostId);
        }

        /// <summary>
        /// Calls ambe_init_enc() in the external DLL.
        /// </summary>
        /// <param name="state">Buffer containing the encoder state to initialize.</param>
        /// <param name="mode">AMBE mode; FULL (0) or HALF (1).</param>
        /// <param name="initialize">Flag to initialize encoder state fully, 1 to initialize, 0 to not.</param>
        [DllImport("AMBE.DLL", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern void ambe_init_enc([Out] IntPtr state, [In] short mode, [In] short initialize);

        /// <summary>
        /// Calls ambe_get_enc_mode() in the external DLL.
        /// </summary>
        /// <param name="state">Buffer containing the encoder state to initialize.</param>
        [DllImport("AMBE.DLL", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern short ambe_get_enc_mode([In] IntPtr state);

        /// <summary>
        /// Calls ambe_voice_enc() in the external DLL.
        /// </summary>
        /// <param name="codeword"></param>
        /// <param name="bitSteal"></param>
        /// <param name="samples"></param>
        /// <param name="sampleLength"></param>
        /// <param name="cmode"></param>
        /// <param name="n"></param>
        /// <param name="unk"></param>
        /// <param name="state">Buffer containing the encoder state.</param>
        /// <returns></returns>
        [DllImport("AMBE.DLL", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint ambe_voice_enc([Out] IntPtr codeword, [In] short bitSteal, [In] IntPtr samples, [In] short sampleLength, [In] ushort cmode, [In] short n, [In] short unk, [In] IntPtr state);
    } // public class AmbeVocoder
}
