// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*/

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace dvmconsole
{
    /// <summary>
    /// Client for AmbeHost.exe, a 32-bit x86 helper process that hosts the external AMBE.DLL
    /// for console builds that cannot load it in-process (x64 and ARM64).
    /// </summary>
    /// <remarks>
    /// One helper process is shared by all <see cref="AmbeVocoder"/> instances. Requests are
    /// serialized over a single named pipe; see tools/AmbeHost/AmbeHost.c for the wire protocol.
    /// </remarks>
    public sealed class AmbeHostClient : IDisposable
    {
        public const string HostExeName = "AmbeHost.exe";

        private const byte OP_PING = 0;
        private const byte OP_CREATE = 1;
        private const byte OP_DESTROY = 2;
        private const byte OP_GET_DEC_MODE = 3;
        private const byte OP_GET_ENC_MODE = 4;
        private const byte OP_DECODE = 5;
        private const byte OP_ENCODE = 6;

        private const int MBE_SAMPLES_LENGTH = 160;
        private const int CONNECT_TIMEOUT_MS = 10000;

        private static readonly object instanceLock = new object();
        private static AmbeHostClient instance;
        private static int generationCounter;

        private readonly object ioLock = new object();
        private readonly ConcurrentQueue<uint> pendingDestroy = new ConcurrentQueue<uint>();
        private readonly Process process;
        private readonly NamedPipeClientStream pipe;
        private volatile bool broken;

        /// <summary>
        /// Gets a number identifying this helper process; ids from one generation are not valid in another.
        /// </summary>
        public int Generation { get; }

        /// <summary>
        /// Gets whether the helper process has failed and must be replaced.
        /// </summary>
        public bool IsBroken => broken;

        /*
        ** Methods
        */

        /// <summary>
        /// Returns true if both AmbeHost.exe and AMBE.DLL are present in the given folder.
        /// </summary>
        public static bool IsHostPresent(string directory)
        {
            return File.Exists(Path.Combine(directory, HostExeName)) && File.Exists(Path.Combine(directory, "AMBE.DLL"));
        }

        /// <summary>
        /// Gets the shared helper client, starting (or restarting) the helper process if needed.
        /// </summary>
        /// <param name="directory">Folder containing AmbeHost.exe and AMBE.DLL.</param>
        public static AmbeHostClient GetShared(string directory)
        {
            lock (instanceLock)
            {
                if (instance == null || instance.IsBroken)
                {
                    instance?.Dispose();
                    instance = null;
                    instance = new AmbeHostClient(directory);
                }

                return instance;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AmbeHostClient"/> class and starts the helper.
        /// </summary>
        /// <param name="directory">Folder containing AmbeHost.exe and AMBE.DLL.</param>
        public AmbeHostClient(string directory)
        {
            Generation = Interlocked.Increment(ref generationCounter);

            string exePath = Path.Combine(directory, HostExeName);
            if (!File.Exists(exePath))
                throw new FileNotFoundException($"{HostExeName} not found", exePath);

            string pipeName = $"dvmconsole-ambe-{Environment.ProcessId}-{Guid.NewGuid():N}";

            ProcessStartInfo psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {HostExeName}");
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);

            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                if (process.HasExited)
                {
                    int code = process.ExitCode;
                    Dispose();
                    throw new InvalidOperationException(code == 2
                        ? $"{HostExeName} could not load AMBE.DLL (exit code 2)"
                        : $"{HostExeName} exited during startup (exit code {code})");
                }

                try
                {
                    pipe.Connect(200);
                    break;
                }
                catch (TimeoutException)
                {
                    if (sw.ElapsedMilliseconds > CONNECT_TIMEOUT_MS)
                    {
                        Dispose();
                        throw new TimeoutException($"Timed out connecting to {HostExeName}");
                    }
                }
            }

            Call(OP_PING, 0, ReadOnlySpan<byte>.Empty, 0);
        }

        /// <summary>
        /// Creates a vocoder instance (decoder + encoder state) in the helper.
        /// </summary>
        /// <param name="fullRate">True for full rate (IMBE), false for half rate (AMBE).</param>
        /// <returns>Instance id.</returns>
        public uint Create(bool fullRate)
        {
            byte[] resp = Call(OP_CREATE, 0, new byte[] { (byte)(fullRate ? 1 : 0) }, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(resp);
        }

        /// <summary>
        /// Queues an instance for release; safe to call from a finalizer.
        /// </summary>
        public void QueueDestroy(uint id)
        {
            if (!broken)
                pendingDestroy.Enqueue(id);
        }

        /// <summary>
        /// Gets the decoder mode of an instance.
        /// </summary>
        public short GetDecoderMode(uint id) => BinaryPrimitives.ReadInt16LittleEndian(Call(OP_GET_DEC_MODE, id, ReadOnlySpan<byte>.Empty, 2));

        /// <summary>
        /// Gets the encoder mode of an instance.
        /// </summary>
        public short GetEncoderMode(uint id) => BinaryPrimitives.ReadInt16LittleEndian(Call(OP_GET_ENC_MODE, id, ReadOnlySpan<byte>.Empty, 2));

        /// <summary>
        /// Decodes one frame of unpacked codeword bits to 160 PCM samples.
        /// </summary>
        public short[] Decode(uint id, ushort dcmode, short[] codewordBits)
        {
            byte[] payload = new byte[4 + codewordBits.Length * 2];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), dcmode);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)codewordBits.Length);
            for (int i = 0; i < codewordBits.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4 + i * 2), codewordBits[i]);

            byte[] resp = Call(OP_DECODE, id, payload, MBE_SAMPLES_LENGTH * 2);

            short[] samples = new short[MBE_SAMPLES_LENGTH];
            for (int i = 0; i < MBE_SAMPLES_LENGTH; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(resp.AsSpan(i * 2));
            return samples;
        }

        /// <summary>
        /// Encodes 160 PCM samples to one frame of unpacked codeword bits.
        /// </summary>
        public short[] Encode(uint id, ushort ecmode, short unk, short[] samples, int codewordBitsLength)
        {
            if (samples.Length != MBE_SAMPLES_LENGTH)
                throw new ArgumentOutOfRangeException(nameof(samples));

            byte[] payload = new byte[6 + MBE_SAMPLES_LENGTH * 2];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), ecmode);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), unk);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)codewordBitsLength);
            for (int i = 0; i < MBE_SAMPLES_LENGTH; i++)
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(6 + i * 2), samples[i]);

            byte[] resp = Call(OP_ENCODE, id, payload, codewordBitsLength * 2);

            short[] bits = new short[codewordBitsLength];
            for (int i = 0; i < codewordBitsLength; i++)
                bits[i] = BinaryPrimitives.ReadInt16LittleEndian(resp.AsSpan(i * 2));
            return bits;
        }

        /// <summary>
        /// Sends one request and reads its response.
        /// </summary>
        private byte[] Call(byte op, uint id, ReadOnlySpan<byte> payload, int expectedLength)
        {
            lock (ioLock)
            {
                if (broken)
                    throw new InvalidOperationException($"{HostExeName} is not running");

                try
                {
                    // release instances whose AmbeVocoder was garbage collected
                    while (op != OP_DESTROY && pendingDestroy.TryDequeue(out uint dead))
                        Exchange(OP_DESTROY, dead, ReadOnlySpan<byte>.Empty, 0);

                    return Exchange(op, id, payload, expectedLength);
                }
                catch (IOException ex)
                {
                    broken = true;
                    throw new InvalidOperationException($"{HostExeName} connection lost", ex);
                }
                catch (ObjectDisposedException ex)
                {
                    broken = true;
                    throw new InvalidOperationException($"{HostExeName} connection lost", ex);
                }
            }
        }

        /// <summary>
        /// Writes a request and reads the response; caller holds <see cref="ioLock"/>.
        /// </summary>
        private byte[] Exchange(byte op, uint id, ReadOnlySpan<byte> payload, int expectedLength)
        {
            byte[] request = new byte[12 + payload.Length];
            request[0] = op;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), id);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), (uint)payload.Length);
            payload.CopyTo(request.AsSpan(12));
            pipe.Write(request, 0, request.Length);
            pipe.Flush();

            byte[] header = new byte[8];
            pipe.ReadExactly(header, 0, header.Length);
            int status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

            byte[] response = new byte[length];
            if (length > 0)
                pipe.ReadExactly(response, 0, length);

            if (status != 0)
                throw new InvalidOperationException($"{HostExeName} returned error {status} for operation {op}");
            if (length != expectedLength)
                throw new InvalidOperationException($"{HostExeName} returned {length} bytes for operation {op}, expected {expectedLength}");

            return response;
        }

        /// <summary>
        /// Stops the helper process.
        /// </summary>
        public void Dispose()
        {
            broken = true;

            try { pipe?.Dispose(); } catch { /* ignore */ }

            try
            {
                if (process != null && !process.HasExited)
                {
                    if (!process.WaitForExit(500))
                        process.Kill();
                }
            }
            catch { /* ignore */ }

            process?.Dispose();
        }
    } // public sealed class AmbeHostClient
}
