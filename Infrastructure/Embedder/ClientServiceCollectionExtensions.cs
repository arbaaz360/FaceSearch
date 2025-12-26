using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            // Register IHttpClientFactory so clients can create HttpClients
            services.AddHttpClient();

            // Check if multiple URLs are configured - use load-balanced client if so
            services.AddSingleton<IEmbedderClient>(sp =>
            {
                var opt = sp.GetRequiredService<IOptions<EmbedderOptions>>().Value;
                var log = sp.GetRequiredService<ILogger<LoadBalancedEmbedderClient>>();
                var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

                // Use load-balanced client if multiple URLs are configured
                if (opt.BaseUrls != null && opt.BaseUrls.Length > 1)
                {
                    return new LoadBalancedEmbedderClient(httpFactory, sp.GetRequiredService<IOptions<EmbedderOptions>>(), log);
                }
                else
                {
                    // Fallback to single-instance client for backward compatibility
                    var http = httpFactory.CreateClient();
                    return new EmbedderClient(http, opt, sp.GetRequiredService<ILogger<EmbedderClient>>());
                }
            });

            return services;
        }
    }
}
