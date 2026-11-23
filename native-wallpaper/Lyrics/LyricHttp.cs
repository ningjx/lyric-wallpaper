using System.Net.Http;
using System.Text.Json;

internal sealed class LyricHttp(NetworkOptions options, CancellationToken lifetime, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient client = handler is null ? new() : new(handler);
    private readonly Dictionary<string, TaskCompletionSource<string?>> pending = new(StringComparer.Ordinal);
    private int failures;
    private long fusedUntil;

    public async Task<string?> RequestAsync(string endpoint, Dictionary<string, string> parameters, string referer,
        string key, CancellationToken stop, bool post = false, bool expectJson = false)
    {
        stop.ThrowIfCancellationRequested();
        TaskCompletionSource<string?> result;
        bool send;
        lock (pending)
        {
            if (Environment.TickCount64 < fusedUntil) throw new HttpRequestException("歌词网络请求处于冷却期。");
            send = !pending.TryGetValue(key, out result!);
            if (send) pending.Add(key, result = new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        var timeout = TimeSpan.FromSeconds(ServiceOptions.Seconds(options.Timeout, .05, 60, 5));
        if (send) _ = SendAsync();
        try { return await result.Task.WaitAsync(timeout + TimeSpan.FromSeconds(3), stop); }
        catch (TimeoutException) { return null; }

        async Task SendAsync()
        {
            try
            {
                int retries = Math.Clamp(options.Retries, 0, 10);
                for (int attempt = 0; attempt <= retries; attempt++)
                {
                    lifetime.ThrowIfCancellationRequested();
                    try
                    {
                        string url = post ? endpoint : endpoint + "?" + string.Join("&", parameters.Select(item =>
                            Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));
                        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, url);
                        if (post) request.Content = new FormUrlEncodedContent(parameters);
                        request.Headers.Referrer = new Uri(referer);
                        if (referer == "https://music.163.com/")
                        {
                            request.Headers.TryAddWithoutValidation("User-Agent",
                                "Mozilla/5.0 (Linux; Android 9; PCT-AL10) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.64 HuaweiBrowser/10.0.3.311 Mobile Safari/537.36");
                            if (!post) request.Headers.TryAddWithoutValidation("Cookie", "appver=2.10.6; os=pc;");
                        }
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                        deadline.CancelAfter(timeout);
                        using var response = await client.SendAsync(request, deadline.Token);
                        response.EnsureSuccessStatusCode();
                        string body = await response.Content.ReadAsStringAsync(deadline.Token);
                        if (expectJson) using (JsonDocument.Parse(body)) { }
                        lock (pending) failures = 0;
                        result.TrySetResult(body);
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
                    {
                        lifetime.ThrowIfCancellationRequested();
                        if (attempt < retries)
                            await Task.Delay(TimeSpan.FromSeconds(1 << Math.Min(attempt, 2)), lifetime);
                    }
                }
                lock (pending)
                {
                    if (++failures >= Math.Max(1, options.FuseThreshold))
                    {
                        fusedUntil = Environment.TickCount64 + (long)(1000 *
                            ServiceOptions.Seconds(options.FuseCooldown, 0, 3600, 60));
                        failures = 0;
                    }
                }
                result.TrySetResult(null);
            }
            catch (OperationCanceledException) { result.TrySetCanceled(lifetime); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { lock (pending) pending.Remove(key); }
        }
    }

    public void Dispose() => client.Dispose();
}
