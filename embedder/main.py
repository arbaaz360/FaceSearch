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

print(f"[InsightFace] ready; active providers={FACE_PROVIDERS_USED or providers}")

# -------------------------------
# Schemas
# -------------------------------
class TextRequest(BaseModel):
    text: str
# -------------------------------
# Debug/status endpoint
# -------------------------------
# add near your imports where device_label, face_app etc. are defined
@app.get("/_status")
async def status():
    try:
        import onnxruntime as ort
        onnx_providers = ort.get_available_providers()
    except Exception:
        onnx_providers = None

    return {
        "clip_device": device_label,
        "insightface_loaded": face_app is not None,
        "insightface_active_providers": face_provider_used if 'face_provider_used' in globals() else None,
        "onnx_available_providers": onnx_providers,
    }
import time

@app.get("/_selftest")
async def selftest():
    # text -> CLIP
    t0 = time.time()
    tokens = open_clip.tokenize(["a red skirt on a mannequin"]).to(device)
    with torch.no_grad():
        v_text = clip_model.encode_text(tokens).float()
        v_text /= v_text.norm(dim=-1, keepdim=True)
    t1 = time.time()

    # image -> CLIP (dummy 224x224)
    import numpy as np
    from PIL import Image
    dummy = Image.fromarray((np.random.rand(224,224,3)*255).astype('uint8'))
    img_t = clip_preprocess(dummy).unsqueeze(0).to(device)
    with torch.no_grad():
        v_img = clip_model.encode_image(img_t).float()
        v_img /= v_img.norm(dim=-1, keepdim=True)
    t2 = time.time()

    return {
        "clip_device": device_label,
        "timings_ms": {
            "text_embed": round((t1 - t0)*1000, 2),
            "image_embed": round((t2 - t1)*1000, 2),
        }
    }
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
    faces = face_app.get(img)
    if not faces:
        return {"vector": []}
    return {"vector": faces[0].normed_embedding.tolist()}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8090)
