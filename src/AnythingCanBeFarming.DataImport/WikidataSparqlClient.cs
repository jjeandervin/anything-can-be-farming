using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using AnythingCanBeFarming.Data;

namespace AnythingCanBeFarming.DataImport;

public sealed record CrosswalkRow(string Qid, string WfoId, string StatementRank);

// Suffix is the required ending of the WFO ID; null selects values that do not end in a digit,
// which no digit partition can match (malformed IDs are still stored raw).
public sealed record CrosswalkPartition(string? Suffix)
{
    public static IReadOnlyList<CrosswalkPartition> All { get; } =
        [.. Enumerable.Range(0, 10).Select(digit => new CrosswalkPartition(digit.ToString())), new CrosswalkPartition((string?)null)];

    public string Label => Suffix ?? "non-digit";
    public bool CanSubdivide => Suffix is { Length: 1 };

    public IEnumerable<CrosswalkPartition> Subdivide() =>
        Enumerable.Range(0, 10).Select(digit => new CrosswalkPartition(digit + Suffix));

    public string Query => $$"""
        SELECT ?item ?wfo ?rank WHERE {
          ?item p:P7715 ?st .
          ?st ps:P7715 ?wfo ;
              wikibase:rank ?rank .
          FILTER(?rank != wikibase:DeprecatedRank)
          {{(Suffix == null ? "FILTER(!REGEX(?wfo, \"[0-9]$\"))" : $"FILTER(STRENDS(?wfo, \"{Suffix}\"))")}}
        }
        """;
}

public sealed class SparqlParseStats
{
    public const int MaximumExamples = 20;
    public long RowsRead { get; set; }
    public long DeprecatedSkipped { get; set; }
    public long WarningCount { get; set; }
    public List<string> Warnings { get; } = [];

    public void Warn(string message)
    {
        WarningCount++;
        if (Warnings.Count < MaximumExamples) Warnings.Add(message);
    }

    public void Add(SparqlParseStats other)
    {
        RowsRead += other.RowsRead;
        DeprecatedSkipped += other.DeprecatedSkipped;
        WarningCount += other.WarningCount;
        Warnings.AddRange(other.Warnings.Take(MaximumExamples - Warnings.Count));
    }
}

public sealed class WikidataSparqlClient(WikidataHttp http)
{
    public const string Path = "sparql";

    public Task<List<CrosswalkRow>> QueryAsync(CrosswalkPartition partition, SparqlParseStats stats, CancellationToken cancellationToken) =>
        http.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Path)
            {
                Content = new FormUrlEncodedContent([new("query", partition.Query)])
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));
            return request;
        }, async (response, token) =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await SparqlCrosswalkParser.ReadAsync(stream, stats, token);
        }, cancellationToken);
}

// Incremental reader for SPARQL JSON results: the response is never held in memory as text or a DOM.
public sealed class SparqlCrosswalkParser
{
    private const string EntityPrefix = "http://www.wikidata.org/entity/";
    private const string RankPrefix = "http://wikiba.se/ontology#";
    private const int MaximumExampleLength = 200;

    private readonly SparqlParseStats stats;
    private readonly List<CrosswalkRow> rows;
    private JsonReaderState readerState;
    private string? rootProperty, resultsProperty, variable, field, item, wfo, rank;
    private bool inBindings, completed;

    private SparqlCrosswalkParser(SparqlParseStats stats, List<CrosswalkRow> rows)
    {
        this.stats = stats;
        this.rows = rows;
    }

    public static async Task<List<CrosswalkRow>> ReadAsync(Stream stream, SparqlParseStats stats, CancellationToken cancellationToken)
    {
        var rows = new List<CrosswalkRow>();
        var parser = new SparqlCrosswalkParser(stats, rows);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, length);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                length += read;
                var consumed = parser.Parse(buffer.AsSpan(0, length), isFinalBlock: read == 0);
                Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
                length -= consumed;
                if (read == 0) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        // The query service reports late timeouts by truncating or appending text to a 200 response.
        if (!parser.completed) throw new InvalidDataException("SPARQL response ended before the result set was complete.");
        return rows;
    }

    private int Parse(ReadOnlySpan<byte> data, bool isFinalBlock)
    {
        var reader = new Utf8JsonReader(data, isFinalBlock, readerState);
        while (reader.Read())
        {
            var depth = reader.CurrentDepth;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when depth == 1:
                    rootProperty = reader.GetString();
                    break;
                case JsonTokenType.PropertyName when depth == 2:
                    resultsProperty = reader.GetString();
                    break;
                case JsonTokenType.StartArray when depth == 2 && rootProperty == "results" && resultsProperty == "bindings":
                    inBindings = true;
                    break;
                case JsonTokenType.EndArray when depth == 2:
                    inBindings = false;
                    break;
                case JsonTokenType.StartObject when inBindings && depth == 3:
                    item = wfo = rank = null;
                    break;
                case JsonTokenType.PropertyName when inBindings && depth == 4:
                    variable = reader.GetString();
                    break;
                case JsonTokenType.PropertyName when inBindings && depth == 5:
                    field = reader.GetString();
                    break;
                case JsonTokenType.String when inBindings && depth == 5 && field == "value":
                    var value = reader.GetString();
                    if (variable == "item") item = value;
                    else if (variable == "wfo") wfo = value;
                    else if (variable == "rank") rank = value;
                    break;
                case JsonTokenType.EndObject when inBindings && depth == 3:
                    Emit();
                    break;
                case JsonTokenType.EndObject when depth == 0:
                    completed = true;
                    break;
            }
        }
        readerState = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    private void Emit()
    {
        stats.RowsRead++;
        if (item == null || wfo == null || rank == null)
        {
            stats.Warn("SPARQL binding without item, wfo, and rank values was skipped.");
            return;
        }
        string statementRank;
        switch (rank)
        {
            case RankPrefix + "PreferredRank": statementRank = "preferred"; break;
            case RankPrefix + "NormalRank": statementRank = "normal"; break;
            case RankPrefix + "DeprecatedRank": stats.DeprecatedSkipped++; return;
            default: stats.Warn($"Unknown statement rank {Example(rank)} was skipped."); return;
        }
        var qid = item.StartsWith(EntityPrefix, StringComparison.Ordinal) ? item[EntityPrefix.Length..] : null;
        if (!WikidataIdentifier.IsQid(qid))
        {
            stats.Warn($"Item {Example(item)} is not a Wikidata entity QID and was skipped.");
            return;
        }
        rows.Add(new CrosswalkRow(qid!, wfo, statementRank));
    }

    private static string Example(string value) =>
        JsonSerializer.Serialize(value.Length > MaximumExampleLength ? value[..MaximumExampleLength] + "…" : value);
}
