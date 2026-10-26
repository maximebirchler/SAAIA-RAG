"""Real, offline E5 model behind the product's embeddings protocol, for local validation.

This is not the TEI server binary and never impersonates its runtime identity.
Model files must already exist locally; no automatic download or truncation.
"""
from __future__ import annotations

import argparse
import hashlib
from importlib.metadata import version
import threading
from pathlib import Path

MODEL_ID = "intfloat/multilingual-e5-base"
MODEL_REVISION = "d128750597153bb5987e10b1c3493a34e5a4502a"
WEIGHTS_SHA256 = "a18a44fad1d0b46ded15928144138cff1135d5cc8233bdd90be5f18822de09a7"


def serve(model_path: Path, port: int, threads: int) -> None:
    import torch
    import uvicorn
    from fastapi import FastAPI, HTTPException
    from pydantic import BaseModel
    from transformers import AutoModel, AutoTokenizer

    if model_path.name != MODEL_REVISION or not (model_path / "model.safetensors").is_file():
        raise ValueError("Pinned model snapshot is required.")
    with (model_path / "model.safetensors").open("rb") as weights:
        if hashlib.file_digest(weights, "sha256").hexdigest() != WEIGHTS_SHA256:
            raise ValueError("Pinned model weights hash mismatch.")
    torch.set_num_threads(threads)
    tokenizer = AutoTokenizer.from_pretrained(model_path, local_files_only=True, trust_remote_code=False)
    model = AutoModel.from_pretrained(model_path, local_files_only=True, trust_remote_code=False).eval()
    if model.config.hidden_size != 768:
        raise ValueError("Unexpected model dimension.")
    lock = threading.Lock()
    counters = {"requests": 0, "inputs": 0, "maximumTokens": 0, "rejectedTooLong": 0}
    runtime_revision = ("saaia-validation-transformers-cpu.v1:" + hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
                        + ":torch-" + torch.__version__ + ":transformers-" + version("transformers"))
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

    class EmbeddingRequest(BaseModel):
        model: str
        input: str | list[str]

    @app.get("/health")
    @app.get("/info")
    def health() -> dict:
        return {"ok": True, "adapter": "transformers_cpu", "model_id": MODEL_ID, "model_revision": MODEL_REVISION, "dimensions": 768,
                "model_file_sha256": WEIGHTS_SHA256,
                "runtime_revision": runtime_revision, "torch": torch.__version__,
                "offline": True, "externalLlmCalls": 0, "counters": counters.copy()}

    # Register explicitly: the model is local to this function, not an unresolved forward annotation.
    def embeddings(request):
        texts = [request.input] if isinstance(request.input, str) else request.input
        if request.model != MODEL_ID or not texts or len(texts) > 32 or any(
            not text or len(text) > 100_000 or not text.startswith(("query: ", "passage: ")) for text in texts
        ):
            raise HTTPException(400, "invalid_local_embedding_request")
        with lock:
            batch = tokenizer(texts, padding=True, truncation=False, return_tensors="pt")
            lengths = batch["attention_mask"].sum(dim=1)
            maximum = int(lengths.max())
            counters["maximumTokens"] = max(counters["maximumTokens"], maximum)
            if maximum > 512:
                counters["rejectedTooLong"] += 1
                raise HTTPException(413, "embedding_input_exceeds_512_tokens_no_truncation")
            with torch.inference_mode():
                hidden = model(**batch).last_hidden_state
                mask = batch["attention_mask"].unsqueeze(-1)
                pooled = (hidden * mask).sum(dim=1) / mask.sum(dim=1)
                vectors = torch.nn.functional.normalize(pooled, p=2, dim=1).tolist()
            counters["requests"] += 1
            counters["inputs"] += len(texts)
        return {"object": "list", "model": MODEL_ID,
                "data": [{"object": "embedding", "index": index, "embedding": vector} for index, vector in enumerate(vectors)],
                "usage": {"prompt_tokens": int(lengths.sum()), "total_tokens": int(lengths.sum())}}

    embeddings.__annotations__["request"] = EmbeddingRequest
    app.post("/v1/embeddings")(embeddings)
    uvicorn.run(app, host="127.0.0.1", port=port, workers=1, access_log=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-path", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--threads", type=int, default=8)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535 or args.port == 5432 or not 1 <= args.threads <= 32:
        raise ValueError("Invalid local resource bounds.")
    serve(args.model_path.resolve(strict=True), args.port, args.threads)
