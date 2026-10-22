"""PyMuPDF side of tools/ContentProbe (run with python -P).

  text <list.txt> <out.jsonl>        one JSON line per PDF: text of every page joined by form feeds (at most 1M chars)
  encrypt <list.txt> <folder> <out>  for each PDF: a plain copy and RC4-40, RC4-128, AES-128, AES-256 copies with an
                                     empty user password, written only into <folder>; one JSON line per PDF
  boxes <list.txt> <out.jsonl>       a few words of an upright page and page.search_for's rectangles for each
  expand <list.txt> <folder> <out>   an uncompressed copy of each PDF in <folder> (for structured fuzzing)
The PDFs named in the lists are only opened for reading.
"""
import json
import os
import sys
import time

import fitz

MAX_CHARS = 1 << 20
MAX_PAGES = 2000


def text_of(path):
    rec = {"path": path}
    start = time.perf_counter()
    try:
        doc = fitz.open(path)
        if doc.needs_pass:
            rec["encrypted"] = True
            rec["text"] = ""
        else:
            parts, total = [], 0
            for i, page in enumerate(doc):
                if i >= MAX_PAGES or total > MAX_CHARS:
                    break
                s = page.get_text()
                parts.append(s)
                total += len(s)
            rec["text"] = "\f".join(parts)[:MAX_CHARS]
            rec["pages"] = doc.page_count
        doc.close()
    except Exception as e:  # noqa: BLE001 - any failure is a result here
        rec["error"] = type(e).__name__
    rec["ms"] = (time.perf_counter() - start) * 1000
    return rec


def encrypt(path, folder, index):
    rec = {"path": path, "copies": {}}
    methods = {
        "plain": fitz.PDF_ENCRYPT_NONE,
        "rc4-40": fitz.PDF_ENCRYPT_RC4_40,
        "rc4-128": fitz.PDF_ENCRYPT_RC4_128,
        "aes-128": fitz.PDF_ENCRYPT_AES_128,
        "aes-256": fitz.PDF_ENCRYPT_AES_256,
    }
    try:
        for name, method in methods.items():
            doc = fitz.open(path)
            if doc.needs_pass:
                rec["error"] = "encrypted"
                doc.close()
                break
            out = os.path.join(folder, f"{index:05d}-{name}.pdf")
            if method == fitz.PDF_ENCRYPT_NONE:
                doc.save(out)
            else:
                perm = int(fitz.PDF_PERM_ACCESSIBILITY | fitz.PDF_PERM_PRINT | fitz.PDF_PERM_COPY)
                doc.save(out, encryption=method, owner_pw="owner-secret", user_pw="", permissions=perm)
            doc.close()
            rec["copies"][name] = out
    except Exception as e:  # noqa: BLE001
        rec["error"] = type(e).__name__
    return rec


def boxes(path):
    """Up to 4 words of the first upright page with text, and PyMuPDF's search_for rectangles for each."""
    rec = {"path": path, "words": []}
    try:
        doc = fitz.open(path)
        if doc.needs_pass:
            return rec
        for page in doc:
            if page.number >= 20:
                break
            if page.rotation != 0 or page.cropbox != page.mediabox:
                continue
            words = [w[4] for w in page.get_text("words") if len(w[4]) >= 5 and w[4].isalpha()]
            if not words:
                continue
            picked = []
            for w in words:
                if w.lower() not in [p.lower() for p in picked]:
                    picked.append(w)
                if len(picked) == 4:
                    break
            rec["page"] = page.number
            rec["width"] = page.rect.width
            rec["height"] = page.rect.height
            for w in picked:
                rects = page.search_for(w)
                rec["words"].append({"word": w, "rects": [[r.x0, r.y0, r.x1, r.y1] for r in rects]})
            break
        doc.close()
    except Exception as e:  # noqa: BLE001
        rec["error"] = type(e).__name__
    return rec


def expand(path, folder, index):
    """An uncompressed copy (streams decoded, no object streams) for structured fuzzing."""
    rec = {"path": path}
    try:
        doc = fitz.open(path)
        if doc.needs_pass:
            rec["error"] = "encrypted"
        else:
            out = os.path.join(folder, f"{index:05d}-expanded.pdf")
            doc.save(out, expand=255, garbage=1)
            rec["copy"] = out
        doc.close()
    except Exception as e:  # noqa: BLE001
        rec["error"] = type(e).__name__
    return rec


def main():
    fitz.TOOLS.mupdf_display_errors(False)
    mode = sys.argv[1]
    if mode == "text":
        with open(sys.argv[2], encoding="utf-8") as src, open(sys.argv[3], "w", encoding="utf-8") as out:
            for line in src:
                path = line.rstrip("\r\n")
                if path:
                    out.write(json.dumps(text_of(path), ensure_ascii=False) + "\n")
                    out.flush()
    elif mode == "encrypt":
        folder = sys.argv[3]
        os.makedirs(folder, exist_ok=True)
        with open(sys.argv[2], encoding="utf-8") as src, open(sys.argv[4], "w", encoding="utf-8") as out:
            for index, line in enumerate(src):
                path = line.rstrip("\r\n")
                if path:
                    out.write(json.dumps(encrypt(path, folder, index), ensure_ascii=False) + "\n")
                    out.flush()
    elif mode == "boxes":
        with open(sys.argv[2], encoding="utf-8") as src, open(sys.argv[3], "w", encoding="utf-8") as out:
            for line in src:
                path = line.rstrip("\r\n")
                if path:
                    out.write(json.dumps(boxes(path), ensure_ascii=False) + "\n")
                    out.flush()
    elif mode == "expand":
        folder = sys.argv[3]
        os.makedirs(folder, exist_ok=True)
        with open(sys.argv[2], encoding="utf-8") as src, open(sys.argv[4], "w", encoding="utf-8") as out:
            for index, line in enumerate(src):
                path = line.rstrip("\r\n")
                if path:
                    out.write(json.dumps(expand(path, folder, index), ensure_ascii=False) + "\n")
                    out.flush()
    else:
        raise SystemExit("unknown mode " + mode)


if __name__ == "__main__":
    main()
