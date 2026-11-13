using System.Diagnostics;
using System.Net;

namespace WinStatusBar.Core.Metrics;

public enum SpeedTestPhase { Latency, Download, Upload, Done }

public sealed record SpeedTestResult(double? DownloadMbps, double? UploadMbps, double? LatencyMs);

/// <summary>
/// Teste de velocidade contra os servidores públicos de teste da Cloudflare (speed.cloudflare.com). Só dados
/// aleatórios de teste trafegam; nada do PC é enviado. Completo (botão Testar): 4 conexões por ~8 s, descartando o
/// 1º segundo (aquecimento do TCP). Leve (<see cref="Light"/>, teste automático): até 3 s e no máximo 12 MB de
/// download e 4 MB de upload, por isso fica abaixo do real em conexões muito rápidas.
/// </summary>
public sealed class SpeedTest
{
    private const string Down = "https://speed.cloudflare.com/__down?bytes=", Up = "https://speed.cloudflare.com/__up";
    private static readonly HttpClient Http = CreateClient();
    private readonly TimeSpan Duration = TimeSpan.FromSeconds(8), Warmup = TimeSpan.FromSeconds(1);
    private readonly int Poll = 200;
    private readonly long DownCap, UpCap, DownChunk = 50_000_000, UpChunk = 25_000_000;

    public SpeedTest() { }

    private SpeedTest(TimeSpan duration, TimeSpan warmup, long downCap, long upCap)
    {
        Duration = duration; Warmup = warmup; Poll = 50;
        DownCap = downCap; UpCap = upCap; DownChunk = downCap / 4; UpChunk = upCap / 4;
    }

    public static SpeedTest Light() => new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(0.5), 12_000_000, 4_000_000);

    /// <summary>Fase e valor parcial (Mbps ou ms), chamado várias vezes por segundo fora da thread de UI.</summary>
    public event Action<SpeedTestPhase, double>? Progress;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(1) };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinStatusBar-SpeedTest/1.0");
        return client;
    }

    public async Task<SpeedTestResult> RunAsync(CancellationToken ct)
    {
        double? latency = await LatencyAsync(ct);
        double? down = await MeasureAsync(SpeedTestPhase.Download, DownloadWorker, DownCap, ct);
        double? up = await MeasureAsync(SpeedTestPhase.Upload, UploadWorker, UpCap, ct);
        Progress?.Invoke(SpeedTestPhase.Done, 0);
        return new SpeedTestResult(down, up, latency);
    }

    private async Task<double?> LatencyAsync(CancellationToken ct)
    {
        var samples = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var r = await Http.GetAsync(Down + "0", HttpCompletionOption.ResponseHeadersRead, ct);
                if (!r.IsSuccessStatusCode) continue;
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (i > 0) samples.Add(sw.Elapsed.TotalMilliseconds); // a 1ª inclui DNS/TLS
            if (samples.Count > 0) Progress?.Invoke(SpeedTestPhase.Latency, samples.Min());
        }
        return samples.Count == 0 ? null : samples.Order().ElementAt(samples.Count / 2);
    }

    private delegate Task Worker(Counter counter, CancellationToken ct);
    private sealed class Counter { public long Bytes; }

    private async Task<double?> MeasureAsync(SpeedTestPhase phase, Worker worker, long cap, CancellationToken ct)
    {
        var counter = new Counter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, 4).Select(_ => worker(counter, stop.Token)).ToArray();
        long warmBytes = -1; double warmTime = 0, last = 0;
        while (sw.Elapsed < Duration)
        {
            await Task.Delay(Poll, ct);
            long bytes = Interlocked.Read(ref counter.Bytes);
            double now = sw.Elapsed.TotalSeconds;
            bool capped = cap > 0 && bytes >= cap;
            // Com teto, o aquecimento também termina ao passar de 1/5 do teto (conexões rápidas chegam ao teto antes de 0,5 s).
            if (warmBytes < 0 && !capped && (sw.Elapsed >= Warmup || (cap > 0 && bytes >= cap / 5))) { warmBytes = bytes; warmTime = now; continue; }
            if (warmBytes < 0 && capped) { last = bytes * 8 / now / 1e6; Progress?.Invoke(phase, last); break; }
            if (warmBytes >= 0)
            {
                double secs = now - warmTime;
                if (secs > (capped ? 0.02 : 0.1)) { last = (bytes - warmBytes) * 8 / secs / 1e6; Progress?.Invoke(phase, last); }
            }
            if (capped || workers.All(w => w.IsCompleted)) break;
        }
        stop.Cancel();
        try { await Task.WhenAll(workers); } catch { }
        ct.ThrowIfCancellationRequested();
        return last > 0 ? last : null;
    }

    private async Task DownloadWorker(Counter counter, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var r = await Http.GetAsync(Down + DownChunk, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!r.IsSuccessStatusCode) return;
                await using var s = await r.Content.ReadAsStreamAsync(ct);
                int n;
                while ((n = await s.ReadAsync(buffer, ct)) > 0) Interlocked.Add(ref counter.Bytes, n);
            }
            catch { return; }
        }
    }

    private async Task UploadWorker(Counter counter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var content = new RandomContent(UpChunk, counter);
                using var r = await Http.PostAsync(Up, content, ct);
                if (!r.IsSuccessStatusCode) return;
            }
            catch { return; }
        }
    }

    /// <summary>Corpo de upload gerado em blocos (sem alocar MB de uma vez), contando o que foi enviado.</summary>
    private sealed class RandomContent(long length, Counter counter) : HttpContent
    {
        private static readonly byte[] Block = CreateBlock();
        private static byte[] CreateBlock() { var b = new byte[64 * 1024]; Random.Shared.NextBytes(b); return b; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            long left = length;
            while (left > 0)
            {
                int n = (int)Math.Min(Block.Length, left);
                await stream.WriteAsync(Block.AsMemory(0, n), ct);
                Interlocked.Add(ref counter.Bytes, n);
                left -= n;
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long l) { l = length; return true; }
    }
}
