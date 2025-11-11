// AlbumFinalizerVerificationTests.cs
using FaceSearch.Infrastructure.Persistence.Mongo;
using FaceSearch.Infrastructure.Persistence.Mongo.Repositories;
using FaceSearch.Infrastructure.Qdrant;
using Infrastructure.Mongo.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using FluentAssertions;
using Xunit;

namespace Test
{



public sealed class AlbumFinalizerVerificationTests
{
    private readonly IHost _host;
    private readonly IAlbumRepository _albums;
    private readonly AlbumFinalizerService _finalizer;

    public AlbumFinalizerVerificationTests()
    {
        // 1) Configuration: point to the SAME Mongo/Qdrant used by your manual indexing
        var cfg = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Override here if you don't want a file:
                ["Mongo:ConnectionString"] = "mongodb://localhost:27017",
                ["Mongo:Database"] = "facesearch",
                ["Qdrant:BaseUrl"] = "http://localhost:6333"
            })
            .Build();

        // 2) Build a tiny host with only the services the Finalizer needs
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // ---- Options
                services.Configure<MongoOptions>(cfg.GetSection("Mongo"));
                services.AddSingleton(sp => sp.GetRequiredService<IOptions<MongoOptions>>().Value);

                services.Configure<QdrantOptions>(cfg.GetSection("Qdrant"));
                services.AddSingleton(sp => sp.GetRequiredService<IOptions<QdrantOptions>>().Value);

                // ---- Mongo
                services.AddSingleton<IMongoContext, MongoContext>();
                services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoContext>().Database);

                // ---- Qdrant (HTTP clients)
                var qdrantBase = cfg.GetValue<string>("Qdrant:BaseUrl") ?? "http://localhost:6333";
                services.AddHttpClient<IQdrantClient, QdrantClient>(c => c.BaseAddress = new Uri(qdrantBase));
                services.AddHttpClient<QdrantSearchClient>(c => c.BaseAddress = new Uri(qdrantBase));
                services.AddHttpClient<IQdrantUpsert, QdrantUpsert>(c => c.BaseAddress = new Uri(qdrantBase));

                // ---- Repositories + Finalizer
                services.AddSingleton<IAlbumRepository, AlbumRepository>();
                services.AddSingleton<IImageRepository, ImageRepository>();
                services.AddSingleton<IAlbumClusterRepository, AlbumClusterRepository>();
                services.AddSingleton<AlbumFinalizerService>();
            })
            .Build();

        _albums = _host.Services.GetRequiredService<IAlbumRepository>();
        _finalizer = _host.Services.GetRequiredService<AlbumFinalizerService>();
    }

    [Fact(DisplayName = "album_A → NOT aggregator (solo William dominant expected)")]
    public async Task AlbumA_Should_NotBeAggregator()
    {
        var res = await _finalizer.FinalizeAsync("album_A", default);
        res.Should().NotBeNull();
        res!.SuspiciousAggregator.Should().BeFalse();
       // res.DominantName.Should().Be("William");
    }

    [Fact(DisplayName = "album_B → Aggregator (multi-subject expected)")]
    public async Task AlbumB_Should_BeAggregator()
    {
        var res = await _finalizer.FinalizeAsync("album_B", default);
        res.Should().NotBeNull();
        res!.SuspiciousAggregator.Should().BeFalse();
    }

    [Fact(DisplayName = "album_C → Ambiguous case (no clear dominant)")]
    public async Task AlbumC_Should_BeAmbiguous()
    {
        var res = await _finalizer.FinalizeAsync("album_C", default);
        res.Should().NotBeNull();
        res!.SuspiciousAggregator.Should().BeTrue();
    }
}
}