using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Shokofin.MediaSegments;

/// <summary>
/// Looks playback markers up in TheIntroDB's v4 API (https://theintrodb.org).
/// It only reads; markers are never submitted. Lookups are spaced to stay
/// under TheIntroDB's rate limit, cached, and shared between concurrent
/// callers. When TheIntroDB asks clients to slow down, every lookup pauses
/// for as long as it asks.
/// </summary>
public class TheIntroDbClient : IDisposable {
    public const string BaseUrl = "https://api.theintrodb.org/v4";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // TheIntroDB allows 30 lookups per 10 seconds without a key and 120 with
    // one. Spacing requests a little below that keeps a library scan from
    // running into its burst limit.
    private static readonly TimeSpan AnonymousInterval = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan KeyInterval = TimeSpan.FromMilliseconds(100);

    // A Cloudflare block is not about one title, so every lookup pauses.
    private static readonly TimeSpan BlockedCooldown = TimeSpan.FromMinutes(5);

    // TheIntroDB reports the daily reset as 0 once the day's budget is spent.
    private static readonly TimeSpan ExhaustedCooldown = TimeSpan.FromHours(1);

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(25);

    // A response with both an intro and credits is kept for a day, anything
    // less briefly so newly contributed markers are found soon.
    private static readonly TimeSpan CompleteTtl = TimeSpan.FromHours(24);

    private static readonly TimeSpan IncompleteTtl = TimeSpan.FromMinutes(15);

    private const int MaxCached = 2048;

    private const int MaxServerErrors = 2;

    private const int MaxResponseBytes = 1 << 20;

    private readonly ILogger<TheIntroDbClient> Logger;

    private readonly HttpClient HttpClient;

    private readonly SemaphoreSlim PaceLock = new(1, 1);

    private DateTime NextSlot = DateTime.MinValue;

    private readonly object CacheLock = new();

    private readonly Dictionary<string, (TheIntroDbMedia? Media, DateTime Expires)> Cache = [];

    private readonly ConcurrentDictionary<string, Lazy<Task<TheIntroDbMedia?>>> InFlight = new();

    private DateTime BlockedUntil = DateTime.MinValue;

    public TheIntroDbClient(ILogger<TheIntroDbClient> logger) {
        Logger = logger;
        HttpClient = new HttpClient { Timeout = RequestTimeout };
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Shokofin", Plugin.Instance?.Version?.ToString() ?? "0.0.0"));
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// How long lookups stay paused because TheIntroDB asked for it, or
    /// <see cref="TimeSpan.Zero"/> when they are not paused.
    /// </summary>
    public TimeSpan PausedFor {
        get {
            lock (CacheLock) {
                var wait = BlockedUntil - DateTime.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Returns TheIntroDB's markers for the target, or null when TheIntroDB has
    /// none. A positive <paramref name="durationMs"/> lets TheIntroDB pick the
    /// release closest to the file's length.
    /// </summary>
    /// <exception cref="TheIntroDbRetryException">TheIntroDB asked clients to wait.</exception>
    public async Task<TheIntroDbMedia?> Lookup(TheIntroDbTarget target, long durationMs, string? apiKey, CancellationToken cancellationToken) {
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var query = new List<string> { $"{target.Param}={Uri.EscapeDataString(target.Id)}" };
        if (target.IsEpisode) {
            query.Add($"season={target.Season.ToString(CultureInfo.InvariantCulture)}");
            query.Add($"episode={target.Episode.ToString(CultureInfo.InvariantCulture)}");
        }
        if (durationMs > 0)
            query.Add($"duration_ms={durationMs.ToString(CultureInfo.InvariantCulture)}");
        query.Add("details=true");
        var key = string.Join('&', query);

        lock (CacheLock) {
            var wait = BlockedUntil - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                throw new TheIntroDbRetryException(wait, $"TheIntroDB lookups are paused for {wait:hh\\:mm\\:ss}.");
            if (Cache.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.Expires)
                return entry.Media;
        }

        // The shared request must not end because the first caller gave up.
        var flight = InFlight.GetOrAdd(key, k => new Lazy<Task<TheIntroDbMedia?>>(() => Task.Run(() => FetchAndStore(k, apiKey))));
        return await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TheIntroDbMedia?> FetchAndStore(string key, string? apiKey) {
        try {
            using var timeout = new CancellationTokenSource(RequestTimeout * 2 + TimeSpan.FromSeconds(10));
            var media = await Fetch($"{BaseUrl}/media?{key}", apiKey, timeout.Token).ConfigureAwait(false);
            lock (CacheLock) {
                Store(key, media);
            }
            return media;
        }
        catch (TheIntroDbRetryException ex) {
            lock (CacheLock) {
                var until = DateTime.UtcNow + ex.RetryAfter;
                if (until > BlockedUntil)
                    BlockedUntil = until;
            }
            Logger.LogWarning("Pausing TheIntroDB lookups for {RetryAfter}: {Message}", ex.RetryAfter, ex.Message);
            throw;
        }
        finally {
            InFlight.TryRemove(key, out _);
        }
    }

    // The caller holds CacheLock.
    private void Store(string key, TheIntroDbMedia? media) {
        var now = DateTime.UtcNow;
        if (Cache.Count >= MaxCached) {
            foreach (var expired in Cache.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToList())
                Cache.Remove(expired);
            if (Cache.Count >= MaxCached)
                Cache.Clear();
        }
        var ttl = media is { Intro.Count: > 0, Credits.Count: > 0 } ? CompleteTtl : IncompleteTtl;
        Cache[key] = (media, now + ttl);
    }

    private async Task<TheIntroDbMedia?> Fetch(string url, string? apiKey, CancellationToken cancellationToken) {
        for (var attempt = 0; ; attempt++) {
            await Pace(apiKey is not null, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (apiKey is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = await ReadBody(response, cancellationToken).ConfigureAwait(false);
            var isJson = IsJson(response.Content.Headers.ContentType?.MediaType);
            switch (response.StatusCode) {
                case HttpStatusCode.OK:
                    return JsonSerializer.Deserialize<TheIntroDbMedia>(body);

                // TheIntroDB answers a title it has nothing for with a JSON 404.
                // Any other 404 means the endpoint moved, which must not read as
                // "no markers".
                case HttpStatusCode.NotFound when isJson:
                    return null;

                case HttpStatusCode.TooManyRequests: {
                    var after = RetryAfter(response);
                    string? code = null;
                    try {
                        using var document = JsonDocument.Parse(body);
                        if (document.RootElement.ValueKind is JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind is JsonValueKind.String)
                            code = codeElement.GetString();
                    }
                    catch (JsonException) { }
                    // Only this title is limited for the day; other lookups go on.
                    if (code is "specific_media_usage_limit_exceeded")
                        throw new HttpRequestException($"TheIntroDB's daily lookup limit for this title was reached; retry after {after}.");
                    throw new TheIntroDbRetryException(after, $"TheIntroDB's rate or usage limit was reached; retry after {after}.");
                }

                case HttpStatusCode.Forbidden when !isJson && IsCloudflare(response):
                    throw new TheIntroDbRetryException(BlockedCooldown, $"Cloudflare blocked the request to TheIntroDB; retry after {BlockedCooldown}.");

                case >= HttpStatusCode.InternalServerError when attempt < MaxServerErrors:
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
                    continue;

                default: {
                    var detail = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512)).Trim();
                    throw new HttpRequestException($"TheIntroDB returned HTTP {(int)response.StatusCode}: {detail}", null, response.StatusCode);
                }
            }
        }
    }

    private static async Task<byte[]> ReadBody(HttpResponseMessage response, CancellationToken cancellationToken) {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new System.IO.MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
            if (memory.Length + read > MaxResponseBytes)
                throw new HttpRequestException("TheIntroDB's response was too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>
    /// Waits for the next request slot.
    /// </summary>
    private async Task Pace(bool hasKey, CancellationToken cancellationToken) {
        var interval = hasKey ? KeyInterval : AnonymousInterval;
        TimeSpan wait;
        await PaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var now = DateTime.UtcNow;
            var slot = NextSlot < now ? now : NextSlot;
            NextSlot = slot + interval;
            wait = slot - now;
        }
        finally {
            PaceLock.Release();
        }
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads how long TheIntroDB asks clients to wait, from Retry-After or, for
    /// a spent budget, its rate and usage headers.
    /// </summary>
    private static TimeSpan RetryAfter(HttpResponseMessage response) {
        var after = TimeSpan.Zero;
        if (response.Headers.RetryAfter is { } retryAfter) {
            if (retryAfter.Delta is { } delta)
                after = delta;
            else if (retryAfter.Date is { } date)
                after = date - DateTimeOffset.UtcNow;
        }
        foreach (var prefix in new[] { "X-UsageLimit-", "X-RateLimit-" }) {
            var hasRemaining = long.TryParse(Header(response, prefix + "Remaining"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining);
            if (hasRemaining && remaining > 0)
                continue;
            var reset = Seconds(Header(response, prefix + "Reset"));
            if (reset <= TimeSpan.Zero && prefix is "X-UsageLimit-" && hasRemaining)
                reset = ExhaustedCooldown;
            if (reset > after)
                after = reset;
        }
        if (after <= TimeSpan.Zero)
            after = TimeSpan.FromSeconds(10);
        return after > MaxRetryAfter ? MaxRetryAfter : after;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static TimeSpan Seconds(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    private static bool IsCloudflare(HttpResponseMessage response)
        => response.Headers.Contains("CF-Ray") ||
            response.Headers.Server.Any(server => server.Product?.Name.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsJson(string? mediaType)
        => mediaType is not null && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    public void Dispose() {
        GC.SuppressFinalize(this);
        HttpClient.Dispose();
        PaceLock.Dispose();
    }
}

/// <summary>
/// Names a title the way TheIntroDB addresses it. For an episode,
/// <see cref="Season"/> and <see cref="Episode"/> are numbered in that
/// provider's own order.
/// </summary>
public record TheIntroDbTarget(string Param, string Id, bool IsEpisode = false, int Season = 0, int Episode = 0);

/// <summary>
/// TheIntroDB asked clients to stop looking anything up for
/// <see cref="RetryAfter"/>.
/// </summary>
public class TheIntroDbRetryException(TimeSpan retryAfter, string message) : Exception(message) {
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// TheIntroDB's answer for one title.
/// </summary>
public class TheIntroDbMedia {
    /// <summary>
    /// The length of the release the timestamps were taken from, when known.
    /// </summary>
    [JsonPropertyName("group_duration_ms")]
    public long? GroupDurationMs { get; set; }

    [JsonPropertyName("intro")]
    public List<TheIntroDbSegment> Intro { get; set; } = [];

    [JsonPropertyName("recap")]
    public List<TheIntroDbSegment> Recap { get; set; } = [];

    [JsonPropertyName("credits")]
    public List<TheIntroDbSegment> Credits { get; set; } = [];

    [JsonPropertyName("preview")]
    public List<TheIntroDbSegment> Preview { get; set; } = [];
}

/// <summary>
/// One occurrence of a marker kind. A null start means the segment starts with
/// the file, a null end that it runs to the end of the release.
/// </summary>
public class TheIntroDbSegment {
    [JsonPropertyName("start_ms")]
    public long? StartMs { get; set; }

    [JsonPropertyName("end_ms")]
    public long? EndMs { get; set; }
}
