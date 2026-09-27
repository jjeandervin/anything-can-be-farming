using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnythingCanBeFarming.DataImport;

// Every MediaWiki Action API call (Wikidata and Wikipedia) goes through here so maxlag and API errors are handled consistently.
public static partial class MediaWikiActionApi
{
    // Servers reject very long URLs; a longer query is sent as a form POST, which the Action API accepts for reads.
    public const int MaximumGetQueryLength = 6000;

    public static Task<JsonDocument> GetAsync(WikidataHttp http, string service, string path, string query,
        CancellationToken cancellationToken) =>
        http.SendAsync(() => query.Length <= MaximumGetQueryLength
            ? new HttpRequestMessage(HttpMethod.Get, $"{path}?{query}")
            : new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded") },
            async (response, token) =>
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (!document.RootElement.TryGetProperty("error", out var error)) return document;
                using (document)
                {
                    var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value)
                        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    if (code == "maxlag") throw new WikidataRetryException(null, $"{service} API reported maxlag");
                    throw new WikidataHttpException(null, $"{service} API error {Token(code) ?? "(no code)"}.");
                }
            }, cancellationToken);

    // Error codes and datatypes are untrusted; only echo short, plain tokens.
    public static string? Token(string? value) => value != null && TokenPattern().IsMatch(value) ? value : value == null ? null : "(unrecognized)";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
