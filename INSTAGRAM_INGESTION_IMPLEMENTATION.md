# Instagram Ingestion Pipeline - Implementation Summary

## Overview

The Instagram ingestion pipeline allows you to ingest Instagram posts from the `instafollowing` MongoDB database into the FaceSearch system, similar to how directory indexing works.

## Features Implemented

### 1. **Instagram Seeding Service** (`InstagramSeedingService`)
- Reads from `instafollowing` database (`followings` and `posts` collections)
- Maps Instagram data to `ImageDocMongo` structure
- Marks accounts as ingested in the `followings` collection
- Supports filtering by target username and following username (for testing)

### 2. **API Endpoints**

#### `POST /api/instagram/seed`
Ingest Instagram posts into FaceSearch.

**Request Body**:
```json
{
  "targetUsername": "viralbhayani",        // Optional: filter by source account
  "followingUsername": "acharyavinodkumar", // Optional: test with single account
  "includeVideos": false,                  // Include video posts
  "minLikes": 0,                          // Optional: minimum likes filter
  "dateFrom": null,                       // Optional: filter from date
  "dateTo": null,                         // Optional: filter to date
  "tags": ["source:instagram"]            // Additional tags
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
  "errors": [],
  "accountStats": {
    "acharyavinodkumar": 25,
    "another_account": 30
  }
}
```

#### `GET /api/instagram/status`
Get ingestion status for all accounts.

**Query Parameters**:
- `targetUsername` (optional): Filter by source account

**Response**:
```json
[
  {
    "username": "acharyavinodkumar",
    "targetUsername": "viralbhayani",
    "postCount": 25,
    "isIngested": true,
    "ingestedAt": "2024-12-01T10:00:00Z",
    "imagesCreated": 25
  }
]
```

### 3. **Frontend UI** (Indexing Page)

The Indexing page now has two tabs:
- **Directory Indexing**: Original directory scanning functionality
- **Instagram Ingestion**: New Instagram data ingestion

**Features**:
- Test with single account: Enter `followingUsername` to test with one account
- Ingest all accounts: Leave `followingUsername` empty to process all
- Filter by source: Enter `targetUsername` to filter by source account
- Account status table: Shows ingestion status for all accounts
- Refresh status: Button to reload account status

## Data Mapping

| Instagram Field | FaceSearch Field | Notes |
|----------------|------------------|-------|
| `username` | `AlbumId` | Format: `__username__` |
| `display_url` / `video_url` | `AbsolutePath` | Stored as URL (worker will download) |
| `post_id` + `username` | `Id` | SHA256 hash of `username:post_id` |
| `taken_at` | `TakenAt` | Photo timestamp |
| `is_video` | `MediaType` | `false` → "image", `true` → "video" |
| `fetched_at` | `CreatedAt` | When we ingested |
| `caption` | `Tags` | Extracted hashtags and mentions |
| `target_username` | `Tags` | Added as `source_account:target_username` |

## Ingestion Status Tracking

The system marks accounts as ingested in the `followings` collection:
- `ingested`: boolean flag
- `ingested_at`: timestamp when ingestion completed

## Usage

### Test with Single Account:
1. Go to Indexing page → Instagram Ingestion tab
2. Enter `followingUsername` (e.g., "acharyavinodkumar")
3. Click "Test Single Account"
4. Check results and account status

### Ingest All Accounts:
1. Go to Indexing page → Instagram Ingestion tab
2. Optionally enter `targetUsername` to filter by source
3. Leave `followingUsername` empty
4. Click "Ingest All Accounts"
5. Monitor progress in results section

## Next Steps

1. **Worker URL Support**: The worker needs to be modified to handle URLs in `AbsolutePath` (download images before processing)
2. **Batch Processing**: Consider adding batch size limits for large ingestions
3. **Progress Tracking**: Add real-time progress updates for long-running ingestions
4. **Error Recovery**: Add ability to retry failed accounts

