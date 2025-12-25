# Instagram Data Ingestion Pipeline - Analysis & Design

## Overview

This document analyzes the Instagram data structure and maps it to the FaceSearch system, similar to how directory indexing works.

## Current Directory Indexing Flow

### Directory Seeding Process:
1. **Input**: Directory path (e.g., `C:/Photos/__personname__/`)
2. **Extraction**:
   - Scans files in directory
   - Computes SHA256 hash of file content → `Id`
   - Extracts directory name → `AlbumId` (e.g., `__personname__`)
   - Full file path → `AbsolutePath`
   - File extension → `MediaType` ("image" | "video" | "other")
   - Current timestamp → `CreatedAt`
3. **Output**: Creates `ImageDocMongo` documents with `EmbeddingStatus = "pending"`
4. **Worker**: Picks up pending images, generates embeddings, stores in Qdrant

### ImageDocMongo Structure:
```csharp
{
    Id: string,                    // SHA256 hash of file content
    AlbumId: string,               // Person/identity identifier (from directory name)
    AbsolutePath: string,          // Full file system path
    MediaType: string,             // "image" | "video" | "other"
    EmbeddingStatus: string,       // "pending" | "done" | "error"
    CreatedAt: DateTime,
    EmbeddedAt: DateTime?,
    Error: string?,
    SubjectId: string?,
    TakenAt: DateTime?,            // EXIF date or file timestamp
    HasPeople: bool,               // Set by worker if face detected
    Tags: List<string>?            // Optional tags
}
```

---

## Instagram Data Structure Analysis

### Expected Collections Structure:

#### 1. `followings` Collection
**Purpose**: Stores accounts that a target account follows

**Expected Schema**:
```json
{
  "_id": ObjectId,
  "target_username": "viralbhayani",        // Account from which we fetched followings
  "following_username": "acharyavinodkumar", // Account being followed
  "fetched_at": ISODate,                     // When this was fetched
  "profile_data": {                          // Optional: Profile metadata
    "full_name": "...",
    "biography": "...",
    "profile_pic_url": "...",
    "is_verified": false,
    "follower_count": 12345,
    "following_count": 678
  }
}
```

#### 2. `posts` Collection
**Purpose**: Stores posts from Instagram accounts (first page responses)

**Expected Schema** (Instagram API response structure):
```json
{
  "_id": ObjectId,
  "username": "acharyavinodkumar",           // Account that posted
  "post_id": "1234567890_987654321",         // Instagram post ID
  "shortcode": "ABC123xyz",                  // Instagram shortcode
  "taken_at": ISODate,                       // When photo was taken (from EXIF)
  "display_url": "https://instagram.com/...", // Full resolution image URL
  "thumbnail_url": "https://instagram.com/...", // Thumbnail URL
  "is_video": false,                         // true if video post
  "video_url": "https://...",                // If is_video=true
  "caption": "Post caption text...",         // Post description
  "likes_count": 1234,
  "comments_count": 56,
  "fetched_at": ISODate,                     // When we fetched this post
  "tags": ["tag1", "tag2"],                 // Optional: extracted tags
  "mentions": ["@user1", "@user2"]          // Optional: mentioned users
}
```

---

## Mapping Strategy

### Key Mappings:

| Instagram Field | FaceSearch Field | Purpose | Notes |
|----------------|------------------|---------|-------|
| `username` (from posts) | `AlbumId` | Person identity | Use `__username__` format to match directory naming |
| `display_url` or `video_url` | `AbsolutePath` | Image location | Store URL instead of file path |
| `post_id` or `shortcode` | `Id` | Unique identifier | Use SHA256 hash of URL + post_id for uniqueness |
| `taken_at` | `TakenAt` | Photo timestamp | Use Instagram's taken_at timestamp |
| `is_video` | `MediaType` | Media type | Map: `false` → "image", `true` → "video" |
| `fetched_at` | `CreatedAt` | Indexing timestamp | When we ingested this into FaceSearch |
| `caption` | (Tags) | Metadata | Could extract tags from caption |
| `target_username` (from followings) | (Album grouping) | Source account | Could be used as a tag or parent grouping |

### Special Considerations:

1. **AlbumId Mapping**:
   - **Option A**: Use `__username__` format (e.g., `__acharyavinodkumar__`)
   - **Option B**: Use plain username (e.g., `acharyavinodkumar`)
   - **Recommendation**: Use `__username__` to match existing directory-based albums

2. **Image ID Generation**:
   - **Option A**: SHA256 hash of `display_url` + `post_id`
   - **Option B**: SHA256 hash of downloaded image content (if downloading)
   - **Option C**: Use `post_id` directly (if unique enough)
   - **Recommendation**: SHA256 hash of `post_id` + `username` for uniqueness

3. **AbsolutePath for URLs**:
   - Since these are URLs, not file paths, we have two options:
     - **Option A**: Store URL directly in `AbsolutePath` (worker needs to download)
     - **Option B**: Download images first, store local path
   - **Recommendation**: Store URL in `AbsolutePath`, modify worker to handle URLs

4. **Tags Extraction**:
   - Extract hashtags from `caption` (e.g., `#tag1` → `tag1`)
   - Extract mentions (e.g., `@user` → `mention:user`)
   - Add source account tag (e.g., `source:viralbhayani`)

5. **TakenAt Timestamp**:
   - Use `taken_at` from Instagram API (if available)
   - Fallback to `fetched_at` if `taken_at` is missing

---

## Proposed Ingestion Pipeline Design

### Phase 1: Analysis & Mapping Service

**Service**: `InstagramSeedingService`

**Responsibilities**:
1. Connect to `instafollowing` database
2. Query `followings` collection to get list of accounts
3. For each account, query `posts` collection
4. Map Instagram post data to `ImageDocMongo` structure
5. Bulk insert into `facesearch.images` collection

### Phase 2: API Endpoint

**Endpoint**: `POST /api/index/seed-instagram`

**Request**:
```json
{
  "targetUsername": "viralbhayani",        // Optional: filter by source account
  "followingUsername": null,              // Optional: filter by specific account
  "includeVideos": false,                  // Include video posts
  "minLikes": 0,                          // Optional: filter by minimum likes
  "dateFrom": null,                       // Optional: filter posts from date
  "dateTo": null,                         // Optional: filter posts to date
  "tags": ["source:instagram"]            // Additional tags to add
}
```

**Response**:
```json
{
  "targetUsername": "viralbhayani",
  "accountsProcessed": 150,
  "postsScanned": 5000,
  "postsMatched": 4500,
  "postsUpserted": 4500,
  "postsSucceeded": 4500,
  "errors": []
}
```

### Phase 3: Worker Modification

**Current Worker Behavior**:
- Reads `AbsolutePath` as file path
- Opens file and generates embeddings

**Required Changes**:
- Detect if `AbsolutePath` is a URL (starts with `http://` or `https://`)
- If URL: Download image to temp location, process, then delete
- If file path: Use existing logic

---

## Implementation Plan

### Step 1: Create Instagram Data Models

```csharp
// Models for reading from instafollowing database
public class InstagramFollowing
{
    public string TargetUsername { get; set; }
    public string FollowingUsername { get; set; }
    public DateTime FetchedAt { get; set; }
    public InstagramProfile? ProfileData { get; set; }
}

public class InstagramPost
{
    public string Username { get; set; }
    public string PostId { get; set; }
    public string Shortcode { get; set; }
    public DateTime TakenAt { get; set; }
    public string DisplayUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool IsVideo { get; set; }
    public string? VideoUrl { get; set; }
    public string? Caption { get; set; }
    public int LikesCount { get; set; }
    public int CommentsCount { get; set; }
    public DateTime FetchedAt { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? Mentions { get; set; }
}
```

### Step 2: Create Instagram Seeding Service

```csharp
public interface IInstagramSeedingService
{
    Task<InstagramSeedResult> SeedInstagramAsync(
        InstagramSeedRequest request, 
        CancellationToken ct = default);
}
```

### Step 3: Extract Tags from Caption

```csharp
private List<string> ExtractTagsFromCaption(string? caption)
{
    if (string.IsNullOrWhiteSpace(caption)) return new List<string>();
    
    var tags = new List<string>();
    
    // Extract hashtags (#tag)
    var hashtagPattern = @"#(\w+)";
    var hashtags = Regex.Matches(caption, hashtagPattern)
        .Cast<Match>()
        .Select(m => m.Groups[1].Value.ToLowerInvariant())
        .ToList();
    tags.AddRange(hashtags);
    
    // Extract mentions (@user)
    var mentionPattern = @"@(\w+)";
    var mentions = Regex.Matches(caption, mentionPattern)
        .Cast<Match>()
        .Select(m => $"mention:{m.Groups[1].Value.ToLowerInvariant()}")
        .ToList();
    tags.AddRange(mentions);
    
    return tags.Distinct().ToList();
}
```

### Step 4: Generate Image ID

```csharp
private string GenerateImageId(string username, string postId)
{
    var input = $"{username}:{postId}";
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
}
```

### Step 5: Map Instagram Post to ImageDocMongo

```csharp
private ImageDocMongo MapPostToImageDoc(
    InstagramPost post, 
    string targetUsername, 
    List<string> additionalTags)
{
    var albumId = $"__{post.Username}__"; // Match directory naming convention
    var imageId = GenerateImageId(post.Username, post.PostId);
    
    var tags = new List<string>();
    tags.AddRange(additionalTags);
    tags.Add($"source:instagram");
    tags.Add($"source_account:{targetUsername}");
    
    if (post.Tags != null) tags.AddRange(post.Tags);
    if (post.Caption != null)
    {
        var extractedTags = ExtractTagsFromCaption(post.Caption);
        tags.AddRange(extractedTags);
    }
    
    return new ImageDocMongo
    {
        Id = imageId,
        AlbumId = albumId,
        AbsolutePath = post.IsVideo ? (post.VideoUrl ?? post.DisplayUrl) : post.DisplayUrl,
        MediaType = post.IsVideo ? "video" : "image",
        EmbeddingStatus = "pending",
        CreatedAt = post.FetchedAt,
        EmbeddedAt = null,
        Error = null,
        SubjectId = null,
        TakenAt = post.TakenAt,
        HasPeople = false, // Will be set by worker
        Tags = tags.Distinct().ToList()
    };
}
```

---

## Questions to Resolve

1. **Database Connection**: 
   - Should we use the same MongoDB connection string or a separate one for `instafollowing`?
   - **Recommendation**: Use same connection, different database name

2. **Image Downloading**:
   - Should worker download images immediately or on-demand?
   - **Recommendation**: Download on-demand in worker (lazy loading)

3. **Album Creation**:
   - Should we pre-create albums or let the finalizer create them?
   - **Recommendation**: Let finalizer create (same as directory indexing)

4. **Duplicate Detection**:
   - How to handle if same post appears in multiple source accounts?
   - **Recommendation**: Use content hash (if downloading) or post_id + username (if URL-based)

5. **URL Expiration**:
   - Instagram URLs may expire. Should we download and store locally?
   - **Recommendation**: Download during worker processing and cache locally

---

## Next Steps

1. ✅ Analyze Instagram data structure (this document)
2. ⏳ Verify actual MongoDB collection schemas
3. ⏳ Implement `InstagramSeedingService`
4. ⏳ Add API endpoint
5. ⏳ Modify worker to handle URLs
6. ⏳ Test with sample data
7. ⏳ Add error handling and retry logic

