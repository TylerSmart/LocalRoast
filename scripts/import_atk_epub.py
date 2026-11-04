#!/usr/bin/env python3
"""
import_atk_epub.py
------------------
High-speed parser and bulk importer for "The Complete America's Test Kitchen TV Show Cookbook" EPUB into LocalRoast.

Extracts:
- Exact recipe titles & IDs from XHTML semantic markup
- Print page numbers (from embedded epub:type="pagebreak")
- Yield strings and calculated baseServings
- Descriptions & "Why This Recipe Works" headnotes
- Ingredient lists (with HTML fractions converted to standard fractions like '1 1/2')
- Ingredient sections (e.g. "For the Marinade", "For the Sauce")
- Ordered preparation steps
- Sub-recipes & variation notes attached to parent recipes
- High-resolution dish photos matched from EPUB figures/captions
- Chapter categorization (mapped to LocalRoast Collections / Tags)

Usage:
  # 1. Inspect & Dry-Run (0 network calls, test extraction quality):
  python scripts/import_atk_epub.py --dry-run

  # 2. Dry-Run only Chapter 5 (Pasta) with first 5 recipes:
  python scripts/import_atk_epub.py --dry-run --chapter 5 --limit 5

  # 3. Enqueue first 10 recipes into LocalRoast's LLM Ingestion Queue (DEFAULT API mode):
  python scripts/import_atk_epub.py --email your@email.com --password yourpassword --limit 10

  # 4. Enqueue full book into LLM Ingestion Queue (creates chapter Collections, uploads dish photos, auto-publishes):
  python scripts/import_atk_epub.py --email your@email.com --password yourpassword

  # 5. Enqueue into Review Inbox without auto-publishing:
  python scripts/import_atk_epub.py --email your@email.com --password yourpassword --limit 5 --review-only
"""

import argparse
import io
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from xml.etree import ElementTree as ET

# Ensure UTF-8 output on Windows terminal
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

DEFAULT_EPUB_PATH = r"C:\Users\tyler\Calibre Library\America's Test Kitchen\The Complete America's Test Kitchen (777)\The Complete America's Test Kit - America's Test Kitchen.epub"
BOOK_TITLE = "The Complete America's Test Kitchen TV Show Cookbook"
BOOK_AUTHOR = "America's Test Kitchen"


def extract_node_text(node):
    """Recursively extract text from an XML element, cleanly spacing fractions."""
    if node is None:
        return ""
    parts = []

    def recurse(el):
        # Ignore page breaks
        if (
            "pagebreak" in el.get("{http://www.idpf.org/2007/ops}type", "")
            or "pagebreak" in el.get("role", "")
        ):
            if el.tail:
                parts.append(el.tail)
            return

        cls = el.get("class", "")
        if "numerator" in cls:
            # If preceding text ends with a digit, add space so '1' + '1/2' -> '1 1/2'
            if parts and parts[-1] and parts[-1][-1].isdigit():
                parts.append(" ")
            if el.text:
                parts.append(el.text)
        elif "denominator" in cls:
            if el.text:
                parts.append(el.text)
        else:
            if el.text:
                parts.append(el.text)

        for child in el:
            recurse(child)
        if el.tail:
            parts.append(el.tail)

    recurse(node)
    text = "".join(parts)
    return re.sub(r"\s+", " ", text).strip()


def extract_page_number(elem):
    """Find print page number from epub:type pagebreak or id."""
    if elem is None:
        return None
    for pb in elem.findall('.//*[@{http://www.idpf.org/2007/ops}type="pagebreak"]'):
        t = pb.get("title")
        if t and t.isdigit():
            return int(t)
    for pb in elem.findall('.//*[@role="doc-pagebreak"]'):
        t = pb.get("title")
        if t and t.isdigit():
            return int(t)
    for pb in elem.findall(".//*[@id]"):
        eid = pb.get("id", "")
        m = re.match(r"page_(\d+)", eid)
        if m:
            return int(m.group(1))
    return None


def parse_servings_from_yield(yield_text):
    """Infer base servings number from strings like 'SERVES 4 to 6' or 'MAKES 2 cups'."""
    if not yield_text:
        return 4.0
    text = yield_text.upper()
    # Match 'SERVES 4 TO 6' -> 4.0
    m_serves = re.search(r"SERVES\s+(\d+(?:\.\d+)?)", text)
    if m_serves:
        return float(m_serves.group(1))
    # Match 'MAKES X SERVINGS'
    m_servings = re.search(r"(\d+(?:\.\d+)?)\s+SERVINGS", text)
    if m_servings:
        return float(m_servings.group(1))
    # Match 'MAKES 12 MUFFINS' -> 12.0
    m_makes = re.search(r"MAKES\s+(\d+(?:\.\d+)?)", text)
    if m_makes:
        val = float(m_makes.group(1))
        # If makes 1 loaf or 2 cups, default to 4 servings
        return val if val >= 2.0 and val <= 100.0 else 4.0
    return 4.0


def parse_atk_epub(epub_path):
    """
    Parse the ATK EPUB archive into structured recipe dictionaries.
    Returns: (recipes, image_map, chapters)
    """
    if not os.path.exists(epub_path):
        raise FileNotFoundError(f"EPUB file not found at: {epub_path}")

    recipes = []
    image_map = {}  # recipe_id or normalized_title -> relative image path in zip

    print(f"Reading EPUB: {epub_path}...")
    with zipfile.ZipFile(epub_path, "r") as z:
        # Step 1: Read Table of Contents (NCX) to discover chapter titles
        ns_ncx = {"ncx": "http://www.daisy.org/z3986/2005/ncx/"}
        ncx_files = [f for f in z.namelist() if f.endswith("_ncx.ncx") or f.endswith(".ncx")]
        chapter_map = {}
        if ncx_files:
            ncx_data = z.read(ncx_files[0])
            root_ncx = ET.fromstring(ncx_data)
            for np in root_ncx.findall(".//ncx:navPoint", ns_ncx):
                content = np.find("ncx:content", ns_ncx)
                label = np.find("ncx:navLabel/ncx:text", ns_ncx)
                if content is not None and label is not None and label.text:
                    src = content.get("src", "")
                    if "#" not in src and "Chapter" in label.text:
                        chapter_map[src.replace("xhtml/", "")] = label.text.strip()

        print(f"Found {len(chapter_map)} chapters in NCX.")

        # Step 2: Index all figures and captions across all XHTML files
        ns_x = {"x": "http://www.w3.org/1999/xhtml"}
        for filename in sorted(z.namelist()):
            if not filename.startswith("OEBPS/xhtml/") or not filename.endswith(".xhtml"):
                continue
            try:
                data = z.read(filename)
                root = ET.fromstring(data)
                for fig in root.findall(".//{http://www.w3.org/1999/xhtml}figure"):
                    img = fig.find(".//{http://www.w3.org/1999/xhtml}img")
                    cap = fig.find(".//{http://www.w3.org/1999/xhtml}figcaption")
                    if img is not None and cap is not None:
                        src = img.get("src", "")
                        # Normalize path relative to OEBPS/
                        if src.startswith("../"):
                            img_zip_path = "OEBPS/" + src[3:]
                        else:
                            img_zip_path = "OEBPS/xhtml/" + src

                        # Check for anchor in caption link <a href="#c001-s2">
                        link = cap.find(".//{http://www.w3.org/1999/xhtml}a")
                        if link is not None and link.get("href"):
                            href = link.get("href").strip()
                            anchor = href.split("#")[-1]
                            image_map[anchor] = img_zip_path

                        # Also index by caption text
                        cap_text = extract_node_text(cap)
                        if cap_text and not cap_text.startswith("Photos (left to right)"):
                            norm_cap = re.sub(r"[^a-z0-9]", "", cap_text.lower())
                            image_map[norm_cap] = img_zip_path
            except Exception:
                pass

        print(f"Indexed {len(image_map)} recipe dish images from EPUB figures.")

        # Step 3: Parse recipe chapters in reading order
        current_chapter_name = "General"

        for filename in sorted(z.namelist()):
            if not filename.startswith("OEBPS/xhtml/") or not filename.endswith(".xhtml"):
                continue
            base = filename.replace("OEBPS/xhtml/", "")
            if any(
                skip in base
                for skip in [
                    "Cover",
                    "Also_by",
                    "Title_Page",
                    "Copyright",
                    "Index",
                    "Contents",
                    "Welcome",
                    "idx",
                    "bm_1",
                    "bm_2",
                ]
            ):
                continue

            if base in chapter_map:
                current_chapter_name = chapter_map[base]

            data = z.read(filename)
            root = ET.fromstring(data)
            body = root.find("x:body", ns_x)
            if body is None:
                continue

            children = body[0] if (len(body) == 1 and body[0].tag.endswith("div")) else body

            current_recipe = None
            parent_master_recipe = None

            for elem in children:
                tag = elem.tag.replace("{http://www.w3.org/1999/xhtml}", "")
                cls = elem.get("class", "")

                h2 = (
                    elem
                    if (tag == "h2" and "recipe_title" in cls)
                    else elem.find(".//x:h2[@class]", ns_x)
                )
                h3 = (
                    elem
                    if (tag == "h3" and "subrecipe_title" in cls)
                    else elem.find(".//x:h3[@class]", ns_x)
                )

                is_h2 = h2 is not None and "recipe_title" in h2.get("class", "")
                is_h3 = h3 is not None and "subrecipe_title" in h3.get("class", "")

                if is_h2 or is_h3:
                    t_elem = h2 if is_h2 else h3
                    t_type = "master" if is_h2 else "sub"
                    t_id = t_elem.get("id", "")
                    title = extract_node_text(t_elem)

                    # Extract yield
                    pyield = elem.find(".//x:p[@class]", ns_x)
                    yield_text = (
                        extract_node_text(pyield)
                        if (pyield is not None and "yield" in pyield.get("class", ""))
                        else None
                    )
                    page_no = extract_page_number(elem) or extract_page_number(t_elem)
                    base_servings = parse_servings_from_yield(yield_text)

                    # Match image
                    matched_img = image_map.get(t_id)
                    if not matched_img:
                        norm_title = re.sub(r"[^a-z0-9]", "", title.lower())
                        matched_img = image_map.get(norm_title)

                    new_recipe = {
                        "id": t_id,
                        "type": t_type,
                        "title": title,
                        "page": page_no,
                        "yield": yield_text,
                        "base_servings": base_servings,
                        "chapter": current_chapter_name,
                        "description": None,
                        "ingredients": [],  # list of { section, text }
                        "steps": [],
                        "variations": [],
                        "image_path": matched_img,
                    }

                    current_recipe = new_recipe
                    if is_h2:
                        parent_master_recipe = new_recipe
                        recipes.append(new_recipe)
                    else:
                        # Sub-recipe: we will check if it has standalone ingredients or is a variation
                        recipes.append(new_recipe)
                elif current_recipe is not None:
                    if "head_note" in cls:
                        desc = extract_node_text(elem)
                        current_recipe["description"] = (
                            (current_recipe["description"] + " " + desc)
                            if current_recipe["description"]
                            else desc
                        )
                    elif "ing_list" in cls:
                        current_section = None
                        for p in elem.findall(".//x:p", ns_x):
                            pcls = p.get("class", "")
                            ptext = extract_node_text(p)
                            if not ptext:
                                continue
                            if "head" in pcls or "section" in pcls:
                                current_section = ptext
                            else:
                                current_recipe["ingredients"].append(
                                    {"section": current_section, "text": ptext}
                                )
                    elif "method" in cls:
                        mtext = extract_node_text(elem)
                        if mtext:
                            current_recipe["steps"].append(mtext)

    # Post-process: attach zero-ingredient variations to parent master recipe
    final_recipes = []
    variation_count = 0

    for r in recipes:
        if r["type"] == "sub" and len(r["ingredients"]) == 0:
            # This is a variation note (e.g. "Substitute Gruyère for cheddar...")
            variation_count += 1
            if final_recipes:
                # Attach to previous recipe as a variation
                target_recipe = final_recipes[-1]
                var_text = f"**Variation: {r['title']}**\n" + "\n\n".join(r["steps"])
                target_recipe["variations"].append(var_text)
                if target_recipe["description"]:
                    target_recipe["description"] += f"\n\n{var_text}"
                else:
                    target_recipe["description"] = var_text
        else:
            final_recipes.append(r)

    print(
        f"Parsed {len(final_recipes)} complete recipes ({variation_count} variations consolidated into parent recipes)."
    )
    return final_recipes


def format_recipe_as_llm_raw_text(r):
    """Format an extracted recipe dictionary into a clean raw-text document for LLM extraction."""
    lines = [
        f"Title: {r['title']}",
        f"Author: {BOOK_AUTHOR}",
        f"Source: {BOOK_AUTHOR}",
        f"Cookbook: {BOOK_TITLE}",
    ]
    if r.get("page"):
        lines.append(f"Page: {r['page']}")
    if r.get("chapter"):
        lines.append(f"Chapter: {r['chapter']}")
    if r.get("yield"):
        lines.append(f"Yield: {r['yield']} (Base Servings: {r['base_servings']})")
    else:
        lines.append(f"Base Servings: {r['base_servings']}")

    if r.get("description"):
        lines.append("")
        lines.append("Description & Kitchen Notes:")
        lines.append(r["description"])

    lines.append("")
    lines.append("Ingredients:")
    last_section = None
    for ing in r["ingredients"]:
        sec = ing.get("section")
        if sec and sec != last_section:
            lines.append(f"\n[{sec}]")
            last_section = sec
        lines.append(f"- {ing['text']}")

    lines.append("")
    lines.append("Instructions:")
    for i, step_text in enumerate(r["steps"]):
        clean_step = re.sub(r"^\d+\.\s*", "", step_text)
        lines.append(f"{i + 1}. {clean_step}")

    return "\n".join(lines)[:99000]


def format_recipe_for_localroast(r, collection_id=None):
    """Format an extracted recipe dictionary into LocalRoast UpsertRecipeRequest structure."""
    ingredients_input = []
    for ing in r["ingredients"]:
        raw = ing["text"]
        ingredients_input.append(
            {
                "sectionName": ing["section"][:150] if ing["section"] else None,
                "displayName": raw[:200],
                "originalRawText": raw[:500],
                "isOptional": "(optional)" in raw.lower(),
            }
        )

    steps_input = []
    for i, step_text in enumerate(r["steps"]):
        clean_step = re.sub(r"^\d+\.\s*", "", step_text)
        steps_input.append({"stepNumber": i + 1, "instruction": clean_step[:4000]})

    desc = r["description"] or ""

    return {
        "title": r["title"][:200],
        "description": desc[:2000] if desc else None,
        "status": "Published",
        "baseServings": r["base_servings"],
        "yieldDisplay": r["yield"][:100] if r["yield"] else None,
        "sourceName": BOOK_AUTHOR,
        "sourceAuthor": BOOK_AUTHOR,
        "sourceBookTitle": BOOK_TITLE,
        "sourceBookPage": r["page"],
        "ingredients": ingredients_input,
        "steps": steps_input,
        "collectionIds": [collection_id] if collection_id else [],
        "_chapter": r["chapter"],
        "_imagePathInZip": r["image_path"],
    }


def normalize_api_base(api_url):
    """Ensure API URL ends with /api."""
    trimmed = api_url.rstrip("/")
    if not trimmed.lower().endswith("/api"):
        trimmed += "/api"
    return trimmed


def api_json_request(method, url, token=None, payload_obj=None):
    """Helper for authenticated JSON HTTP requests."""
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"

    data = None
    if payload_obj is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload_obj).encode("utf-8")

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req) as resp:
        raw = resp.read().decode("utf-8")
        return json.loads(raw) if raw else None


def authenticate_api(api_base, email, password):
    """Login to LocalRoast and obtain a JWT Bearer token."""
    login_url = f"{api_base}/auth/login"
    try:
        data = api_json_request("POST", login_url, payload_obj={"email": email, "password": password})
        token = (data or {}).get("token") or (data or {}).get("accessToken")
        if not token:
            raise ValueError("No token in login response.")
        print("Successfully authenticated with LocalRoast API.")
        return token
    except urllib.error.HTTPError as e:
        err = e.read().decode("utf-8", errors="ignore")
        raise RuntimeError(f"Authentication failed ({e.code}): {err}")


def inspect_llm_settings(api_base, token):
    """Fetch and display active LLM settings so the user can see which model will run."""
    try:
        settings = api_json_request("GET", f"{api_base}/llm-settings", token=token)
        if settings:
            provider = settings.get("provider", "Unknown")
            base_url = settings.get("baseUrl", "")
            text_model = settings.get("textModel")
            print(f"LLM Queue Config: Provider={provider} ({base_url}), TextModel={text_model or 'NOT SET'}")
            if not text_model:
                print(
                    "  [!] Warning: No Text Model is selected in LLM Settings yet. "
                    "Jobs will queue safely and pause in WaitingForLlmHost until you select a Text Model."
                )
    except Exception as ex:
        print(f"  (Notice: could not query /api/llm-settings: {ex})")


def ensure_book_collection(api_base, token, collection_name=BOOK_TITLE):
    """Ensure a single LocalRoast Collection exists for the cookbook (by default BOOK_TITLE)."""
    clean_name = (collection_name or BOOK_TITLE).strip()[:100]
    if not clean_name:
        return None

    existing = api_json_request("GET", f"{api_base}/collections", token=token) or []
    by_norm = {
        c["name"].strip().lower(): c["id"]
        for c in existing
        if c.get("name") and c.get("id")
    }

    norm = clean_name.lower()
    if norm in by_norm:
        print(f"Reusing existing collection: '{clean_name}'")
        return by_norm[norm]

    created = api_json_request(
        "POST",
        f"{api_base}/collections",
        token=token,
        payload_obj={
            "name": clean_name,
            "description": f"Recipes imported from {BOOK_TITLE} by {BOOK_AUTHOR}",
            "sortOrder": 1,
        },
    )
    if created and created.get("id"):
        print(f"Created cookbook collection: '{clean_name}'")
        return created["id"]

    return None


def upload_cover_image(api_base, token, zip_handle, image_inner_path):
    """Extract an image from an open EPUB zip handle and upload to /api/media/recipe-cover."""
    if not image_inner_path or zip_handle is None:
        return None
    try:
        if image_inner_path not in zip_handle.namelist():
            return None
        img_bytes = zip_handle.read(image_inner_path)

        boundary = "----WebKitFormBoundaryLocalRoastImport777"
        filename = os.path.basename(image_inner_path)
        ext = os.path.splitext(filename)[1].lower()
        mime = "image/png" if ext == ".png" else "image/webp" if ext == ".webp" else "image/jpeg"

        body = io.BytesIO()
        body.write(f"--{boundary}\r\n".encode("utf-8"))
        body.write(
            f'Content-Disposition: form-data; name="file"; filename="{filename}"\r\n'.encode(
                "utf-8"
            )
        )
        body.write(f"Content-Type: {mime}\r\n\r\n".encode("utf-8"))
        body.write(img_bytes)
        body.write(f"\r\n--{boundary}--\r\n".encode("utf-8"))

        media_url = f"{api_base}/media/recipe-cover"
        req = urllib.request.Request(
            media_url,
            data=body.getvalue(),
            headers={
                "Authorization": f"Bearer {token}",
                "Content-Type": f"multipart/form-data; boundary={boundary}",
            },
            method="POST",
        )
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode("utf-8"))  # { coverImagePath, thumbnailImagePath }
    except Exception as ex:
        print(f"    (Notice: image upload failed for {image_inner_path}: {ex})")
        return None


def enqueue_recipe_to_llm_queue(
    api_base,
    token,
    recipe_dict,
    collection_id=None,
    cover_image_path=None,
    thumbnail_image_path=None,
    auto_publish=True,
):
    """Enqueue a parsed recipe into LocalRoast POST /api/ingestion/jobs/text for LLM extraction."""
    raw_text = format_recipe_as_llm_raw_text(recipe_dict)
    payload = {
        "rawText": raw_text,
        "titleHint": recipe_dict["title"][:200],
        "sourceName": BOOK_AUTHOR,
        "sourceAuthor": BOOK_AUTHOR,
        "sourceBookTitle": BOOK_TITLE,
        "sourceBookPage": recipe_dict["page"],
        "coverImagePath": cover_image_path,
        "thumbnailImagePath": thumbnail_image_path,
        "collectionIds": [collection_id] if collection_id else [],
        "autoPublish": auto_publish,
    }
    return api_json_request("POST", f"{api_base}/ingestion/jobs/text", token=token, payload_obj=payload)


def post_recipe_directly_to_api(api_base, token, recipe_payload):
    """Fallback direct creation to LocalRoast POST /api/recipes (skips LLM queue)."""
    clean_payload = {k: v for k, v in recipe_payload.items() if not k.startswith("_")}
    return api_json_request("POST", f"{api_base}/recipes", token=token, payload_obj=clean_payload)


def main():
    parser = argparse.ArgumentParser(
        description="Extract and enqueue America's Test Kitchen EPUB into LocalRoast's LLM ingestion queue."
    )
    parser.add_argument(
        "--epub",
        default=DEFAULT_EPUB_PATH,
        help="Path to EPUB file (default: Calibre ATK directory)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Parse and display summary stats and sample LLM payload without calling the API.",
    )
    parser.add_argument(
        "--chapter",
        type=str,
        default=None,
        help="Filter to a specific chapter number or keyword (e.g. '5' or 'Pasta').",
    )
    parser.add_argument(
        "--limit", type=int, default=None, help="Limit number of recipes to process."
    )
    parser.add_argument(
        "--offset", type=int, default=0, help="Skip the first N matched recipes (useful for resuming)."
    )
    parser.add_argument("--output-json", type=str, default=None, help="Export parsed recipes to a JSON file.")
    parser.add_argument(
        "--enqueue-llm",
        action="store_true",
        default=True,
        help="Enqueue recipes into LocalRoast's LLM Ingestion Queue (default mode).",
    )
    parser.add_argument(
        "--direct-import",
        action="store_true",
        help="Bypass the LLM queue and POST raw unparsed strings directly to /api/recipes.",
    )
    parser.add_argument(
        "--import-api",
        action="store_true",
        help="Trigger API import (also triggered automatically when --email/--username or --token is provided).",
    )
    parser.add_argument(
        "--collection",
        type=str,
        default=BOOK_TITLE,
        help=f"Collection name for imported recipes (default: '{BOOK_TITLE}').",
    )
    parser.add_argument(
        "--auto-publish",
        action="store_true",
        help="Force per-job auto-publishing even if the Auto-Publish toggle in LLM Settings is off.",
    )
    parser.add_argument(
        "--review-only",
        action="store_true",
        help="Hold LLM-extracted recipes in ReadyForReview (default unless --auto-publish or LLM Settings Auto-Publish is enabled).",
    )
    parser.add_argument("--url", default="http://localhost:5041", help="LocalRoast base URL")
    parser.add_argument("--email", "--username", dest="email", help="LocalRoast account email")
    parser.add_argument("--password", help="LocalRoast password")
    parser.add_argument("--token", help="Pre-existing JWT or PAT token (optional)")
    parser.add_argument(
        "--no-images",
        action="store_true",
        help="Skip uploading matched EPUB dish cover photos.",
    )
    parser.add_argument(
        "--upload-images",
        action="store_true",
        help="Upload matched dish photos (enabled by default during API import).",
    )

    args = parser.parse_args()

    # 1. Parse EPUB
    recipes = parse_atk_epub(args.epub)

    # Filter chapter if requested
    if args.chapter:
        ch_filter = args.chapter.lower()
        recipes = [
            r
            for r in recipes
            if ch_filter in r["chapter"].lower() or f"chapter {ch_filter}" in r["chapter"].lower()
        ]
        print(f"Filtered to chapter '{args.chapter}': {len(recipes)} recipes remaining.")

    # Apply offset and limit if requested
    if args.offset and args.offset > 0:
        recipes = recipes[args.offset :]
        print(f"Skipped first {args.offset} recipes ({len(recipes)} remaining).")

    if args.limit:
        recipes = recipes[: args.limit]
        print(f"Limited to {len(recipes)} recipes.")

    should_call_api = bool(args.import_api or args.email or args.token) and not args.dry_run

    # 2. Dry Run display
    if args.dry_run or (not args.output_json and not should_call_api):
        print("\n" + "=" * 65)
        print("EPUB EXTRACTION DRY-RUN SUMMARY (DEFAULT MODE: LLM QUEUE)")
        print("=" * 65)
        print(f"Total recipes selected: {len(recipes)}")

        chapter_counts = {}
        for r in recipes:
            chapter_counts[r["chapter"]] = chapter_counts.get(r["chapter"], 0) + 1

        print("\nBreakdown by Chapter:")
        for ch, count in chapter_counts.items():
            print(f"  • {ch:45} : {count:3} recipes")

        images_count = sum(1 for r in recipes if r["image_path"])
        print(f"\nDish photos matched: {images_count} of {len(recipes)} recipes")

        print("\n--- SAMPLE LLM QUEUE PAYLOAD (First in selection) ---")
        if recipes:
            sample_raw = format_recipe_as_llm_raw_text(recipes[0])
            preview_lines = sample_raw.splitlines()[:22]
            for ln in preview_lines:
                print(f"  {ln}")
            if len(sample_raw.splitlines()) > 22:
                print(f"  ... ({len(sample_raw.splitlines()) - 22} more lines)")

        print("=" * 65)
        if not args.output_json and not should_call_api:
            print("\nTip: Enqueue a test batch of 10 recipes into the LLM Ingestion Queue with:")
            print("     python scripts/import_atk_epub.py --email <email> --password <pass> --limit 10")
        return

    # 3. Export to JSON
    if args.output_json:
        formatted_list = [format_recipe_for_localroast(r) for r in recipes]
        with open(args.output_json, "w", encoding="utf-8") as f:
            json.dump(formatted_list, f, indent=2, ensure_ascii=False)
        print(f"\nSuccessfully exported {len(formatted_list)} recipes to {args.output_json}!")

    # 4. Import / Enqueue into LocalRoast API (LLM Queue is default)
    if should_call_api:
        api_base = normalize_api_base(args.url)
        token = args.token
        if not token:
            if not args.email or not args.password:
                print("Error: --email and --password (or --token) are required for API import.")
                sys.exit(1)
            token = authenticate_api(api_base, args.email, args.password)

        use_llm_queue = not args.direct_import
        upload_images = not args.no_images
        auto_publish = bool(args.auto_publish) and not args.review_only

        if use_llm_queue:
            inspect_llm_settings(api_base, token)
            mode_desc = (
                "LLM Queue (Force Auto-Publish ON)"
                if auto_publish
                else "LLM Queue (Follows LLM Settings Auto-Publish Toggle)"
            )
        else:
            mode_desc = "Direct Recipe Creation (Bypassing LLM)"

        book_collection_id = ensure_book_collection(api_base, token, args.collection)

        print(f"\nStarting {mode_desc} for {len(recipes)} recipe(s) -> {api_base}...")
        success_count = 0
        error_count = 0

        with zipfile.ZipFile(args.epub, "r") as epub_zip:
            for i, r in enumerate(recipes):
                title = r["title"][:200]
                col_id = book_collection_id
                action_verb = "Queueing" if use_llm_queue else "Importing"
                print(f"[{i+1}/{len(recipes)}] {action_verb}: {title}...", end="", flush=True)

                cover_path = None
                thumb_path = None
                if upload_images and r["image_path"]:
                    uploaded = upload_cover_image(api_base, token, epub_zip, r["image_path"])
                    if uploaded:
                        cover_path = uploaded.get("coverImagePath")
                        thumb_path = uploaded.get("thumbnailImagePath")

                try:
                    if use_llm_queue:
                        job = enqueue_recipe_to_llm_queue(
                            api_base,
                            token,
                            r,
                            collection_id=col_id,
                            cover_image_path=cover_path,
                            thumbnail_image_path=thumb_path,
                            auto_publish=auto_publish,
                        )
                        success_count += 1
                        print(f" Queued! (Job ID: {job.get('id')})")
                    else:
                        payload = format_recipe_for_localroast(r, collection_id=col_id)
                        if cover_path:
                            payload["coverImagePath"] = cover_path
                            payload["thumbnailImagePath"] = thumb_path
                        created = post_recipe_directly_to_api(api_base, token, payload)
                        success_count += 1
                        print(f" Created! (Recipe ID: {created.get('id')})")
                except urllib.error.HTTPError as http_ex:
                    error_count += 1
                    err_body = http_ex.read().decode("utf-8", errors="ignore")
                    print(f" FAILED ({http_ex.code}): {err_body}")
                except Exception as ex:
                    error_count += 1
                    print(f" FAILED: {ex}")

        print(f"\nFinished! {success_count} succeeded, {error_count} failed.")
        if use_llm_queue and success_count > 0:
            print(
                "Background worker is now extracting recipes sequentially, reusing and creating "
                "canonical FoodItems and Tags as each recipe completes."
            )


if __name__ == "__main__":
    main()
