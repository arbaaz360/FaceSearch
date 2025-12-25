# FaceSearch

A production-ready facial recognition and image search system that indexes images, detects faces, and enables similarity search using vector embeddings. Built with .NET 8, Python, MongoDB, Qdrant, and React.

## 📋 Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Technical Stack](#technical-stack)
- [System Components](#system-components)
- [Database Structure](#database-structure)
- [API Endpoints](#api-endpoints)
- [Frontend](#frontend)
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
- **Modern UI**: React-based frontend for managing albums, reviews, and searches

### Key Features

- ✅ **Face Detection & Recognition**: Automatic face detection with gender filtering (female-only)
- ✅ **Multi-Modal Search**: Text, image, and face similarity search with thumbnail previews
- ✅ **Identity Management**: Album-based person identification with clustering
- ✅ **Review Workflow**: Staged review system for unresolved faces and album-level reviews
- ✅ **Bulk Processing**: Directory scanning with automatic clustering
- ✅ **Aggregator Detection**: Identifies accounts with multiple different people
- ✅ **Duplicate Album Detection**: Automatic detection of similar albums with merge candidate flagging
- ✅ **Album Merging**: Merge duplicate albums with automatic data migration
- ✅ **Preview Thumbnails**: Base64-encoded JPEG thumbnails for search results and albums
- ✅ **GPU Acceleration**: Supports CUDA, DirectML, and CPU backends
- ✅ **React Frontend**: Modern web UI for all operations

---

## 🏗️ Architecture

### High-Level Architecture

```
┌─────────────────┐
│  React Frontend │  ← Modern Web UI (Port 3000)
│   (Vite + React)│
└────────┬────────┘
         │
┌────────▼────────┐
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
   Images → MongoDB (pending) → Worker → Embedder → Qdrant (vectors) → MongoDB (done) → Album Finalization
   ```

2. **Face Review**:
   ```
   Upload → Detect → Review Collection → Manual Review → Main Collection (with identity)
   ```

3. **Search**:
   ```
   Query → Embedder (vector) → Qdrant (similarity) → MongoDB (metadata) → Generate Previews → Results
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
| **Frontend** | React 18 + Vite | Modern web UI |

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
- Search endpoints (text, image, face) with thumbnail previews
- Face review workflow
- Album management with cluster visualization
- Directory scanning
- Health & diagnostics
- Review system (merge candidates, aggregators)

**Port**: `5240`  
**Swagger UI**: `http://localhost:5240/swagger`

### 2. React Frontend (`frontend/`)

Modern web application providing:
- Albums page with thumbnails and merge candidate management
- Album detail page with top clusters visualization
- Search page with text, image, and face search (with thumbnails)
- Face review page for resolving unresolved faces
- Indexing page for directory seeding
- Diagnostics page for system management
- Reviews page for managing album-level reviews

**Port**: `3000` (development)  
**URL**: `http://localhost:3000`

### 3. .NET Worker (`Workers.Indexer/`)

Background service that:
- Polls MongoDB for pending images
- Generates CLIP and face embeddings (configurable)
- Upserts vectors to Qdrant
- Assigns faces to album clusters
- Triggers album finalization (dominance, aggregator detection, merge candidate detection)

**Runs**: Continuously in background

### 4. Python Embedder (`embedder/`)

FastAPI service providing:
- `/embed/text` - Text to CLIP vector
- `/embed/image` - Image to CLIP vector
- `/embed/face` - Single face embedding
- `/embed/face/multi` - Multiple faces with gender detection

**Port**: `8090`  
**GPU Backends**: DirectML (default), CUDA, CPU

### 5. MongoDB

Document database storing:
- Image metadata and status
- Album identities
- Face clusters
- Review queue (face_reviews)
- Review records (reviews)

**Port**: `27017`  
**Database**: `facesearch`

### 6. Qdrant

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
  existingSuspectedDuplicateAlbumId: string?,
  existingSuspectedDuplicateClusterId: string?,
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
  AbsolutePath: string?,
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
  AlbumB: string?,          // For merge reviews
  ClusterId: string?,
  Similarity: double?,       // For merge reviews
  Ratio: double?,           // For aggregator reviews
  Notes: string?,
  CreatedAt: DateTime
}
```

**Indexes**:
- `ux_pending_review`: Unique partial index on (Type, Status, AlbumId, ClusterId) where Status = "pending"

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
  "mediaType": "image",
  "subjectId": "optional"
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
  "absolutePath": "/path/to/image.jpg",
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
  "suggestedScore": 0.85,
  "path": "/path/to/image.jpg"
}
```

#### 4. `album_dominants`
Album dominance vectors (for aggregator detection and duplicate album detection).

**Payload**:
```json
{
  "albumId": "album-id",
  "clusterId": "cluster-id",
  "ratio": 0.75
}
```

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
Face similarity search with thumbnail previews.

**Request**: `multipart/form-data` with `file` (image)

**Query Parameters**: Same as image search

**Response**:
```json
{
  "results": [
    {
      "imageId": "...",
      "albumId": "...",
      "absolutePath": "...",
      "score": 0.85,
      "previewUrl": "data:image/jpeg;base64,..."  // Thumbnail preview
    }
  ]
}
```

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
List all albums with dominant face previews.

**Query Parameters**:
- `skip` (default: 0)
- `take` (default: 20, max: 100)

**Response**:
```json
{
  "total": 100,
  "items": [
    {
      "albumId": "...",
      "displayName": "...",
      "imageCount": 100,
      "faceImageCount": 85,
      "previewBase64": "data:image/jpeg;base64,...",  // Dominant face thumbnail
      "mergeCandidate": false,
      "duplicateAlbumId": null,
      "suspiciousAggregator": false
    }
  ]
}
```

#### `GET /api/albums/{albumId}`
Get album details.

#### `GET /api/albums/{albumId}/dominant-face`
Get dominant face thumbnail as base64-encoded JPEG.

**Response**:
```json
{
  "albumId": "...",
  "hasDominantFace": true,
  "previewBase64": "data:image/jpeg;base64,...",
  "faceId": "...",
  "imagePath": "..."
}
```

#### `GET /api/albums/{albumId}/clusters`
Get top face clusters for an album with previews.

**Query Parameters**:
- `topK` (default: 10, max: 20)

**Response**:
```json
{
  "albumId": "...",
  "totalClusters": 5,
  "clusters": [
    {
      "clusterId": "...",
      "imageCount": 50,
      "faceCount": 45,
      "isDominant": true,
      "previewBase64": "data:image/jpeg;base64,...",
      "faceId": "...",
      "imagePath": "..."
    }
  ]
}
```

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

#### `POST /api/albums/{albumId}/clear-suspicious`
Clear the suspicious aggregator flag on an album.

---

### Review Endpoints

#### `GET /api/reviews`
List all review records (merge candidates, aggregators).

**Query Parameters**:
- `type` (optional: "AlbumMerge" | "AggregatorAlbum")
- `status` (optional: "pending" | "approved" | "rejected")

#### `POST /_diagnostics/embedder/reviews/update-status/{reviewId}`
Update review status.

**Request**:
```json
{
  "status": "approved" | "rejected" | "pending"
}
```

**Note**: Approving an "AggregatorAlbum" review automatically clears the `SuspiciousAggregator` flag.

#### `POST /_diagnostics/embedder/reviews/merge-albums`
Merge two albums.

**Request**:
```json
{
  "sourceAlbumId": "album-to-merge",
  "targetAlbumId": "target-album"
}
```

**Behavior**:
- Updates all image documents to target album
- Updates all cluster documents to target album
- Updates Qdrant vectors with new albumId
- Deletes source album document

---

### Indexing Endpoints

#### `POST /api/index/seed-directory`
Scan directory and create pending image documents.

**Request**:
```json
{
  "directoryPath": "C:/path/to/images",
  "albumId": "my-album",  // Optional: if not provided, uses directory name
  "deriveAlbumFromLeaf": true,  // Use directory name as albumId
  "includeVideos": false,
  "recursive": true
}
```

**Response**:
```json
{
  "root": "C:/path/to/images",
  "albumId": "my-album",
  "scanned": 1234,
  "matched": 1234,
  "upserts": 1234,
  "succeeded": 1234
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
**⚠️ DESTRUCTIVE**: Delete all Qdrant collections and MongoDB documents, then recreate Qdrant collections.

**Response**:
```json
{
  "success": true,
  "message": "Factory reset completed successfully...",
  "qdrantCollectionsDeleted": ["clip_512", "faces_arcface_512", ...],
  "qdrantCollectionsRecreated": true,
  "mongoCollectionsCleared": {
    "images": 1234,
    "albums": 56,
    ...
  },
  "errors": []
}
```

#### `GET /_diagnostics/embedder/album-status?albumId={albumId}`
Get album processing status (pending, done, error counts).

#### `GET /_diagnostics/embedder/album-errors?albumId={albumId}`
Get error messages for failed images in an album.

#### `POST /_diagnostics/embedder/reset-errors?albumId={albumId}`
Reset error images back to pending for retry.

#### `GET /_diagnostics/embedder/merge-candidates`
List albums flagged as merge candidates.

#### `GET /_diagnostics/embedder/reviews`
List all review records.

#### `POST /_diagnostics/embedder/reviews/create-merge/{albumId}`
Manually create a merge review for an album.

#### `POST /_diagnostics/embedder/reviews/fix-null-ids`
Fix review records with null `_id` values.

#### `POST /_diagnostics/embedder/generate-clip-embeddings?albumId={albumId}`
Generate CLIP embeddings for existing images that are missing them.

**Query Parameters**:
- `albumId` (optional: filter by album)

#### `GET /_diagnostics/embedder/check-file?path={filePath}`
Check if a file exists and get file information.

**Response**:
```json
{
  "path": "C:/path/to/file.jpg",
  "exists": true,
  "size": 123456,
  "lastModified": "2024-01-01T00:00:00Z",
  "error": null
}
```

#### `GET /_diagnostics/embedder/test-preview?path={filePath}`
Test preview generation for a specific file path.

**Response**:
```json
{
  "path": "C:/path/to/file.jpg",
  "fileExists": true,
  "fileSize": 123456,
  "lastModified": "2024-01-01T00:00:00Z",
  "previewBase64": "data:image/jpeg;base64,...",
  "error": null
}
```

#### `POST /_diagnostics/embedder/fix-album-ids`
Fix album IDs for images based on their actual directory path.

**Query Parameters**:
- `oldAlbumId` (optional: filter by old album ID)
- `basePath` (optional: filter by path pattern)

**Response**:
```json
{
  "fixed": 50,
  "qdrantUpdated": 0,
  "errors": []
}
```

**Note**: Extracts album ID from directory path (e.g., `__albumname__` or `__name_x`) and updates MongoDB. Qdrant will be updated on next re-index.

---

## 🎨 Frontend

### React Application

The frontend is a modern React application built with Vite, providing a user-friendly interface for all FaceSearch operations.

**Location**: `frontend/`  
**Development Server**: `http://localhost:3000`  
**Production Build**: Served from `FaceSearch/wwwroot/`

### Pages

1. **Albums** (`/albums`)
   - Browse all albums with thumbnails
   - View merge candidates and suspicious aggregators
   - Merge albums directly from the list
   - Click to view album details

2. **Album Detail** (`/albums/:albumId`)
   - View album information and statistics
   - See top face clusters with previews
   - Manage merge candidates
   - Clear suspicious aggregator flag
   - View and manage reviews

3. **Search** (`/search`)
   - Text search
   - Image similarity search
   - Face search with thumbnail previews
   - Click thumbnails to enlarge
   - View full file paths

4. **Face Review** (`/face-review`)
   - Review unresolved faces
   - Accept or reject faces
   - Assign to albums

5. **Indexing** (`/indexing`)
   - Seed directories for indexing
   - Monitor indexing progress

6. **Diagnostics** (`/diagnostics`)
   - System status
   - Generate CLIP embeddings
   - Factory reset
   - Error management

7. **Reviews** (`/reviews`)
   - View album-level reviews (merge candidates, aggregators)
   - Approve or reject reviews
   - Merge albums from reviews

### Setup

```bash
cd frontend
npm install
npm run dev
```

### Build for Production

```bash
cd frontend
npm run build
```

The built files will be output to `FaceSearch/wwwroot/` and served by the .NET API.

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
   - CLIP embedding → clip_512 (if EnableClip=true)
   - Face embedding → faces_arcface_512 (if EnableFace=true)
   ↓
5. Upserts vectors to Qdrant
   ↓
6. Marks images as "done" in MongoDB
   ↓
7. If album complete → triggers album finalization:
   - Computes dominant subject
   - Detects aggregators
   - Detects merge candidates
   - Creates review records if needed
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
5. User reviews in UI (Face Review page)
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
   - Dominant subject (most common face cluster)
   - Aggregator detection (multiple different people)
   - Merge candidates (similar albums via album_dominants search)
   ↓
4. Updates album document
   ↓
5. Creates review entries for:
   - Suspicious aggregators (AggregatorAlbum review)
   - Merge candidates (AlbumMerge review)
```

### 5. Album Merging Workflow

```
1. User identifies merge candidate (via UI or API)
   ↓
2. POST /_diagnostics/embedder/reviews/merge-albums
   ↓
3. System updates:
   - All image documents: AlbumId → targetAlbumId
   - All cluster documents: AlbumId → targetAlbumId, _id → newId
   - All Qdrant vectors: albumId → targetAlbumId
   ↓
4. Deletes source album document
   ↓
5. Returns merge statistics
```

---

## 🚀 Quick Start

### Prerequisites

- **Docker Desktop** (for MongoDB + Qdrant)
- **.NET 8.0 SDK**
- **Python 3.11**
- **Node.js 18+** (for frontend)
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
5. Start React frontend (if `node_modules` exists, otherwise runs `npm install` first)

**Access Points**:
- **Frontend**: http://localhost:3000
- **API Swagger**: http://localhost:5240/swagger
- **Embedder**: http://localhost:8090/_status
- **Qdrant Dashboard**: http://localhost:6333/dashboard

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

#### Step 5: Start Frontend
```bash
cd frontend
npm install
npm run dev
```

### Stop All Services

```bash
stop-all.bat
```

This stops all Docker containers.

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
    "ReviewFaceCollection": "faces_review_512",
    "DominantCollection": "album_dominants"
  },
  "Mongo": {
    "ConnectionString": "mongodb://127.0.0.1:27017",
    "Database": "facesearch"
  },
  "AlbumFinalizer": {
    "LinkThreshold": 0.45,
    "TopK": 50,
    "AggregatorThreshold": 0.50,
    "SubjectMatchThreshold": 0.74
  }
}
```

**Key Settings**:
- `SubjectMatchThreshold`: Similarity threshold for duplicate album detection (default: 0.74)

### Worker Configuration (`Workers.Indexer/appsettings.json`)

```json
{
  "Indexer": {
    "BatchSize": 256,
    "IntervalSeconds": 1,
    "EnableClip": false,
    "EnableFace": true,
    "Parallelism": 8
  }
}
```

**Key Settings**:
- `EnableClip`: Generate CLIP embeddings during indexing (default: false for faster initial indexing)
- `EnableFace`: Generate face embeddings (default: true)
- `BatchSize`: Number of images processed per batch
- `Parallelism`: Concurrent image processing threads

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
    "albumId": "my-album",
    "deriveAlbumFromLeaf": false
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

# Face search (returns thumbnails)
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

### Example 5: Fix Album IDs

```bash
# Fix album IDs for images with wrong albumId
curl -X POST "http://localhost:5240/_diagnostics/embedder/fix-album-ids?oldAlbumId=__araiya__"
```

### Example 6: Generate CLIP Embeddings

```bash
# Generate CLIP embeddings for existing images
curl -X POST "http://localhost:5240/_diagnostics/embedder/generate-clip-embeddings?albumId=my-album"
```

### Example 7: Merge Albums

```bash
# Merge two albums
curl -X POST http://localhost:5240/_diagnostics/embedder/reviews/merge-albums \
  -H "Content-Type: application/json" \
  -d '{
    "sourceAlbumId": "album-to-merge",
    "targetAlbumId": "target-album"
  }'
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
- Use `POST /_diagnostics/embedder/reset-errors` to retry failed images

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

### Missing Qdrant Collections

**Issue**: Worker fails with "Collection doesn't exist" errors

**Solutions**:
- Collections are auto-created on startup
- Manually create: Run `create-qdrant-collections.ps1`
- Factory reset: `POST /_diagnostics/embedder/factory-reset` (recreates collections)

### Preview Thumbnails Not Showing

**Issue**: Search results or albums show "No preview"

**Solutions**:
- Verify file paths exist: Use `GET /_diagnostics/embedder/check-file?path={path}`
- Test preview generation: Use `GET /_diagnostics/embedder/test-preview?path={path}`
- Check API logs for preview generation errors
- Verify image files are not corrupted or locked
- Check file permissions

### Album IDs Mismatch

**Issue**: Album ID in path doesn't match stored albumId

**Solutions**:
- Use `POST /_diagnostics/embedder/fix-album-ids?oldAlbumId={wrongId}` to fix
- System extracts album ID from directory path automatically
- Qdrant will be updated on next re-index

---

## 📊 Performance Tuning

### Worker Performance

Adjust in `Workers.Indexer/appsettings.json`:
- `BatchSize`: Number of images processed per batch (default: 256)
- `Parallelism`: Concurrent image processing (default: 8)
- `IntervalSeconds`: Polling interval (default: 1)
- `EnableClip`: Disable CLIP during initial indexing for faster processing

### Search Performance

- Use `minScore` to filter low-quality results
- Limit `topK` to reduce response size
- Use `albumId` filter to narrow search scope

### Embedder Performance

- Use GPU backend (DirectML/CUDA) for faster embeddings
- Monitor GPU memory usage
- Adjust batch sizes if memory constrained

### Preview Generation

- Preview generation happens synchronously during search
- Large images may take longer to process
- Consider caching previews if needed

---

## 🔒 Security Considerations

- **Factory Reset**: Protected endpoint - use with caution
- **File Paths**: Ensure proper access controls on image directories
- **API Security**: Add authentication/authorization for production
- **Network**: Consider firewall rules for service ports
- **CORS**: Frontend configured for development; update for production

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

**Last Updated**: December 2024
