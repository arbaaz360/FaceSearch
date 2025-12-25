# FaceSearch

A production-ready facial recognition and image search system that indexes images, detects faces, and enables similarity search using vector embeddings. Built with .NET 8, Python, MongoDB, and Qdrant.

## 📋 Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Technical Stack](#technical-stack)
- [System Components](#system-components)
- [Database Structure](#database-structure)
- [API Endpoints](#api-endpoints)
- [Workflows](#workflows)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Usage Examples](#usage-examples)
- [Troubleshooting](#troubleshooting)

---

## 🎯 Overview

FaceSearch is a hybrid system that combines:
- **Vector Search**: Fast similarity search using Qdrant vector database
- **Metadata Storage**: Relational data in MongoDB
- **Face Recognition**: InsightFace (ArcFace) for face embeddings
- **Image Search**: OpenCLIP for semantic image/text search
- **Review Workflow**: Manual identity resolution for detected faces

### Key Features

- ✅ **Face Detection & Recognition**: Automatic face detection with gender filtering (female-only)
- ✅ **Multi-Modal Search**: Text, image, and face similarity search
- ✅ **Identity Management**: Album-based person identification with clustering
- ✅ **Review Workflow**: Staged review system for unresolved faces
- ✅ **Bulk Processing**: Directory scanning with automatic clustering
- ✅ **Aggregator Detection**: Identifies accounts with multiple different people
- ✅ **GPU Acceleration**: Supports CUDA, DirectML, and CPU backends

---

## 🏗️ Architecture

### High-Level Architecture

```
┌─────────────────┐
│   .NET API      │  ← REST API (FaceSearch)
│   (Port 5240)   │
└────────┬────────┘
         │
    ┌────┴────┬──────────────────┐
    │         │                  │
┌───▼───┐ ┌──▼────┐      ┌───────▼──────┐
│MongoDB│ │Qdrant │      │Python        │
│       │ │       │      │Embedder      │
│Metadata│ │Vectors│      │(Port 8090)   │
└───┬───┘ └───┬───┘      └───────┬──────┘
    │         │                  │
    └────┬────┴──────────────────┘
         │
┌────────▼────────┐
│ .NET Worker     │  ← Background Indexer
│ (Indexer)       │
└─────────────────┘
```

### Data Flow

1. **Image Indexing**:
   ```
   Images → MongoDB (pending) → Worker → Embedder → Qdrant (vectors) → MongoDB (done)
   ```

2. **Face Review**:
   ```
   Upload → Detect → Review Collection → Manual Review → Main Collection (with identity)
   ```

3. **Search**:
   ```
   Query → Embedder (vector) → Qdrant (similarity) → MongoDB (metadata) → Results
   ```

---

## 🛠️ Technical Stack

### Backend Services

| Component | Technology | Purpose |
|-----------|-----------|---------|
| **API** | .NET 8 (C#) | REST API, business logic |
| **Worker** | .NET 8 (C#) | Background image processing |
| **Embedder** | Python 3.11 + FastAPI | Vector embeddings (CLIP + InsightFace) |
| **Vector DB** | Qdrant v1.10.0 | Similarity search |
| **Metadata DB** | MongoDB 6 | Document storage |

### ML Models

| Model | Purpose | Dimensions | Source |
|-------|---------|------------|--------|
| **OpenCLIP ViT-B/32** | Image/Text embeddings | 512 | OpenAI pretrained |
| **InsightFace buffalo_l** | Face embeddings | 512 | ArcFace architecture |

### Infrastructure

- **Docker Compose**: MongoDB + Qdrant containers
- **GPU Support**: CUDA, DirectML (Windows), CPU fallback
- **ONNX Runtime**: GPU-accelerated face detection

---

## 🧩 System Components

### 1. .NET API (`FaceSearch/`)

Main REST API service providing:
- Search endpoints (text, image, face)
- Face review workflow
- Album management
- Directory scanning
- Health & diagnostics

**Port**: `5240`  
**Swagger UI**: `http://localhost:5240/swagger`

### 2. .NET Worker (`Workers.Indexer/`)

Background service that:
- Polls MongoDB for pending images
- Generates CLIP and face embeddings
- Upserts vectors to Qdrant
- Assigns faces to album clusters
- Triggers album finalization

**Runs**: Continuously in background

### 3. Python Embedder (`embedder/`)

FastAPI service providing:
- `/embed/text` - Text to CLIP vector
- `/embed/image` - Image to CLIP vector
- `/embed/face` - Single face embedding
- `/embed/face/multi` - Multiple faces with gender detection

**Port**: `8090`  
**GPU Backends**: DirectML (default), CUDA, CPU

### 4. MongoDB

Document database storing:
- Image metadata and status
- Album identities
- Face clusters
- Review queue
- Review records

**Port**: `27017`  
**Database**: `facesearch`

### 5. Qdrant

Vector database storing:
- CLIP embeddings (image/text search)
- Face embeddings (face recognition)
- Review embeddings (unresolved faces)
- Album dominance vectors

**Port**: `6333`  
**Distance Metric**: Cosine similarity

---

## 💾 Database Structure

### MongoDB Collections

#### 1. `images`
Tracks image files and indexing status.

```typescript
{
  _id: string,              // SHA256 hash (primary key)
  AlbumId: string,          // Links to albums
  AbsolutePath: string,      // Full file path
  MediaType: string,        // "image" | "video" | "other"
  EmbeddingStatus: string,  // "pending" | "done" | "error"
  CreatedAt: DateTime,
  EmbeddedAt: DateTime?,
  Error: string?,
  SubjectId: string?,
  TakenAt: DateTime?,
  HasPeople: bool,          // Face detected flag
  Tags: string[]?
}
```

**Indexes**:
- `ix_album_created`: (AlbumId, CreatedAt)
- `ix_album_haspeople`: (AlbumId, HasPeople)
- `ix_album_tags`: (AlbumId, Tags)

#### 2. `albums`
Represents a person/identity.

```typescript
{
  _id: string,              // AlbumId (unique person identifier)
  DisplayName: string?,
  InstagramHandle: string?,
  IdentityResolved: bool,
  Tags: string[]?,
  ImageCount: int,
  FaceImageCount: int,
  DominantSubject: {
    ClusterId: string,
    Ratio: double,          // 0-1 dominance ratio
    SampleFaceId: string,
    ImageCount: int
  }?,
  SuspiciousAggregator: bool,
  isSuspectedMergeCandidate: bool,
  UpdatedAt: DateTime
}
```

#### 3. `album_clusters`
Face clusters within albums (multiple people per album).

```typescript
{
  _id: string,              // "{AlbumId}::{ClusterId}"
  AlbumId: string,
  ClusterId: string,        // "cluster::{albumId}::{guid}"
  FaceCount: int,
  ImageCount: int,
  SampleFaceIds: string[],
  ImageIds: string[],
  CreatedAt: DateTime,
  UpdatedAt: DateTime
}
```

**Indexes**:
- `ux_album_cluster`: Unique (AlbumId, ClusterId)
- `ix_album_imagecount`: (AlbumId, ImageCount DESC)

#### 4. `face_reviews`
Unresolved faces awaiting manual review.

```typescript
{
  _id: string,              // Review ID
  AlbumId: string?,       // Assigned when resolved
  SuggestedAlbumId: string?,
  SuggestedScore: double?,
  Gender: string,           // "female" | "male" | "unknown"
  Resolved: bool,
  Accepted: bool,
  Rejected: bool,
  GroupId: string?,         // Groups similar faces
  Members: FaceReviewMember[]?,
  ThumbnailBase64: string?,
  Vector512: float[],       // Face embedding
  Bbox: int[]?,             // [x1, y1, x2, y2]
  CreatedAt: DateTime
}
```

#### 5. `reviews`
Album-level reviews (aggregator detection, merge candidates).

```typescript
{
  _id: string,
  Type: "AggregatorAlbum" | "AlbumMerge",
  Status: "pending" | "approved" | "rejected",
  AlbumId: string?,
  ClusterId: string?,
  CreatedAt: DateTime
}
```

### Qdrant Collections

All collections use **512-dimensional vectors** with **Cosine distance**.

#### 1. `clip_512`
CLIP embeddings for image/text search.

**Payload**:
```json
{
  "imageId": "sha256-hash",
  "albumId": "album-id",
  "absolutePath": "/path/to/image.jpg",
  "takenAt": "2024-01-01T00:00:00Z",
  "mediaType": "image"
}
```

#### 2. `faces_arcface_512`
Resolved face embeddings (known identities).

**Payload**:
```json
{
  "imageId": "sha256-hash",
  "albumId": "album-id",           // KEY: Person identity
  "albumClusterId": "cluster::...",
  "resolved": true,
  "displayName": "Person Name",
  "instagramHandle": "@username"
}
```

#### 3. `faces_review_512`
Unresolved face embeddings (awaiting review).

**Payload**:
```json
{
  "reviewId": "review-id",
  "gender": "female",
  "resolved": false,
  "suggestedAlbumId": "album-id",
  "suggestedScore": 0.85
}
```

#### 4. `album_dominants`
Album dominance vectors (for aggregator detection).

---

## 🔌 API Endpoints

### Search Endpoints

#### `POST /api/search/text`
Text-based semantic search.

**Request**:
```json
{
  "query": "a red dress",
  "topK": 30,
  "minScore": 0.5,
  "albumId": "optional-filter"
}
```

**Response**:
```json
{
  "hits": [
    {
      "imageId": "...",
      "albumId": "...",
      "absolutePath": "...",
      "score": 0.85
    }
  ]
}
```

#### `POST /api/search/image`
Image similarity search.

**Request**: `multipart/form-data` with `file` (image)

**Query Parameters**:
- `topK` (default: 30)
- `albumId` (optional filter)
- `account` (optional filter)
- `tags` (optional array)
- `minScore` (optional)

#### `POST /api/search/face`
Face similarity search.

**Request**: `multipart/form-data` with `file` (image)

**Query Parameters**: Same as image search

---

### Face Review Endpoints

#### `POST /api/faces/review`
Detect faces and create review entries.

**Request**: `multipart/form-data` with `file` (image)

**Query Parameters**:
- `matchThreshold` (default: 0.72)
- `topK` (default: 5)

**Response**:
```json
{
  "faces": [
    {
      "faceId": "review-id",
      "gender": "female",
      "suggestedAlbumId": "album-id",
      "suggestedScore": 0.85,
      "aboveThreshold": true,
      "thumbnailBase64": "data:image/jpeg;base64,...",
      "similarFaces": [...]
    }
  ],
  "threshold": 0.72
}
```

#### `POST /api/faces/{faceId}/resolve`
Accept or reject a face review.

**Request**:
```json
{
  "accept": true,
  "albumId": "optional-album-id",
  "displayName": "Person Name",
  "instagramHandle": "@username"
}
```

**Behavior**:
- If `accept: false`: Marks as rejected, no upsert
- If `accept: true`: Upserts to `faces_arcface_512` with identity
- Auto-resolves similar faces (score >= 0.90)

#### `GET /api/faces/unresolved`
List unresolved faces awaiting review.

**Query Parameters**:
- `take` (default: 100)

#### `POST /api/faces/who`
Quick "who is this?" lookup without persisting.

**Request**: `multipart/form-data` with `file` (image)

**Response**: Best album match + similar unresolved faces

#### `POST /api/faces/scan-directory`
Bulk scan directory for faces.

**Request**:
```json
{
  "directoryPath": "C:/path/to/images",
  "recursive": true,
  "threshold": 0.72,
  "topK": 5,
  "tags": ["aggregator", "promediaimages"]
}
```

**Response**:
```json
{
  "scanId": "scan-id",
  "imagesScanned": 0,
  "facesDetected": 0,
  "reviewsCreated": 0
}
```

#### `GET /api/faces/scan-status/{scanId}`
Get directory scan progress.

---

### Album Endpoints

#### `GET /api/albums`
List all albums.

**Query Parameters**:
- `skip` (default: 0)
- `take` (default: 20, max: 100)

#### `GET /api/albums/{albumId}`
Get album details.

#### `POST /api/albums/{albumId}/identity`
Update album identity or rename.

**Request**:
```json
{
  "newAlbumId": "optional-new-id",
  "displayName": "New Name",
  "instagramHandle": "@newhandle"
}
```

**Note**: Renaming updates MongoDB references but Qdrant vectors keep old `albumId` until reindexed.

#### `POST /api/albums/{albumId}/recompute`
Recompute album dominance and aggregator detection.

#### `POST /api/albums/{albumId}/tags`
Set tags on an album.

**Request**:
```json
{
  "tags": ["tag1", "tag2"]
}
```

---

### Indexing Endpoints

#### `POST /api/index/seed-directory`
Scan directory and create pending image documents.

**Request**:
```json
{
  "directoryPath": "C:/path/to/images",
  "albumId": "my-album",
  "includeVideos": false
}
```

**Response**:
```json
{
  "pathsAdded": 1234,
  "pathsSkipped": 0
}
```

---

### Diagnostics Endpoints

#### `GET /healthz`
Health check endpoint.

#### `GET /_diagnostics/embedder/status`
Embedder service status.

#### `GET /_diagnostics/embedder/selftest`
Embedder self-test (CLIP text/image embedding).

#### `POST /_diagnostics/embedder/factory-reset`
**⚠️ DESTRUCTIVE**: Delete all Qdrant collections and MongoDB documents.

**Response**:
```json
{
  "success": true,
  "message": "Factory reset completed successfully...",
  "qdrantCollectionsDeleted": ["clip_512", "faces_arcface_512", ...],
  "mongoCollectionsCleared": {
    "images": 1234,
    "albums": 56,
    ...
  },
  "errors": []
}
```

---

## 🔄 Workflows

### 1. Image Indexing Workflow

```
1. POST /api/index/seed-directory
   ↓
2. Creates "pending" image docs in MongoDB
   ↓
3. Worker picks up pending images
   ↓
4. Worker calls embedder:
   - CLIP embedding → clip_512
   - Face embedding → faces_arcface_512
   ↓
5. Upserts vectors to Qdrant
   ↓
6. Marks images as "done" in MongoDB
   ↓
7. If album complete → triggers album finalization
```

### 2. Face Review Workflow

```
1. POST /api/faces/review (upload image)
   ↓
2. Detects female faces
   ↓
3. Searches faces_arcface_512 for matches
   ↓
4. Creates review entries:
   - MongoDB: face_reviews
   - Qdrant: faces_review_512
   ↓
5. User reviews in UI (review.html)
   ↓
6. POST /api/faces/{faceId}/resolve
   ↓
7. If accepted:
   - Upserts to faces_arcface_512 with albumId
   - Auto-resolves similar faces (score >= 0.90)
   - Updates MongoDB face_reviews (resolved=true)
```

### 3. Directory Scan Workflow

```
1. POST /api/faces/scan-directory
   ↓
2. Scans directory for images
   ↓
3. For each image:
   - Detects faces
   - Searches for matches
   - If match < threshold → clusters similar faces
   ↓
4. Creates one review per cluster
   ↓
5. Returns scan progress
```

### 4. Album Finalization Workflow

```
1. Worker detects album completion (no pending images)
   ↓
2. Calls AlbumFinalizerService
   ↓
3. Computes:
   - Dominant subject (most common face)
   - Aggregator detection (multiple different people)
   - Merge candidates (similar albums)
   ↓
4. Updates album document
   ↓
5. Creates review entries for suspicious albums
```

---

## 🚀 Quick Start

### Prerequisites

- **Docker Desktop** (for MongoDB + Qdrant)
- **.NET 8.0 SDK**
- **Python 3.11**
- **Windows** (or adjust scripts for Linux/Mac)

### One-Command Startup

Run the startup script:

```bash
start-all.bat
```

This will:
1. Start Docker containers (MongoDB + Qdrant)
2. Start Python embedder service
3. Start .NET API
4. Start .NET Worker

### Manual Startup

#### Step 1: Start Infrastructure
```bash
docker-compose up -d
```

#### Step 2: Start Embedder
```bash
cd embedder
start.bat
```

#### Step 3: Start API
```bash
cd FaceSearch
dotnet run
```

#### Step 4: Start Worker
```bash
cd Workers.Indexer
dotnet run
```

### Verify Services

- **API**: http://localhost:5240/swagger
- **Embedder**: http://localhost:8090/_status
- **Qdrant**: http://localhost:6333/dashboard
- **MongoDB**: http://localhost:27017

---

## ⚙️ Configuration

### API Configuration (`FaceSearch/appsettings.json`)

```json
{
  "Embedder": {
    "BaseUrl": "http://localhost:8090/",
    "TimeoutSeconds": 30,
    "MaxRetries": 3
  },
  "Qdrant": {
    "BaseUrl": "http://localhost:6333/",
    "ClipCollection": "clip_512",
    "FaceCollection": "faces_arcface_512",
    "ReviewFaceCollection": "faces_review_512"
  },
  "Mongo": {
    "ConnectionString": "mongodb://127.0.0.1:27017",
    "Database": "facesearch"
  },
  "AlbumFinalizer": {
    "LinkThreshold": 0.45,
    "TopK": 50,
    "AggregatorThreshold": 0.50
  }
}
```

### Worker Configuration (`Workers.Indexer/appsettings.json`)

```json
{
  "Indexer": {
    "BatchSize": 256,
    "IntervalSeconds": 1,
    "EnableClip": true,
    "EnableFace": true,
    "Parallelism": 8
  }
}
```

### Embedder Configuration (`embedder/start.bat`)

```batch
set CLIP_DEVICE=dml    # Options: dml | cuda | cpu
set PORT=8090
```

### MongoDB Path (Windows)

Update `docker-compose.yml` line 25 if needed:
```yaml
volumes:
  - "C:/data/facesearch-mongo:/data/db"  # Change this path
```

---

## 📖 Usage Examples

### Example 1: Index Images

```bash
# Seed directory
curl -X POST http://localhost:5240/api/index/seed-directory \
  -H "Content-Type: application/json" \
  -d '{
    "directoryPath": "C:/Users/Photos",
    "albumId": "my-album"
  }'

# Worker will automatically process pending images
```

### Example 2: Review Faces

```bash
# Upload image for review
curl -X POST http://localhost:5240/api/faces/review \
  -F "file=@photo.jpg" \
  -F "matchThreshold=0.72"

# Resolve a face
curl -X POST http://localhost:5240/api/faces/{faceId}/resolve \
  -H "Content-Type: application/json" \
  -d '{
    "accept": true,
    "displayName": "Jane Doe",
    "instagramHandle": "@janedoe"
  }'
```

### Example 3: Search

```bash
# Text search
curl -X POST http://localhost:5240/api/search/text \
  -H "Content-Type: application/json" \
  -d '{
    "query": "a person wearing sunglasses",
    "topK": 10
  }'

# Face search
curl -X POST http://localhost:5240/api/search/face \
  -F "file=@query.jpg" \
  -F "topK=20" \
  -F "albumId=my-album"
```

### Example 4: Bulk Directory Scan

```bash
curl -X POST http://localhost:5240/api/faces/scan-directory \
  -H "Content-Type: application/json" \
  -d '{
    "directoryPath": "C:/Users/Downloads",
    "recursive": true,
    "threshold": 0.72,
    "tags": ["aggregator"]
  }'

# Check progress
curl http://localhost:5240/api/faces/scan-status/{scanId}
```

---

## 🐛 Troubleshooting

### Embedder Not Starting

**Issue**: Python service fails to start

**Solutions**:
- Verify Python 3.11 is installed: `py -3.11 --version`
- Check if port 8090 is available
- Review `embedder/embedder.err.log` for errors
- Try CPU mode: Change `CLIP_DEVICE=cpu` in `start.bat`

### Docker Issues

**Issue**: Containers won't start

**Solutions**:
- Ensure Docker Desktop is running
- Check if ports 27017 (MongoDB) or 6333 (Qdrant) are in use
- Verify MongoDB data directory exists: `C:/data/facesearch-mongo`
- Run: `docker-compose logs` to see errors

### Worker Not Processing

**Issue**: Images stuck in "pending" status

**Solutions**:
- Verify embedder is running: `curl http://localhost:8090/_status`
- Check worker logs for errors
- Verify MongoDB connection
- Check if images have valid file paths

### API Connection Errors

**Issue**: API can't connect to services

**Solutions**:
- Verify all services are running
- Check configuration URLs in `appsettings.json`
- Test embedder: `curl http://localhost:8090/_status`
- Test MongoDB: Check Docker container status
- Test Qdrant: Visit http://localhost:6333/dashboard

### GPU Issues

**Issue**: Embedder using CPU instead of GPU

**Solutions**:
- DirectML: Requires Windows with compatible GPU
- CUDA: Requires NVIDIA GPU with CUDA drivers
- Check embedder status: `GET /_diagnostics/embedder/status`
- Fallback to CPU: Set `CLIP_DEVICE=cpu`

---

## 📊 Performance Tuning

### Worker Performance

Adjust in `Workers.Indexer/appsettings.json`:
- `BatchSize`: Number of images processed per batch (default: 256)
- `Parallelism`: Concurrent image processing (default: 8)
- `IntervalSeconds`: Polling interval (default: 1)

### Search Performance

- Use `minScore` to filter low-quality results
- Limit `topK` to reduce response size
- Use `albumId` filter to narrow search scope

### Embedder Performance

- Use GPU backend (DirectML/CUDA) for faster embeddings
- Monitor GPU memory usage
- Adjust batch sizes if memory constrained

---

## 🔒 Security Considerations

- **Factory Reset**: Protected endpoint - use with caution
- **File Paths**: Ensure proper access controls on image directories
- **API Security**: Add authentication/authorization for production
- **Network**: Consider firewall rules for service ports

---

## 📝 License

[Add your license information here]

---

## 🤝 Contributing

[Add contribution guidelines here]

---

## 📧 Support

[Add support contact information here]

---

**Last Updated**: 2024
