using Microsoft.AspNetCore.Http.Extensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "ProdCluster";
        options.ServiceId = "UrlShortener";
    })
    .Configure<SiloOptions>(options =>
    {
        options.SiloName = "ProdSilo";
    })
    .UseAzureStorageClustering(options =>
    {
        options.ConfigureTableServiceClient(builder.Configuration.GetValue<string>("STORAGE_CONNECTION_STRING"));
    })
    .AddApplicationInsightsTelemetryConsumer(builder.Configuration.GetValue<string>("INSTRUMENTATION_KEY"))
    .AddAzureTableGrainStorage(
        name: "urls",
        configureOptions: options =>
        {
            options.UseJson = true;
            options.ConfigureTableServiceClient(builder.Configuration.GetValue<string>("STORAGE_CONNECTION_STRING"));
        });
});

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddLogging();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var grainFactory = app.Services.GetRequiredService<IGrainFactory>();

app.MapGet("/shorten/{*path}", async (IGrainFactory grains, HttpRequest request, string path) =>
{
    var shortenedRouteSegment = Guid.NewGuid().GetHashCode().ToString("X");
    var shortenerGrain = grains.GetGrain<IUrlShortenerGrain>(shortenedRouteSegment);
    await shortenerGrain.SetUrl(shortenedRouteSegment, path);
    var resultBuilder = new UriBuilder(request.GetEncodedUrl())
    {
        Path = $"/go/{shortenedRouteSegment}"
    };

    return Results.Ok(resultBuilder.Uri);
});

app.MapGet("/go/{shortenedRouteSegment}", async (IGrainFactory grains, string shortenedRouteSegment) =>
{
    var shortenerGrain = grains.GetGrain<IUrlShortenerGrain>(shortenedRouteSegment);
    var url = await shortenerGrain.GetUrl();

    return Results.Redirect(url);
});

app.Run();

public interface IUrlShortenerGrain : IGrainWithStringKey
{
    Task SetUrl(string shortenedRouteSegment, string fullUrl);
    Task<string> GetUrl();
}

public class UrlShortenerGrain : Grain, IUrlShortenerGrain
{
    private IPersistentState<KeyValuePair<string, string>> _cache;
    private ILogger<UrlShortenerGrain> _logger;

    public UrlShortenerGrain(
        [PersistentState(
            stateName: "url",
            storageName: "urls")] IPersistentState<KeyValuePair<string, string>> state,
        ILogger<UrlShortenerGrain> logger)
    {
        _cache = state;
        _logger = logger;
    }

    public Task<string> GetUrl()
    {
        _logger.LogInformation($"Retrieving URL: {_cache.State.Value}");
        return Task.FromResult(_cache.State.Value);
    }

    public async Task SetUrl(string shortenedRouteSegment, string fullUrl)
    {
        _cache.State = new KeyValuePair<string, string>(shortenedRouteSegment, fullUrl);
        _logger.LogInformation($"Saving URL: {fullUrl} to {shortenedRouteSegment}");
        await _cache.WriteStateAsync();
    }
}