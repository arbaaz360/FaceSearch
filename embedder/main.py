from fastapi import FastAPI, UploadFile, File, HTTPException
from pydantic import BaseModel
from PIL import Image
import numpy as np
import io
import torch
import open_clip
import cv2
import os
import insightface
# Optional: introspect ONNX Runtime if present
try:
    import onnxruntime as ort
except Exception:
    ort = None

app = FastAPI(title="Embedder Service (GPU)", version="3.1.0")

# -------------------------------
# Device selection (CUDA > DirectML > CPU)
# -------------------------------
force = os.getenv("CLIP_DEVICE", "").lower()  # "cuda" | "dml" | "cpu" | ""

cuda_available = torch.cuda.is_available()

# torch-directml detects via the separate package
try:
    import torch_directml
    dml_available = True
    dml_device = torch_directml.device()
except Exception:
    dml_available = False
    dml_device = None

device = None
device_label = "cpu (CPU)"

if force == "cuda" and cuda_available:
    device = torch.device("cuda:0")
    device_label = f"cuda:0 ({torch.cuda.get_device_name(0)})"
elif force == "dml" and dml_available:
    device = dml_device
    device_label = "dml (DirectML)"
elif force == "cpu":
    device = torch.device("cpu")
    device_label = "cpu (CPU)"
else:
    if cuda_available:
        device = torch.device("cuda:0")
        device_label = f"cuda:0 ({torch.cuda.get_device_name(0)})"
    elif dml_available:
        device = dml_device
        device_label = "dml (DirectML)"
    else:
        device = torch.device("cpu")

print(f"[CLIP] device={device_label}")
# -------------------------------
# Load OpenCLIP (image/text encoder)
# -------------------------------
print("[CLIP] Loading OpenCLIP ViT-B/32 (pretrained='openai') …")
clip_model, _, clip_preprocess = open_clip.create_model_and_transforms("ViT-B-32", pretrained="openai")
clip_model.eval()
clip_model.to(device)
print("[CLIP] ready")
# -------------------------------
# Inspect ONNX providers + Insightface(if onnxruntime present)
# -------------------------------
# --- InsightFace / ONNX provider selection ---
import onnxruntime as ort
available = ort.get_available_providers()
print(f"[ONNX] available providers={available}")

providers = ["CPUExecutionProvider"]
ctx_id = -1  # -1 = CPU/DML; 0 = CUDA device 0

if "CUDAExecutionProvider" in available:
    providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
    ctx_id = 0
elif "DmlExecutionProvider" in available:  # if you ever switch to onnxruntime-directml
    providers = ["DmlExecutionProvider", "CPUExecutionProvider"]
    ctx_id = -1

print(f"[InsightFace] init buffalo_l with providers={providers} …")
import insightface
face_app = insightface.app.FaceAnalysis(name="buffalo_l", providers=providers)
face_app.prepare(ctx_id=ctx_id)

# Capture the actual providers from the loaded sessions (recognition model reflects the final session)
FACE_PROVIDERS_USED = None
try:
    rec = face_app.models.get("recognition", None)
    if rec is not None and hasattr(rec, "session"):
        FACE_PROVIDERS_USED = rec.session.get_providers()
except Exception:
    pass

FACE_PROVIDERS_USED = FACE_PROVIDERS_USED or providers

face_device_label = None
if FACE_PROVIDERS_USED:
    if "CUDAExecutionProvider" in FACE_PROVIDERS_USED:
        face_device_label = "cuda"
    elif "DmlExecutionProvider" in FACE_PROVIDERS_USED:
        face_device_label = "directml"
    else:
        face_device_label = FACE_PROVIDERS_USED[0]

print(f"[InsightFace] ready; active providers={FACE_PROVIDERS_USED}")

# -------------------------------
# Schemas
# -------------------------------
class TextRequest(BaseModel):
    text: str
# -------------------------------
# Debug/status endpoint
# -------------------------------
import time


def _status_payload():
    status_value = "ok" if face_app is not None else "degraded"
    try:
        import onnxruntime as ort
        onnx_providers = ort.get_available_providers()
    except Exception:
        onnx_providers = None

    return {
        "status": status_value,
        "clip_device": device_label,
        "face_device": face_device_label,
        "insightface_loaded": face_app is not None,
        "insightface_active_providers": FACE_PROVIDERS_USED,
        "onnx_available_providers": onnx_providers,
    }


@app.get("/_status")
async def status():
    return _status_payload()


@app.get("/status")
async def status_alias():
    return _status_payload()

@app.get("/_selftest")
async def selftest():
    result = {
        "clip_device": device_label,
        "face_device": face_device_label,
        "timings_ms": {},
        "passed": False,
    }

    # text -> CLIP
    try:
        t0 = time.time()
        tokens = open_clip.tokenize(["a red skirt on a mannequin"]).to(device)
        with torch.no_grad():
            v_text = clip_model.encode_text(tokens).float()
            v_text /= v_text.norm(dim=-1, keepdim=True)
        t1 = time.time()
        result["timings_ms"]["text_embed"] = round((t1 - t0) * 1000, 2)

        # image -> CLIP (dummy 224x224)
        dummy = Image.fromarray((np.random.rand(224, 224, 3) * 255).astype('uint8'))
        img_t = clip_preprocess(dummy).unsqueeze(0).to(device)
        with torch.no_grad():
            v_img = clip_model.encode_image(img_t).float()
            v_img /= v_img.norm(dim=-1, keepdim=True)
        t2 = time.time()
        result["timings_ms"]["image_embed"] = round((t2 - t1) * 1000, 2)

        # face detect (blank image just exercises the pipeline)
        if face_app is not None:
            blank = np.zeros((320, 320, 3), dtype=np.uint8)
            _ = face_app.get(blank)
            t3 = time.time()
            result["timings_ms"]["face_embed"] = round((t3 - t2) * 1000, 2)
        else:
            result["timings_ms"]["face_embed"] = None
        result["passed"] = True
    except Exception as ex:
        result["passed"] = False
        result["details"] = str(ex)
    return result
@app.get("/_gpu")
def gpu_status():
    try:
        import torch
        if torch.cuda.is_available():
            mem = torch.cuda.memory_allocated(0) / (1024 ** 2)
            return {"cuda": torch.cuda.get_device_name(0), "memory_mb": round(mem, 2)}
    except Exception:
        pass
    return {"info": "DirectML device (memory not queryable via PyTorch)"}

# -------------------------------
# Endpoints
# -------------------------------
@app.post("/embed/text")
async def embed_text(req: TextRequest):
    tokens = open_clip.tokenize([req.text]).to(device)
    with torch.no_grad():
        feats = clip_model.encode_text(tokens).float()
        feats /= feats.norm(dim=-1, keepdim=True)
    return {"vector": feats[0].cpu().tolist()}

@app.post("/embed/image")
async def embed_image(file: UploadFile = File(...)):
    img = Image.open(io.BytesIO(await file.read())).convert("RGB")
    img_t = clip_preprocess(img).unsqueeze(0).to(device)
    with torch.no_grad():
        feats = clip_model.encode_image(img_t).float()
        feats /= feats.norm(dim=-1, keepdim=True)
    return {"vector": feats[0].cpu().tolist()}

@app.post("/embed/face")
async def embed_face(file: UploadFile = File(...)):
    if face_app is None:
        raise HTTPException(status_code=503, detail="face embedder not available (onnxruntime-directml not loaded or model init failed)")
    data = await file.read()
    arr = np.frombuffer(data, np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_COLOR)  # BGR
    if img is None:
        raise HTTPException(status_code=400, detail="invalid image data")

    faces = face_app.get(img)
    if not faces:
        return {"vector": []}
    first = faces[0]
    gender_label, gender_score = "unknown", None
    try:
        if hasattr(first, "gender") and first.gender is not None:
            gender_score = float(first.gender)
            # insightface gender: 0=female, 1=male
            gender_label = "female" if gender_score < 0.5 else "male"
    except Exception:
        pass
    return {
        "vector": first.normed_embedding.tolist(),
        "gender": gender_label,
        "gender_score": gender_score
    }


@app.post("/embed/face/multi")
async def embed_face_multi(file: UploadFile = File(...), female_only: bool = True):
    """
    Detect all faces, return embedding + gender per face.
    female_only=True filters to faces classified as female.
    """
    if face_app is None:
        raise HTTPException(status_code=503, detail="face embedder not available (onnxruntime-directml not loaded or model init failed)")

    data = await file.read()
    arr = np.frombuffer(data, np.uint8)
    img = cv2.imdecode(arr, cv2.IMREAD_COLOR)  # BGR
    if img is None:
        raise HTTPException(status_code=400, detail="invalid image data")

    faces = face_app.get(img)

    results = []
    for f in faces:
        gender_score = None
        gender_label = "unknown"
        try:
            if hasattr(f, "gender") and f.gender is not None:
                gender_score = float(f.gender)
                # insightface gender: 0=female, 1=male
                gender_label = "female" if gender_score < 0.5 else "male"
        except Exception:
            pass

        if female_only and gender_label != "female":
            continue

        bbox = None
        try:
            if hasattr(f, "bbox") and f.bbox is not None:
                # bbox usually ndarray [x1, y1, x2, y2]
                bbox = [int(v) for v in f.bbox]
        except Exception:
            pass

        results.append({
            "vector": f.normed_embedding.tolist(),
            "gender": gender_label,
            "gender_score": gender_score,
            "bbox": bbox
        })

    return {"faces": results, "count": len(results)}

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", "8090"))
    uvicorn.run(app, host="0.0.0.0", port=port)
