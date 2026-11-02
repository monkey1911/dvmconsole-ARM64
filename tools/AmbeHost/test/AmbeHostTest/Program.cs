// SPDX-License-Identifier: AGPL-3.0-only
//
// CI test for AmbeHost.exe + AmbeHostClient using the AMBE.DLL test stand-in (test/stub_ambe.c).
// Usage: AmbeHostTest <folder containing AmbeHost.exe and the stand-in AMBE.dll>

using System.Diagnostics;
using System.Runtime.InteropServices;
using dvmconsole;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
    if (!ok) failures++;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: AmbeHostTest <dir>");
    return 2;
}

string dir = Path.GetFullPath(args[0]);
Console.WriteLine($"Test process architecture: {RuntimeInformation.ProcessArchitecture}");
Check(AmbeHostClient.IsHostPresent(dir), "AmbeHost.exe and AMBE.DLL present");

using (AmbeHostClient client = new AmbeHostClient(dir))
{
    Check(true, "helper started and answered ping");

    uint full = client.Create(true);
    uint half = client.Create(false);
    Check(full != 0 && half != 0 && full != half, $"created instances {full} and {half}");
    Check(client.GetDecoderMode(full) == 0, "full rate decoder mode = 0");
    Check(client.GetEncoderMode(half) == 1, "half rate encoder mode = 1");

    // decode: stub fills half-frame n with (n * 1000 + i + bits[0])
    short[] bits = new short[176];
    bits[0] = 5;
    short[] samples = client.Decode(full, 0, bits);
    bool decodeOk = samples.Length == 160;
    for (int i = 0; i < 80 && decodeOk; i++)
        decodeOk = samples[i] == i + 5 && samples[80 + i] == 1000 + i + 5;
    Check(decodeOk, "decode returns both half-frames in order");

    // encode: stub writes codeword[n] = samples[first sample of half n] + n
    short[] pcm = new short[160];
    pcm[0] = 1234;
    pcm[80] = 4321;
    short[] enc = client.Encode(half, 0x2040, 8192, pcm, 98);
    Check(enc.Length == 98 && enc[0] == 1234 && enc[1] == 4322, "encode passes both half-frames in order");

    // instance release and reuse
    client.QueueDestroy(full);
    uint again = client.Create(true);
    Check(again == full, "destroyed instance slot is reused");

    // concurrency: several threads sharing one helper
    int concurrencyErrors = 0;
    Parallel.For(0, 4, t =>
    {
        uint id = client.Create(t % 2 == 0);
        short[] b = new short[176];
        for (int n = 0; n < 250; n++)
        {
            b[0] = (short)(t * 10 + (n % 7));
            short[] s = client.Decode(id, 0, b);
            if (s[0] != b[0] || s[80] != 1000 + b[0])
                Interlocked.Increment(ref concurrencyErrors);
        }
        client.QueueDestroy(id);
    });
    Check(concurrencyErrors == 0, "1000 decodes across 4 threads, no cross-talk");

    // latency
    Stopwatch sw = Stopwatch.StartNew();
    const int rounds = 2000;
    for (int i = 0; i < rounds; i++)
        client.Decode(again, 0, bits);
    sw.Stop();
    double usPerFrame = sw.Elapsed.TotalMilliseconds * 1000.0 / rounds;
    Console.WriteLine($"Average round trip: {usPerFrame:F0} us per frame (budget 20000 us)");
    Check(usPerFrame < 5000, "round trip well under the 20 ms frame time");

    // helper crash is reported, not hung
    foreach (Process p in Process.GetProcessesByName("AmbeHost"))
        p.Kill();
    Thread.Sleep(300);
    bool threw = false;
    try { client.Decode(again, 0, bits); }
    catch (InvalidOperationException) { threw = true; }
    Check(threw && client.IsBroken, "killed helper is detected as broken");
}

// a fresh client starts a new helper
using (AmbeHostClient restarted = AmbeHostClient.GetShared(dir))
{
    uint id = restarted.Create(true);
    Check(restarted.GetDecoderMode(id) == 0, "new helper starts after a crash");
}

Thread.Sleep(500);
Check(Process.GetProcessesByName("AmbeHost").Length == 0, "helper exits when the client disposes");

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
