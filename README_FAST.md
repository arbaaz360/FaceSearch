# Fast Pipeline (Separable from main FaceSearch)

This is an optional, speed-optimised path that **does not change** the main FaceSearch flows. It uses the same embedder but writes to its own Qdrant collection (`faces_fast_512`) and skips clustering/review logic.

## Setup
1) Ensure Qdrant is up, then create the fast collection:
```
scripts/create-qdrant-fast-collection.ps1
```
2) Configure folders to index in `Workers.FastIndexer/appsettings.json` under `FastIndexer:Folders`.

## Run the fast pipeline
```
start-fast.bat
```
This will:
- Verify (or start) an embedder on :8090
- Run the FastSearch API on :5251
- Run the FastIndexer worker (indexes the configured folders into `faces_fast_512`)

You can also run components manually:
- `dotnet run --project FastSearch.FastApi --urls http://localhost:5251`
- `dotnet run --project Workers.FastIndexer`

## Fast API
- Search: `POST /fast/search` (multipart `file`, optional `topK`) → returns top matches with file paths/notes.
- Health: `GET /fast/health`

## Fast UI
The existing frontend includes a new **Fast Search** page (route `/fast-search`) that talks to the Fast API (proxied to `http://localhost:5251` in dev).

## Notes
- Payload stored per face: `path`, `faceIndex`, optional `note`, optional `bbox`.
- IDs are deterministic (path + face index) so re-indexing updates existing points.
- Main FaceSearch collections and flows remain untouched.
