using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace FaceSearch.Infrastructure.Persistence.Mongo
{
    public interface IImageRepository
    {
        Task<List<ImageDocMongo>> PullPendingAsync(int batchSize, CancellationToken ct);
        Task MarkDoneAsync(string id, CancellationToken ct);
        Task MarkErrorAsync(string id, string error, CancellationToken ct);

        // NEW: used by IndexerWorker to flag images that contain >=1 face
        Task SetHasPeopleAsync(string id, bool value, CancellationToken ct);
        Task<long> CountPendingByAlbumAsync(string albumId, CancellationToken ct);
        
        // Reset error images back to pending for retry
        Task<int> ResetErrorsToPendingAsync(string? albumId, CancellationToken ct);
        
        // Get image by ID
        Task<ImageDocMongo?> GetAsync(string id, CancellationToken ct);
    }
}
