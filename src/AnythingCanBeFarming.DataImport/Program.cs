using Microsoft.Extensions.Configuration;

namespace AnythingCanBeFarming.DataImport;

internal static class Program
{
    private const string Usage = """
        Usage: dotnet run --project src/AnythingCanBeFarming.DataImport -- wfo [backbone|supplemental|all] [--directory <package>] [--file <backbone TSV>] [--version <release>] [--force] [--diagnostics <backbone jsonl>]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata crosswalk [--allow-shrink]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata details [--full] [--limit N]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata all [--allow-shrink] [--full] [--limit N]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata resolve
               dotnet run --project src/AnythingCanBeFarming.DataImport -- usda [--directory <archive>] [--force] [--version 8] [--zenodo-record 18945513] [--diagnostics <jsonl>]
               dotnet run --project src/AnythingCanBeFarming.DataImport -- usda link
               dotnet run --project src/AnythingCanBeFarming.DataImport -- usda verify [--sample 20] [--symbols ACSA3,COFL2,...] [--seed 42]
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("wfo" or "wikidata" or "usda") || args.Contains("--help"))
        {
            Console.WriteLine(Usage);
            return args.Contains("--help") ? 0 : 1;
        }
        return args[0] switch
        {
            "wikidata" => await WikidataAsync(args),
            "usda" => await UsdaAsync(args),
            _ => await WfoAsync(args)
        };
    }

    private static async Task<int> UsdaAsync(string[] args)
    {
        try
        {
            var hasMode = args.Length > 1 && !args[1].StartsWith("--");
            var mode = hasMode ? args[1] : "import";
            if (mode is not ("import" or "link" or "verify")) throw new InvalidDataException($"Unknown USDA command: {mode}");
            string? directory = null, diagnosticPath = null, symbols = null;
            string version = "8", zenodoRecord = "18945513";
            var (force, sample, seed) = (false, 20, 42);
            for (var index = hasMode ? 2 : 1; index < args.Length; index++)
            {
                var option = args[index];
                if (option == "--force" && mode == "import") { force = true; continue; }
                var allowed = mode switch
                {
                    "import" => new[] { "--directory", "--version", "--zenodo-record", "--diagnostics" },
                    "verify" => ["--sample", "--symbols", "--seed"],
                    _ => []
                };
                if (!allowed.Contains(option) || index + 1 >= args.Length)
                    throw new InvalidDataException($"Unknown or incomplete option: {option}");
                var value = args[++index];
                switch (option)
                {
                    case "--directory": directory = value; break;
                    case "--version": version = value; break;
                    case "--zenodo-record": zenodoRecord = value; break;
                    case "--diagnostics": diagnosticPath = value; break;
                    case "--symbols": symbols = value; break;
                    case "--sample":
                        sample = int.TryParse(value, out var size) && size >= 0 ? size : throw new InvalidDataException("--sample must be zero or a positive number.");
                        break;
                    case "--seed":
                        seed = int.TryParse(value, out var number) ? number : throw new InvalidDataException("--seed must be a whole number.");
                        break;
                }
            }
            var root = WfoSource.FindRepositoryRoot();
            var settings = LoadSettings(root);
            var connection = ConnectionString(settings);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            if (mode == "link")
            {
                await UsdaLinker.RunAsync(connection, Console.Out, cancellation.Token);
                return 0;
            }
            if (mode == "verify")
            {
                using var client = new UsdaPlantsClients(UsdaPlantsOptions.FromConfiguration(settings));
                var verification = await new UsdaVerifier(connection, client.Plants, Console.Out).VerifyAsync(
                    new UsdaVerifyOptions(sample, seed, symbols?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    cancellation.Token);
                return verification.Passed ? 0 : 2;
            }
            var archive = UsdaArchive.Discover(Path.GetFullPath(directory ?? Path.Combine(root, "data", "imports", "usda")));
            diagnosticPath ??= Path.Combine(archive, $"usda-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            await using var diagnosticWriter = new StreamWriter(new FileStream(diagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Console.WriteLine($"Diagnostics: {Path.GetFullPath(diagnosticPath)}");
            await new UsdaImporter(connection, UsdaSeeds.DefaultDirectory(root), Console.Out).ImportAsync(archive,
                new UsdaImportOptions(force, version, zenodoRecord), diagnosticWriter, cancellation.Token);
            return 0;
        }
        catch (Exception exception)
        {
            // Never print connection strings, request URLs, or response bodies.
            Console.Error.WriteLine(exception is InvalidOperationException or InvalidDataException or FileNotFoundException
                ? exception.Message : $"Import failed ({exception.GetType().Name}). Check configuration and diagnostic output.");
            return 1;
        }
    }

    private static async Task<int> WikidataAsync(string[] args)
    {
        try
        {
            var mode = args.Length > 1 ? args[1] : throw new InvalidDataException("Specify a Wikidata command: crosswalk, details, all, or resolve.");
            if (mode is not ("crosswalk" or "details" or "all" or "resolve")) throw new InvalidDataException($"Unknown Wikidata command: {mode}");
            var (allowShrink, full) = (false, false);
            int? limit = null;
            for (var index = 2; index < args.Length; index++)
            {
                var option = args[index];
                if (option == "--allow-shrink" && mode is ("crosswalk" or "all")) allowShrink = true;
                else if (option == "--full" && mode is ("details" or "all")) full = true;
                else if (option == "--limit" && mode is ("details" or "all") && index + 1 < args.Length)
                    limit = int.TryParse(args[++index], out var value) && value > 0 ? value
                        : throw new InvalidDataException("--limit must be a positive number.");
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
            if (mode is "crosswalk" or "all")
                await new WikidataCrosswalkImporter(connection, clients.Sparql, clients.Api, Console.Out)
                    .ImportAsync(allowShrink, cancellation.Token);
            if (mode is "details" or "all")
                await new WikidataDetailsImporter(connection, clients.Api, Console.Out).ImportAsync(full, limit, cancellation.Token);
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
            // USDA symbols link through those Wikidata links or by name, so they follow the backbone too.
            Console.WriteLine("Relinking USDA symbols to WFO taxa…");
            await UsdaLinker.RunAsync(connection, Console.Out, cancellation.Token);
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
