"""PaddleOCR in a restartable child process with hard per-request deadlines."""
from __future__ import annotations

import math
import multiprocessing
from pathlib import Path

from .image_queue import prepare_ocr_image

CACHE_VERSION = "paddleocr-3.3.2-paddle-3.2.2-ppocrv5-mobile-preprocess-v1"


def recognize(engine, path: str, preprocessed: bool) -> list[dict]:
    image = prepare_ocr_image(Path(path)) if preprocessed else Path(path)
    source = str(image)
    if image == Path(path) and not image.suffix:
        from PIL import Image, ImageOps
        import numpy as np

        with Image.open(image) as original:
            rgb = ImageOps.exif_transpose(original).convert("RGB")
            source = np.ascontiguousarray(np.asarray(rgb)[:, :, ::-1])
    lines = []
    for result in engine.predict(source):
        for text, score in zip(result["rec_texts"], result["rec_scores"], strict=True):
            confidence = float(score)
            if str(text).strip() and math.isfinite(confidence) and 0 <= confidence <= 1:
                lines.append({"text": str(text), "confidence": confidence})
    return lines


def serve(connection):
    try:
        from paddleocr import PaddleOCR

        engine = PaddleOCR(
            device="cpu", enable_mkldnn=False, cpu_threads=2,
            text_detection_model_name="PP-OCRv5_mobile_det",
            text_recognition_model_name="PP-OCRv5_mobile_rec",
            use_doc_orientation_classify=False, use_doc_unwarping=False,
            use_textline_orientation=False, text_rec_score_thresh=0,
        )
        connection.send(("ok", None))
        while True:
            path, preprocessed = connection.recv()
            try:
                connection.send(("ok", recognize(engine, path, preprocessed)))
            except Exception as exc:
                connection.send(("error", f"{type(exc).__name__}: {exc}"))
    except EOFError:
        pass
    except Exception as exc:
        connection.send(("error", f"{type(exc).__name__}: {exc}"))
    finally:
        connection.close()


class PaddleWorker:
    def __init__(self, *, target=serve, startup_timeout=150, inference_timeout=35):
        self.context = multiprocessing.get_context("spawn")
        self.target = target
        self.startup_timeout = startup_timeout
        self.inference_timeout = inference_timeout
        self.process = None
        self.connection = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        if self.connection is not None:
            self.connection.close()
            self.connection = None
        if self.process is not None:
            self.process.join(0.1)
            if self.process.is_alive():
                self.process.kill()
                self.process.join()
            self.process.close()
            self.process = None

    def receive(self, seconds, stage):
        try:
            if not self.connection.poll(seconds):
                raise TimeoutError(f"{stage} exceeded {seconds}s")
            status, value = self.connection.recv()
        except (TimeoutError, EOFError, OSError):
            self.close()
            raise
        if status != "ok":
            raise RuntimeError(f"{stage}: {value}")
        return value

    def __call__(self, path: Path, preprocessed: bool):
        if self.process is None:
            self.connection, child = self.context.Pipe()
            self.process = self.context.Process(target=self.target, args=(child,), daemon=True)
            self.process.start()
            child.close()
            try:
                self.receive(self.startup_timeout, "model startup")
            except Exception:
                self.close()
                raise
        self.connection.send((str(path), preprocessed))
        return self.receive(self.inference_timeout, "OCR inference")
