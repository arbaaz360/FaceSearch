// File: Infrastructure/Persistence/Mongo/Repositories/AlbumClusterRepository.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Mongo.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FaceSearch.Infrastructure.Persistence.Mongo.Repositories
{
    public sealed class AlbumClusterRepository : IAlbumClusterRepository
    {
        private readonly IMongoCollection<AlbumClusterMongo> _col;

        public AlbumClusterRepository(IMongoContext ctx)
        {
            _col = ctx.AlbumClusters;

            // Optional safety: unique (AlbumId, ClusterId)
            var idx = new CreateIndexModel<AlbumClusterMongo>(
                Builders<AlbumClusterMongo>.IndexKeys
                    .Ascending(x => x.AlbumId)
                    .Ascending(x => x.ClusterId),
                new CreateIndexOptions { Unique = true, Name = "ux_album_cluster" });

            _col.Indexes.CreateOne(idx);
        }

        public Task<List<AlbumClusterMongo>> GetByAlbumAsync(string albumId, CancellationToken ct = default) =>
            _col.Find(x => x.AlbumId == albumId).ToListAsync(ct);

        public async Task UpsertIncrementalAsync(
            string albumId,
            string clusterId,
            string imageId,
            string faceId,
            float[] vec,
            int sampleCap,
            CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            var filter = Builders<AlbumClusterMongo>.Filter.And(
                Builders<AlbumClusterMongo>.Filter.Eq(x => x.AlbumId, albumId),
                Builders<AlbumClusterMongo>.Filter.Eq(x => x.ClusterId, clusterId));

            // ---- 1) Create/append without ever *overwriting* ImageIds
            var upsertBase = Builders<AlbumClusterMongo>.Update
                .SetOnInsert(x => x.Id, $"{albumId}::{clusterId}")
                .SetOnInsert(x => x.AlbumId, albumId)
                .SetOnInsert(x => x.ClusterId, clusterId)
                .SetOnInsert(x => x.CreatedAt, now)
                .SetOnInsert(x => x.ImageIds, new List<string>())   // initialize
                .SetOnInsert(x => x.SampleFaceIds, new List<string>()) // initialize
                .AddToSet(x => x.ImageIds, imageId)
                .AddToSet(x => x.SampleFaceIds, faceId)
                .Inc(x => x.FaceCount, 1)
                .Set(x => x.UpdatedAt, now);

            if (!string.IsNullOrWhiteSpace(faceId))
                upsertBase = upsertBase.AddToSet(x => x.SampleFaceIds, faceId);

            // only push ImageIds when we actually have a real id
            if (!string.IsNullOrWhiteSpace(imageId))
                upsertBase = upsertBase.AddToSet(x => x.ImageIds, imageId);

            await _col.UpdateOneAsync(
                filter,
                upsertBase,
                new UpdateOptions { IsUpsert = true },
                ct);

            // ---- 2) Read small doc & compute centroid incrementally
            var doc = await _col.Find(filter).FirstOrDefaultAsync(ct);

            var currentSamples = doc?.SampleFaceIds ?? new List<string>();
            if (currentSamples.Count > sampleCap)
                currentSamples = currentSamples.Take(sampleCap).ToList();

            //float[] centroid;
            //if (doc?.Centroid512 is null || doc.Centroid512.Length != vec.Length)
            //{
            //    centroid = (float[])vec.Clone();
            //}
            //else
            //{
            //    centroid = (float[])doc.Centroid512.Clone();
            //    var n = Math.Max(1, doc.FaceCount); // FaceCount was just incremented above
            //    for (int i = 0; i < vec.Length; i++)
            //        centroid[i] = centroid[i] + (vec[i] - centroid[i]) / n;
            //}

            // ---- 3) Final server-side materialization of ImageCount (never sets ImageIds!)
            var update2 = new BsonDocumentUpdateDefinition<AlbumClusterMongo>(
                new BsonDocument("$set", new BsonDocument
                {
                    // ImageCount = size(ifNull(ImageIds, []))
                    { "ImageCount", new BsonDocument("$size",
                        new BsonDocument("$ifNull", new BsonArray { "$ImageIds", new BsonArray() })) },
                    //{ "Centroid512", new BsonArray(centroid.Select(f => (BsonValue)f)) },
                    { "SampleFaceIds", new BsonArray(currentSamples) },
                    { "UpdatedAt", now }
                }));

            await _col.UpdateOneAsync(
                filter,
                update2,
                cancellationToken: ct);
        }
    }
}
