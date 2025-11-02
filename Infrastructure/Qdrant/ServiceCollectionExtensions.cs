using Microsoft.Extensions.DependencyInjection;

namespace FaceSearch.Infrastructure.Qdrant;

public static class QdrantServiceCollectionExtensions
{
    public static IServiceCollection AddQdrantClient(this IServiceCollection services, Action<QdrantOptions>? configure = null)
    {
        var opt = new QdrantOptions();
        configure?.Invoke(opt);

        services.AddSingleton(opt);

        services.AddHttpClient<IQdrantClient, QdrantClient>((sp, http) =>
        {
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddHttpClient<IQdrantUpsert, QdrantUpsert>((sp, http) =>
        {
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        return services;
    }
}
