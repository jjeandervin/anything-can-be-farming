using System.Net;
using System.Text;
using System.Text.Json;
using AnythingCanBeFarming.DataImport;
using Microsoft.AspNetCore.WebUtilities;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class WikidataCrosswalkTests
{
    private const string Results = """
        {"head":{"vars":["item","wfo","rank"]},"results":{"bindings":[
          {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q159657"},
           "wfo":{"type":"literal","value":"wfo-0000514950"},
           "rank":{"type":"uri","value":"http://wikiba.se/ontology#NormalRank"}},
          {"rank":{"type":"uri","value":"http://wikiba.se/ontology#PreferredRank"},
           "wfo":{"type":"literal","xml:lang":"en","value":"wfo-00\"<b>"},
           "item":{"type":"uri","value":"http://www.wikidata.org/entity/Q42"}},
          {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q7"},
           "wfo":{"type":"literal","value":"wfo-0000000007"},
           "rank":{"type":"uri","value":"http://wikiba.se/ontology#DeprecatedRank"}},
          {"item":{"type":"uri","value":"http://www.wikidata.org/entity/L123"},
           "wfo":{"type":"literal","value":"wfo-0000000001"},
           "rank":{"type":"uri","value":"http://wikiba.se/ontology#NormalRank"}},
          {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q8"},
           "wfo":{"type":"literal","value":"wfo-0000000008"},
           "rank":{"type":"uri","value":"http://wikiba.se/ontology#BestRank"}},
          {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q9"},
           "rank":{"type":"uri","value":"http://wikiba.se/ontology#NormalRank"}}
        ]}}
        """;

    [Fact]
    public async Task Parser_extracts_qids_maps_ranks_and_skips_deprecated_and_invalid_bindings()
    {
        var stats = new SparqlParseStats();
        var rows = await SparqlCrosswalkParser.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(Results)), stats, default);
        Assert.Equal([
            new CrosswalkRow("Q159657", "wfo-0000514950", "normal"),
            new CrosswalkRow("Q42", "wfo-00\"<b>", "preferred")
        ], rows);
        Assert.Equal(6, stats.RowsRead);
        Assert.Equal(1, stats.DeprecatedSkipped);
        Assert.Equal(3, stats.WarningCount);
        Assert.Contains(stats.Warnings, x => x.Contains("L123"));
        Assert.Contains(stats.Warnings, x => x.Contains("BestRank"));
    }

    [Fact]
    public async Task Parser_is_resumable_when_the_response_arrives_one_byte_at_a_time()
    {
        var stats = new SparqlParseStats();
        var rows = await SparqlCrosswalkParser.ReadAsync(new TrickleStream(Encoding.UTF8.GetBytes(Results)), stats, default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(6, stats.RowsRead);
    }

    [Fact]
    public async Task Parser_handles_tokens_longer_than_its_buffer()
    {
        var wfo = new string('x', 300_000) + "1";
        using var response = FakeWikidata.SparqlResponse([("Q1", wfo, FakeWikidata.Normal)]);
        var rows = await SparqlCrosswalkParser.ReadAsync(await response.Content.ReadAsStreamAsync(), new SparqlParseStats(), default);
        Assert.Equal(wfo, Assert.Single(rows).WfoId);
    }

    [Theory]
    [InlineData("""{"head":{},"results":{"bindings":[{"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q1"}""")]
    [InlineData("""{"head":{},"results":{"bindings":[]}} SPARQL-QUERY: java.util.concurrent.TimeoutException""")]
    [InlineData("""{"head":{},"results":{"bindings":[]""")]
    [InlineData("")]
    public async Task Parser_rejects_truncated_or_damaged_responses(string body)
    {
        var exception = await Record.ExceptionAsync(() =>
            SparqlCrosswalkParser.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)), new SparqlParseStats(), default));
        Assert.True(exception is JsonException or InvalidDataException, exception?.GetType().Name);
    }

    [Fact]
    public async Task Sparql_request_posts_the_partition_query_with_accept_and_user_agent_headers()
    {
        var fake = new FakeWikidata();
        fake.Add("Q1", "wfo-0000000017", FakeWikidata.Preferred);
        fake.Add("Q2", "wfo-0000000027");
        fake.Add("Q3", "wfo-0000000018");
        using var clients = fake.Clients();
        var rows = await clients.Sparql.QueryAsync(new CrosswalkPartition("7"), new SparqlParseStats(), default);
        Assert.Equal(["Q1", "Q2"], rows.Select(x => x.Qid));
        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://query.wikidata.org/sparql", request.RequestUri!.ToString());
        Assert.Contains(request.Headers.Accept, x => x.MediaType == "application/sparql-results+json");
        Assert.Equal(WikidataOptions.DefaultUserAgent, string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.StartsWith("AnythingCanBeFarming/", WikidataOptions.DefaultUserAgent);
        Assert.EndsWith("(https://github.com/jjeandervin/anything-can-be-farming)", WikidataOptions.DefaultUserAgent);
    }

    [Fact]
    public void Partitions_cover_every_last_digit_and_non_digit_endings_and_subdivide_by_two_characters()
    {
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "non-digit"], CrosswalkPartition.All.Select(x => x.Label));
        Assert.Contains("FILTER(STRENDS(?wfo, \"7\"))", new CrosswalkPartition("7").Query);
        Assert.Contains("FILTER(?rank != wikibase:DeprecatedRank)", new CrosswalkPartition("7").Query);
        Assert.Equal(["07", "17", "27", "37", "47", "57", "67", "77", "87", "97"], new CrosswalkPartition("7").Subdivide().Select(x => x.Label));
        Assert.False(new CrosswalkPartition("07").CanSubdivide);
        Assert.False(new CrosswalkPartition((string?)null).CanSubdivide);
    }

    [Fact]
    public async Task Timed_out_partition_is_subdivided_and_all_rows_are_still_harvested()
    {
        var fake = new FakeWikidata();
        for (var i = 0; i < 100; i++) fake.Add($"Q{i + 1}", $"wfo-{i:D10}");
        fake.Add("Q1000", "wfo-bad");
        fake.HangingPartitions.Add("7");
        using var clients = fake.Clients(sparqlTimeout: TimeSpan.FromMilliseconds(200));
        var accepted = new List<CrosswalkRow>();
        var report = new CrosswalkReport();
        await new WikidataCrosswalkHarvester(clients.Sparql, TextWriter.Null).HarvestAsync(
            (rows, _) => { accepted.AddRange(rows); return Task.CompletedTask; }, report, new SparqlParseStats(), default);

        Assert.Equal(101, accepted.Count);
        Assert.Equal(accepted.Count, accepted.Select(x => x.Qid).Distinct().Count());
        Assert.Contains(accepted, x => x.WfoId == "wfo-bad");
        var failed = Assert.Single(report.Partitions, x => x.Outcome != "Succeeded");
        Assert.Equal("7", failed.Partition);
        Assert.Contains("timed out", failed.Outcome);
        Assert.Equal(["07", "17", "27", "37", "47", "57", "67", "77", "87", "97"],
            report.Partitions.Where(x => x.Partition.Length == 2).Select(x => x.Partition));
        Assert.Equal(10, report.Partitions.Count(x => x.Partition.Length == 2 && x.Outcome == "Succeeded"));
    }

    [Fact]
    public async Task Sub_partition_that_fails_twice_fails_the_harvest()
    {
        var fake = new FakeWikidata();
        fake.Add("Q1", "wfo-0000000037");
        fake.Add("Q2", "wfo-0000000001");
        fake.FailingPartitions.UnionWith(["7", "37"]);
        using var clients = fake.Clients();
        var accepted = new List<CrosswalkRow>();
        var report = new CrosswalkReport();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new WikidataCrosswalkHarvester(clients.Sparql, TextWriter.Null)
            .HarvestAsync((rows, _) => { accepted.AddRange(rows); return Task.CompletedTask; }, report, new SparqlParseStats(), default));
        Assert.Contains("37", error.Message);
        Assert.Equal(2, fake.SparqlPartitions.Count(x => x == "37"));
        Assert.DoesNotContain(accepted, x => x.Qid == "Q1");
        Assert.Equal(2, report.Partitions.Count(x => x.Partition == "37" && x.Outcome.StartsWith("Failed")));
    }

    [Fact]
    public async Task Client_errors_do_not_trigger_subdivision()
    {
        var calls = 0;
        using var clients = Clients((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); });
        var error = await Assert.ThrowsAsync<WikidataHttpException>(() => new WikidataCrosswalkHarvester(clients.Sparql, TextWriter.Null)
            .HarvestAsync((_, _) => Task.CompletedTask, new CrosswalkReport(), new SparqlParseStats(), default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retries_429_and_503_honoring_retry_after_and_defaulting_to_ten_seconds()
    {
        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(3)) } },
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(DateTimeOffset.UtcNow.AddSeconds(30)) } },
            FakeWikidata.SparqlResponse([("Q1", "wfo-0000000001", FakeWikidata.Normal)])
        ]);
        var delays = new List<TimeSpan>();
        using var clients = Clients((_, _) => Task.FromResult(responses.Dequeue()), delays);
        var rows = await clients.Sparql.QueryAsync(new CrosswalkPartition("1"), new SparqlParseStats(), default);
        Assert.Single(rows);
        Assert.Equal(3, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), delays[1]);
        Assert.InRange(delays[2].TotalSeconds, 25, 30);
    }

    [Fact]
    public async Task Gives_up_after_five_retries_and_never_retries_other_client_errors()
    {
        var calls = 0;
        using (var clients = Clients((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)); }))
        {
            var error = await Assert.ThrowsAsync<WikidataHttpException>(() =>
                clients.Sparql.QueryAsync(new CrosswalkPartition("1"), new SparqlParseStats(), default));
            Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
            Assert.DoesNotContain("wikidata.org", error.Message);
        }
        Assert.Equal(6, calls);

        calls = 0;
        using (var clients = Clients((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }))
            await Assert.ThrowsAsync<WikidataHttpException>(() => clients.Sparql.QueryAsync(new CrosswalkPartition("1"), new SparqlParseStats(), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Minimum_interval_is_enforced_between_requests()
    {
        var delays = new List<TimeSpan>();
        var options = new WikidataOptions { SparqlInterval = TimeSpan.FromSeconds(2) };
        using var clients = new WikidataClients(options, new Stub((_, _) => Task.FromResult(FakeWikidata.SparqlResponse([]))),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        for (var i = 0; i < 3; i++) await clients.Sparql.QueryAsync(new CrosswalkPartition("1"), new SparqlParseStats(), default);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, x => Assert.InRange(x.TotalSeconds, 1.5, 2));
    }

    [Fact]
    public async Task Cancellation_is_honored_while_waiting_for_a_response()
    {
        using var cancellation = new CancellationTokenSource();
        using var clients = Clients(async (_, token) => { await cancellation.CancelAsync(); await Task.Delay(Timeout.Infinite, token); return null!; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WikidataCrosswalkHarvester(clients.Sparql, TextWriter.Null).HarvestAsync((_, _) => Task.CompletedTask,
                new CrosswalkReport(), new SparqlParseStats(), cancellation.Token));
    }

    [Fact]
    public async Task Property_check_accepts_expected_datatypes_and_retries_maxlag()
    {
        var fake = new FakeWikidata();
        var maxlag = FakeWikidata.Json(new { error = new { code = "maxlag", info = "Waiting for a database server: 6 seconds lagged." } });
        maxlag.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
        fake.ApiResponses.Enqueue(() => maxlag);
        using var clients = fake.Clients();
        await clients.Api.VerifyPropertiesAsync(default);
        Assert.Equal([TimeSpan.FromSeconds(5)], fake.Delays);
        var query = QueryHelpers.ParseQuery(fake.Requests[^1].RequestUri!.Query);
        Assert.Equal("5", query["maxlag"]);
        Assert.Equal(string.Join('|', WikidataApiClient.ExpectedDatatypes.Keys), query["ids"]);
    }

    [Fact]
    public async Task Property_check_fails_fast_when_a_datatype_changed()
    {
        var fake = new FakeWikidata();
        fake.Datatypes["P7715"] = "string";
        fake.Datatypes.Remove("P18");
        using var clients = fake.Clients();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => clients.Api.VerifyPropertiesAsync(default));
        Assert.Contains("P7715 is string, expected external-id", error.Message);
        Assert.Contains("P18 is missing", error.Message);
    }

    [Fact]
    public async Task Api_errors_other_than_maxlag_fail_without_echoing_untrusted_text()
    {
        var fake = new FakeWikidata();
        fake.ApiResponses.Enqueue(() => FakeWikidata.Json(new { error = new { code = "<script>alert(1)</script>" } }));
        using var clients = fake.Clients();
        var error = await Assert.ThrowsAsync<WikidataHttpException>(() => clients.Api.VerifyPropertiesAsync(default));
        Assert.DoesNotContain("<script>", error.Message);
        Assert.Single(fake.Requests);
    }

    private static WikidataClients Clients(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        List<TimeSpan>? delays = null) =>
        new(new WikidataOptions { SparqlInterval = TimeSpan.Zero, ApiInterval = TimeSpan.Zero }, new Stub(send),
            (delay, _) => { delays?.Add(delay); return Task.CompletedTask; });

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
