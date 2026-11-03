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

  # 3. Export all 1,600+ recipes to a structured JSON file:
  python scripts/import_atk_epub.py --output-json atk_recipes.json

  # 4. Import directly into LocalRoast API (e.g. test with 10 recipes first):
  python scripts/import_atk_epub.py --import-api --url http://localhost:5041 --username your@email.com --password yourpassword --limit 10

  # 5. Import full book with photos:
  python scripts/import_atk_epub.py --import-api --url http://localhost:5041 --username your@email.com --password yourpassword --upload-images
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


def format_recipe_for_localroast(r):
    """Format an extracted recipe dictionary into LocalRoast UpsertRecipeRequest structure."""
    # Build ingredients
    ingredients_input = []
    for ing in r["ingredients"]:
        raw = ing["text"]
        ingredients_input.append(
            {
                "sectionName": ing["section"],
                "displayName": raw,
                "originalRawText": raw,
                "isOptional": "(optional)" in raw.lower(),
            }
        )

    # Build steps
    steps_input = []
    for i, step_text in enumerate(r["steps"]):
        # Strip leading number if present (e.g. '1. ')
        clean_step = re.sub(r"^\d+\.\s*", "", step_text)
        steps_input.append({"stepNumber": i + 1, "instruction": clean_step})

    # Clean description
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
        "collectionNames": [r["chapter"]],
        "tagNames": ["America's Test Kitchen", "Cookbook"],
        "_imagePathInZip": r["image_path"],
    }


def authenticate_api(api_url, username, password):
    """Login to LocalRoast and obtain a JWT Bearer token."""
    login_url = f"{api_url.rstrip('/')}/auth/login"
    payload = json.dumps({"usernameOrEmail": username, "password": password}).encode("utf-8")
    req = urllib.request.Request(
        login_url, data=payload, headers={"Content-Type": "application/json"}
    )
    try:
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            token = data.get("token") or data.get("accessToken")
            if not token:
                raise ValueError("No token in login response.")
            print("Successfully authenticated with LocalRoast API.")
            return token
    except urllib.error.HTTPError as e:
        err = e.read().decode("utf-8", errors="ignore")
        raise RuntimeError(f"Authentication failed ({e.code}): {err}")


def upload_cover_image(api_url, token, zip_path, image_inner_path):
    """Extract an image from EPUB zip and upload to /media/recipe-cover."""
    if not image_inner_path:
        return None
    try:
        with zipfile.ZipFile(zip_path, "r") as z:
            if image_inner_path not in z.namelist():
                return None
            img_bytes = z.read(image_inner_path)

        boundary = "----WebKitFormBoundaryLocalRoastImport777"
        filename = os.path.basename(image_inner_path)

        body = io.BytesIO()
        body.write(f"--{boundary}\r\n".encode("utf-8"))
        body.write(
            f'Content-Disposition: form-data; name="file"; filename="{filename}"\r\n'.encode(
                "utf-8"
            )
        )
        body.write(b"Content-Type: image/jpeg\r\n\r\n")
        body.write(img_bytes)
        body.write(f"\r\n--{boundary}--\r\n".encode("utf-8"))

        media_url = f"{api_url.rstrip('/')}/media/recipe-cover"
        req = urllib.request.Request(
            media_url,
            data=body.getvalue(),
            headers={
                "Authorization": f"Bearer {token}",
                "Content-Type": f"multipart/form-data; boundary={boundary}",
            },
        )
        with urllib.request.urlopen(req) as resp:
            res_data = json.loads(resp.read().decode("utf-8"))
            return res_data  # { coverUrlPath, thumbnailUrlPath, width, height }
    except Exception as ex:
        print(f"    (Notice: image upload failed for {image_inner_path}: {ex})")
        return None


def post_recipe_to_api(api_url, token, recipe_payload):
    """Post structured recipe to LocalRoast POST /recipes."""
    url = f"{api_url.rstrip('/')}/recipes"
    # Remove internal metadata key
    clean_payload = {k: v for k, v in recipe_payload.items() if not k.startswith("_")}
    payload_bytes = json.dumps(clean_payload).encode("utf-8")

    req = urllib.request.Request(
        url,
        data=payload_bytes,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main():
    parser = argparse.ArgumentParser(
        description="Extract and import America's Test Kitchen EPUB into LocalRoast."
    )
    parser.add_argument(
        "--epub",
        default=DEFAULT_EPUB_PATH,
        help="Path to EPUB file (default: Calibre ATK directory)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Parse and display summary stats and sample recipes without saving.",
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
    parser.add_argument("--output-json", type=str, default=None, help="Export parsed recipes to a JSON file.")
    parser.add_argument(
        "--import-api",
        action="store_true",
        help="Import recipes directly into LocalRoast via REST API.",
    )
    parser.add_argument("--url", default="http://localhost:5041", help="LocalRoast API base URL")
    parser.add_argument("--username", help="LocalRoast username/email")
    parser.add_argument("--password", help="LocalRoast password")
    parser.add_argument("--token", help="Pre-existing JWT token (optional)")
    parser.add_argument(
        "--upload-images",
        action="store_true",
        help="Upload matched dish photos to LocalRoast media storage.",
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

    # Apply limit if requested
    if args.limit:
        recipes = recipes[: args.limit]
        print(f"Limited to first {args.limit} recipes.")

    # 2. Dry Run display
    if args.dry_run or (not args.output_json and not args.import_api):
        print("\n" + "=" * 65)
        print("EPUB EXTRACTION DRY-RUN SUMMARY")
        print("=" * 65)
        print(f"Total recipes selected: {len(recipes)}")

        # Breakdown by chapter
        chapter_counts = {}
        for r in recipes:
            chapter_counts[r["chapter"]] = chapter_counts.get(r["chapter"], 0) + 1

        print("\nBreakdown by Chapter:")
        for ch, count in chapter_counts.items():
            print(f"  • {ch:45} : {count:3} recipes")

        images_count = sum(1 for r in recipes if r["image_path"])
        print(f"\nDish photos matched: {images_count} of {len(recipes)} recipes")

        print("\n--- SAMPLE RECIPE (First in selection) ---")
        if recipes:
            sample = format_recipe_for_localroast(recipes[0])
            print(f"Title:        {sample['title']}")
            print(f"Chapter:      {recipes[0]['chapter']}")
            print(f"Yield:        {sample['yieldDisplay']} (Servings: {sample['baseServings']})")
            print(f"Page:         {sample['sourceBookPage']}")
            print(
                f"Image:        {recipes[0]['image_path'] if recipes[0]['image_path'] else 'None'}"
            )
            print(f"Ingredients ({len(sample['ingredients'])}):")
            for ing in sample["ingredients"][:6]:
                sec = f"[{ing['sectionName']}] " if ing["sectionName"] else ""
                print(f"  - {sec}{ing['displayName']}")
            if len(sample["ingredients"]) > 6:
                print(f"  ... and {len(sample['ingredients']) - 6} more")
            print(f"Steps ({len(sample['steps'])}):")
            for st in sample["steps"][:2]:
                print(f"  Step {st['stepNumber']}: {st['instruction'][:100]}...")
            if len(sample["steps"]) > 2:
                print(f"  ... and {len(sample['steps']) - 2} more steps")

        print("=" * 65)
        if not args.output_json and not args.import_api:
            print("\nTip: Run with --output-json atk_recipes.json to export,")
            print("     or with --import-api --username <email> --password <pass> to import directly.")
        return

    # 3. Export to JSON
    if args.output_json:
        formatted_list = [format_recipe_for_localroast(r) for r in recipes]
        with open(args.output_json, "w", encoding="utf-8") as f:
            json.dump(formatted_list, f, indent=2, ensure_ascii=False)
        print(f"\nSuccessfully exported {len(formatted_list)} recipes to {args.output_json}!")

    # 4. Import directly to API
    if args.import_api:
        token = args.token
        if not token:
            if not args.username or not args.password:
                print("Error: --username and --password are required for --import-api.")
                sys.exit(1)
            token = authenticate_api(args.url, args.username, args.password)

        print(f"\nStarting API import of {len(recipes)} recipes to {args.url}...")
        success_count = 0
        error_count = 0

        for i, r in enumerate(recipes):
            payload = format_recipe_for_localroast(r)
            title = payload["title"]
            print(f"[{i+1}/{len(recipes)}] Importing: {title}...", end="", flush=True)

            # Upload image if available and requested
            if args.upload_images and r["image_path"]:
                uploaded = upload_cover_image(args.url, token, args.epub, r["image_path"])
                if uploaded:
                    payload["coverImagePath"] = uploaded.get("coverUrlPath")
                    payload["thumbnailImagePath"] = uploaded.get("thumbnailUrlPath")

            try:
                created = post_recipe_to_api(args.url, token, payload)
                success_count += 1
                print(f" Done! (ID: {created.get('id')})")
            except Exception as ex:
                error_count += 1
                print(f" FAILED: {ex}")

        print(f"\nImport finished! {success_count} succeeded, {error_count} failed.")


if __name__ == "__main__":
    main()
