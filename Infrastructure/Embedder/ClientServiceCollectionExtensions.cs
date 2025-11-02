using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FaceSearch.Infrastructure.Embedder
{
    public static class ClientServiceCollectionExtensions
    {
        public static IServiceCollection AddEmbedderClient(
            this IServiceCollection services,
            Action<EmbedderOptions>? configure = null)
        {
            // Bind options
            if (configure != null)
                services.Configure(configure);
            services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmbedderOptions>>().Value);

            // Register IHttpClientFactory so EmbedderClient can create its own HttpClient
            services.AddHttpClient();

            // Register EmbedderClient as a normal service, not a typed one
            services.AddSingleton<IEmbedderClient, EmbedderClient>();

            return services;
        }
    }
}
