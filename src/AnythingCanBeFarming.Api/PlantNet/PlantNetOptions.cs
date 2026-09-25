namespace AnythingCanBeFarming.Api.PlantNet;

public sealed class PlantNetOptions
{
    public const string SectionName = "PlantNet";

    public string ApiKey { get; set; } = "";
}

public static class PlantNetServiceCollectionExtensions
{
    public static IServiceCollection AddPlantNet(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlantNetOptions>(configuration.GetSection(PlantNetOptions.SectionName));
        services.AddHttpClient<PlantNetClient>(client =>
        {
            client.BaseAddress = new Uri("https://my-api.plantnet.org/");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        })
        // Pl@ntNet requires the private API key in the query string.
        .RemoveAllLoggers();
        return services;
    }
}
