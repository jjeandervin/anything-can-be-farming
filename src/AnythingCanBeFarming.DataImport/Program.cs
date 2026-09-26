using Microsoft.Extensions.Configuration;

namespace AnythingCanBeFarming.DataImport;

internal static class Program
{
    private const string Usage = """
        Usage: dotnet run --project src/AnythingCanBeFarming.DataImport -- wfo [backbone|supplemental|all] [--directory <package>] [--file <backbone TSV>] [--version <release>] [--force] [--diagnostics <backbone jsonl>]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata crosswalk [--allow-shrink]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata resolve
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("wfo" or "wikidata") || args.Contains("--help"))
        {
            Console.WriteLine(Usage);
            return args.Contains("--help") ? 0 : 1;
        }
        return args[0] == "wikidata" ? await WikidataAsync(args) : await WfoAsync(args);
    }

    private static async Task<int> WikidataAsync(string[] args)
    {
        try
        {
            var mode = args.Length > 1 ? args[1] : throw new InvalidDataException("Specify a Wikidata command: crosswalk or resolve.");
            if (mode is not ("crosswalk" or "resolve")) throw new InvalidDataException($"Unknown Wikidata command: {mode}");
            var allowShrink = false;
            foreach (var option in args.Skip(2))
            {
                if (option == "--allow-shrink" && mode == "crosswalk") allowShrink = true;
                else throw new InvalidDataException($"Unknown or incomplete option: {option}");
            }
            string? root = null;
            try { root = WfoSource.FindRepositoryRoot(); }
            catch (InvalidOperationException) { }
            var settings = LoadSettings(root);
            var connection = ConnectionString(settings);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            if (mode == "resolve")
            {
                await WikidataLinkResolver.RunAsync(connection, Console.Out, cancellation.Token);
                return 0;
            }
            using var clients = new WikidataClients(WikidataOptions.FromConfiguration(settings));
            await new WikidataCrosswalkImporter(connection, clients.Sparql, clients.Api, Console.Out)
                .ImportAsync(allowShrink, cancellation.Token);
            return 0;
        }
        catch (Exception exception)
        {
            // Never print connection strings, request URLs, or response bodies.
            Console.Error.WriteLine(exception is InvalidOperationException or InvalidDataException
                ? exception.Message : $"Import failed ({exception.GetType().Name}). Check configuration and output.");
            return 1;
        }
    }

    private static IConfiguration LoadSettings(string? root)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder();
        if (root != null)
        {
            var apiDirectory = Path.Combine(root, "src", "AnythingCanBeFarming.Api");
            configuration.SetBasePath(apiDirectory).AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{environment}.json", optional: true);
        }
        if (environment == "Development") configuration.AddUserSecrets("acbf-api-local-development");
        return configuration.AddEnvironmentVariables().Build();
    }

    private static string ConnectionString(IConfiguration settings) => settings.GetConnectionString("acbf")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:acbf using API user secrets or ConnectionStrings__acbf (use the Aspire database endpoint).");

    private static async Task<int> WfoAsync(string[] args)
    {
        try
        {
            string? file = null, version = null, diagnosticPath = null, directory = null;
            var hasMode = args.Length > 1 && !args[1].StartsWith("--");
            var mode = hasMode ? args[1] : "backbone";
            if (mode is not ("backbone" or "supplemental" or "all"))
                throw new InvalidDataException($"Unknown WFO mode: {mode}");
            var force = false;
            for (var index = hasMode ? 2 : 1; index < args.Length; index++)
            {
                var option = args[index];
                if (option == "--force") { force = true; continue; }
                if (option is not ("--file" or "--version" or "--diagnostics" or "--directory") || index + 1 >= args.Length)
                    throw new InvalidDataException($"Unknown or incomplete option: {option}");
                var value = args[++index];
                switch (option)
                {
                    case "--file": file = value; break;
                    case "--version": version = value; break;
                    case "--diagnostics": diagnosticPath = value; break;
                    case "--directory": directory = value; break;
                }
            }
            string? root = null;
            try { root = WfoSource.FindRepositoryRoot(); }
            catch (InvalidOperationException) when (file != null || directory != null) { }
            if (mode == "supplemental" && (file != null || diagnosticPath != null))
                throw new InvalidDataException("Use --directory for supplemental files. --file and --diagnostics apply to backbone imports only.");
            var connection = ConnectionString(LoadSettings(root));
            directory = Path.GetFullPath(directory ?? (root != null ? Path.Combine(root, "data", "imports", "wfo") : Path.GetDirectoryName(Path.GetFullPath(file!))!));
            // Resolve ambiguities before publishing any part of a package.
            var supplemental = mode == "backbone" ? [] : Enum.GetValues<WfoSupplementalKind>()
                .Select(kind => (Kind: kind, Path: WfoSupplementalSource.Discover(directory, kind))).ToArray();
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            if (mode != "supplemental")
            {
                file = Path.GetFullPath(file ?? WfoSource.Discover(directory));
                diagnosticPath ??= Path.Combine(Path.GetDirectoryName(file)!, $"wfo-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
                if (Path.GetFullPath(diagnosticPath).Equals(file, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Diagnostics must not overwrite the source file.");
                await using var diagnosticWriter = new StreamWriter(new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                Console.WriteLine($"Diagnostics: {Path.GetFullPath(diagnosticPath)}");
                await new WfoImporter(connection, Console.Out).ImportAsync(file, WfoSource.Version(file, version), force, diagnosticWriter, cancellation.Token);
            }
            foreach (var entry in supplemental)
                await new WfoSupplementalImporter(connection, Console.Out).ImportAsync(entry.Path, entry.Kind,
                    WfoSupplementalSource.Version(entry.Path, version), force, cancellation.Token);
            // Backbone and deduplication changes move current and accepted taxa under existing Wikidata links.
            Console.WriteLine("Re-resolving Wikidata links against the WFO backbone…");
            await WikidataLinkResolver.RunAsync(connection, Console.Out, cancellation.Token);
            return 0;
        }
        catch (Exception exception)
        {
            // Never print connection strings, raw parser buffers, or PostgreSQL row details.
            Console.Error.WriteLine(exception is InvalidOperationException or InvalidDataException or FileNotFoundException
                ? exception.Message : $"Import failed ({exception.GetType().Name}). Check configuration and diagnostic output.");
            return 1;
        }
    }
}
