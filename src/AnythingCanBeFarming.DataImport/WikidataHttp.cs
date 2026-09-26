using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace AnythingCanBeFarming.DataImport;

public sealed class WikidataOptions
{
    public static string DefaultUserAgent { get; } =
        $"AnythingCanBeFarming/{typeof(WikidataOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (https://github.com/jjeandervin/anything-can-be-farming)";

    public string UserAgent { get; set; } = DefaultUserAgent;
    public Uri SparqlBaseAddress { get; set; } = new("https://query.wikidata.org/");
    public Uri ApiBaseAddress { get; set; } = new("https://www.wikidata.org/");
    public TimeSpan SparqlInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ApiInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    // The query service stops queries at 60 seconds; allow for queueing and transfer on top.
    public TimeSpan SparqlRequestTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan ApiRequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan DefaultRetryAfter { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaximumRetryAfter { get; set; } = TimeSpan.FromMinutes(10);
    public int MaximumRetries { get; set; } = 5;

    public static WikidataOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new WikidataOptions();
        var section = configuration.GetSection("Wikidata");
        if (!string.IsNullOrWhiteSpace(section["UserAgent"])) options.UserAgent = section["UserAgent"]!;
        if (!string.IsNullOrWhiteSpace(section["SparqlBaseAddress"])) options.SparqlBaseAddress = new Uri(section["SparqlBaseAddress"]!);
        if (!string.IsNullOrWhiteSpace(section["ApiBaseAddress"])) options.ApiBaseAddress = new Uri(section["ApiBaseAddress"]!);
        return options;
    }

    public static HttpMessageHandler CreateHandler() =>
        new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
}

// Typed clients over separately throttled HttpClients. Tests pass a stub handler (not disposed here).
public sealed class WikidataClients : IDisposable
{
    private readonly HttpClient sparqlHttp, apiHttp;

    public WikidataClients(WikidataOptions options, HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
    {
        sparqlHttp = new HttpClient(handler ?? WikidataOptions.CreateHandler(), disposeHandler: handler == null)
            { BaseAddress = options.SparqlBaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        apiHttp = new HttpClient(handler ?? WikidataOptions.CreateHandler(), disposeHandler: handler == null)
            { BaseAddress = options.ApiBaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        Sparql = new WikidataSparqlClient(new WikidataHttp(sparqlHttp, options, options.SparqlInterval, options.SparqlRequestTimeout, delay, time));
        Api = new WikidataApiClient(new WikidataHttp(apiHttp, options, options.ApiInterval, options.ApiRequestTimeout, delay, time));
    }

    public WikidataSparqlClient Sparql { get; }
    public WikidataApiClient Api { get; }

    public void Dispose()
    {
        sparqlHttp.Dispose();
        apiHttp.Dispose();
    }
}

// Messages never contain request URLs or response bodies; they end up in source_import.error_message.
public sealed class WikidataHttpException(HttpStatusCode? statusCode, string message, bool isTimeout = false, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public bool IsTimeout { get; } = isTimeout;
}

// Thrown by a response reader when a 200 response still asks the client to back off (Action API maxlag).
public sealed class WikidataRetryException(TimeSpan? retryAfter, string reason) : Exception(reason)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

// One request in flight at a time, a minimum gap between requests, and bounded Retry-After handling.
public sealed class WikidataHttp(HttpClient http, WikidataOptions options, TimeSpan minimumInterval, TimeSpan requestTimeout,
    Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? time = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long? lastCompleted;

    public async Task<T> SendAsync<T>(Func<HttpRequestMessage> createRequest,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readResponse, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await ThrottleAsync(cancellationToken);
                TimeSpan wait;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(requestTimeout);
                    using var request = createRequest();
                    request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
                    try
                    {
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                        {
                            if (attempt >= options.MaximumRetries)
                                throw new WikidataHttpException(response.StatusCode,
                                    $"Wikidata returned HTTP {(int)response.StatusCode} after {options.MaximumRetries} retries.");
                            wait = RetryAfter(response) ?? options.DefaultRetryAfter;
                        }
                        else if (!response.IsSuccessStatusCode)
                            throw new WikidataHttpException(response.StatusCode, $"Wikidata returned HTTP {(int)response.StatusCode}.");
                        else
                        {
                            try { return await readResponse(response, timeout.Token); }
                            catch (WikidataRetryException retry) when (attempt < options.MaximumRetries)
                            {
                                wait = retry.RetryAfter ?? RetryAfter(response) ?? options.DefaultRetryAfter;
                            }
                            catch (WikidataRetryException retry)
                            {
                                throw new WikidataHttpException(null, $"{retry.Message} after {options.MaximumRetries} retries.");
                            }
                        }
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                    {
                        throw new WikidataHttpException(null, "Wikidata request timed out.", isTimeout: true, exception);
                    }
                    catch (HttpRequestException exception)
                    {
                        throw new WikidataHttpException(null, $"Network error contacting Wikidata ({exception.HttpRequestError}).", inner: exception);
                    }
                }
                finally
                {
                    lastCompleted = time.GetTimestamp();
                }
                await delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > options.MaximumRetryAfter ? options.MaximumRetryAfter : wait,
                    cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        if (lastCompleted is not { } last) return;
        var remaining = minimumInterval - time.GetElapsedTime(last);
        if (remaining > TimeSpan.Zero) await delay(remaining, cancellationToken);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - time.GetUtcNow(),
            _ => null
        };
}
