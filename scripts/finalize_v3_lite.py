from pathlib import Path
import base64
import zlib

chunk_root = Path(__file__).with_name("finalize_v3_lite_chunks")
payload = "".join(
    path.read_text(encoding="ascii")
    for path in sorted(chunk_root.glob("*.b85"))
)
if not payload:
    raise RuntimeError("V3 Lite finalizer payload is missing.")
exec(zlib.decompress(base64.b85decode(payload)).decode("utf-8"))
