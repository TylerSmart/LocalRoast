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
- Sub-recipes & recipe variations extracted as standalone recipes with inherited parent base ingredients
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
from collections import defaultdict
import difflib
import heapq
import io
import json
import os
import re
import sys
import unicodedata
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


SKIP_XHTML_PARTS = (
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
)


def _norm_recipe_key(text, strip_leading_the=False):
    key = re.sub(r"[^a-z0-9]", "", (text or "").lower())
    if strip_leading_the and key.startswith("the") and len(key) > 5:
        key = key[3:]
    return key


def norm_dedupe_title(text):
    nfd = unicodedata.normalize("NFD", (text or "").replace("\u2019", "'").replace("\u2018", "'"))
    stripped = "".join(ch for ch in nfd if unicodedata.category(ch) != "Mn")
    return " ".join(stripped.lower().split())[:200]


def _hyphen_space_lower(text):
    return re.sub(r"\s+", " ", re.sub(r"[-–—]", " ", (text or "").lower())).strip()


def _significant_words(text):
    stop_words = {
        "with", "and", "for", "from", "into", "over", "style", "easy", "quick",
        "best", "classic", "foolproof", "simple", "basic", "make", "ahead",
        "optional", "recipe", "recipes", "follow", "follows", "page", "this",
    }
    return {
        w for w in re.findall(r"[a-z]{4,}", (text or "").lower())
        if w not in stop_words
    }


def _should_skip_epub_xhtml(filename):
    if not filename.startswith("OEBPS/xhtml/") or not filename.endswith(".xhtml"):
        return True
    base = filename.replace("OEBPS/xhtml/", "")
    return any(skip in base for skip in SKIP_XHTML_PARTS)


def _element_heading_flags(elem, ns_x):
    tag = elem.tag.replace("{http://www.w3.org/1999/xhtml}", "")
    cls = elem.get("class", "")
    h2 = elem if (tag == "h2" and "recipe_title" in cls) else elem.find(".//x:h2[@class]", ns_x)
    h3 = elem if (tag == "h3" and "subrecipe_title" in cls) else elem.find(".//x:h3[@class]", ns_x)
    is_h2 = h2 is not None and "recipe_title" in h2.get("class", "")
    is_h3 = h3 is not None and "subrecipe_title" in h3.get("class", "")
    return tag, cls, h2, h3, is_h2, is_h3


def _build_id_to_recipe_target(z, ns_x):
    """Index XHTML element IDs to their owning recipe title and whether the block is a technique sidebar."""
    id_to_recipe_target = {}

    for filename in sorted(z.namelist()):
        if _should_skip_epub_xhtml(filename):
            continue
        base = filename.replace("OEBPS/xhtml/", "")
        root = ET.fromstring(z.read(filename))
        body = root.find("x:body", ns_x)
        if body is None:
            continue
        children = body[0] if (len(body) == 1 and body[0].tag.endswith("div")) else body

        cur_title = None
        parent_master_title = None
        parent_has_content = False

        for elem in children:
            tag, cls, h2, h3, is_h2, is_h3 = _element_heading_flags(elem, ns_x)

            h3_head = (
                elem
                if (tag == "h3" and "head" in cls and "subrecipe_title" not in cls)
                else elem.find(".//x:h3[@class]", ns_x)
            )
            is_sidebar_elem = "recipe_note" in cls or (
                h3_head is not None
                and "head" in h3_head.get("class", "")
                and "subrecipe_title" not in h3_head.get("class", "")
                and not is_h2
                and not is_h3
            )

            if is_h2 or is_h3:
                t_elem = h2 if is_h2 else h3
                title = extract_node_text(t_elem)
                if (
                    is_h3
                    and cur_title is not None
                    and re.match(
                        r"^(Day\s+(One|Two|Three|Four|Five|\d+)|Stage\s+\d+|Part\s+\d+)",
                        title,
                        re.IGNORECASE,
                    )
                ):
                    pass
                elif (
                    is_h3
                    and parent_master_title is not None
                    and not parent_has_content
                    and title.lower() in parent_master_title.lower()
                ):
                    cur_title = parent_master_title
                else:
                    cur_title = title
                    if is_h2:
                        parent_master_title = title
                        parent_has_content = False
            elif cur_title == parent_master_title and (
                "ing_list" in cls or "method" in cls or "indent" in cls
            ):
                parent_has_content = True

            if cur_title:
                entry = {"title": cur_title, "is_sidebar": is_sidebar_elem}
                eid = elem.get("id")
                if eid:
                    id_to_recipe_target[f"{base}#{eid}"] = entry
                    id_to_recipe_target.setdefault(eid, entry)
                for sub in elem.findall(".//*[@id]"):
                    sid = sub.get("id")
                    if sid:
                        sub_cls = sub.get("class", "")
                        sub_is_sidebar = is_sidebar_elem or (
                            sub.tag.endswith("h3")
                            and "head" in sub_cls
                            and "subrecipe_title" not in sub_cls
                        )
                        sub_entry = {"title": cur_title, "is_sidebar": sub_is_sidebar}
                        id_to_recipe_target[f"{base}#{sid}"] = sub_entry
                        id_to_recipe_target.setdefault(sid, sub_entry)

    return id_to_recipe_target


def extract_ingredient_links_and_text(
    p, base_xhtml, current_recipe_title, id_to_recipe_target, ns_x
):
    """Extract ingredient text and resolve EPUB <a href> cross-references to target recipe titles."""
    ptext = extract_node_text(p)
    if not ptext:
        return "", []

    low = ptext.lower()
    if "(see this page)" in low or "; see this page)" in low:
        return ptext, []

    linked_titles = []
    seen_titles = set()
    cur_norm = _norm_recipe_key(current_recipe_title)

    for a in p.findall(".//x:a", ns_x):
        href = (a.get("href") or "").strip()
        if not href:
            continue
        frag = href.split("#")[-1] if "#" in href else ""
        before_hash = href.split("#")[0] if "#" in href else href
        file_part = before_hash.split("/")[-1] if before_hash else base_xhtml
        if not frag:
            continue
        target = id_to_recipe_target.get(f"{file_part}#{frag}") or id_to_recipe_target.get(frag)
        if not target or target.get("is_sidebar"):
            continue
        t_title = target["title"]
        if _norm_recipe_key(t_title) == cur_norm:
            continue
        if t_title not in seen_titles:
            seen_titles.add(t_title)
            linked_titles.append(t_title)

    if linked_titles:
        primary = linked_titles[0]
        text_norm = _norm_recipe_key(ptext)
        prim_norm = _norm_recipe_key(primary, strip_leading_the=True)

        m = re.match(
            r"^(\s*(?:\d+[\d\s/\.]*\s+)?(?:recipes?|batches?)\s+)(.+?)\s*\(([^()]*\bthis page\b[^()]*)\)(.*)$",
            ptext,
            flags=re.IGNORECASE,
        )
        if prim_norm and prim_norm not in text_norm:
            if m:
                prefix, raw_ref, paren_inner, suffix = m.groups()
                raw_ref_clean = re.sub(
                    r"\s*\(\s*optional\s*\)\s*$", "", raw_ref, flags=re.IGNORECASE
                ).strip()
                notes = []
                if "optional" in paren_inner.lower() or "(optional)" in raw_ref.lower():
                    notes.append("optional")
                all_words = set(re.findall(r"[a-z]{4,}", raw_ref_clean.lower()))
                prim_low = primary.lower()
                if all_words and not all(w in prim_low for w in all_words):
                    notes.append(raw_ref_clean)
                notes.append("this page")
                if len(linked_titles) > 1:
                    notes.append(f"or see {'; '.join(linked_titles[1:])}")
                ptext = f"{prefix}{primary} ({'; '.join(notes)}){suffix}".strip()
            else:
                ptext = re.sub(
                    r"\(([^()]*)\bthis page\b([^()]*)\)",
                    lambda pm: (
                        f"({pm.group(1)}this page; see {'; or see '.join(linked_titles)}{pm.group(2)})"
                    ),
                    ptext,
                    count=1,
                    flags=re.IGNORECASE,
                )

    return ptext, linked_titles


def _resolve_sibling_subrecipe_references(final_recipes):
    """Link standalone sibling component sub-recipes referenced by title or generic cues."""
    groups_by_master = {}
    master_order = []
    variations_by_master = {}

    for r in final_recipes:
        if r.get("type") == "master":
            mid = id(r)
            groups_by_master[mid] = {"master": r, "subs": []}
            variations_by_master[mid] = []
            master_order.append(mid)
        elif r.get("type") == "sub":
            parent = r.get("source_parent_recipe")
            if parent is not None and id(parent) in groups_by_master:
                groups_by_master[id(parent)]["subs"].append(r)
                if r.get("parent_recipe_title"):
                    variations_by_master[id(parent)].append(r)

    all_norm_titles = set()
    for r in final_recipes:
        n1 = _norm_recipe_key(r["title"], strip_leading_the=False)
        n2 = _norm_recipe_key(r["title"], strip_leading_the=True)
        stripped = re.sub(r"\s*\([^()]+\)\s*$", "", r["title"]).strip()
        n3 = _norm_recipe_key(stripped, strip_leading_the=True)
        for n in (n1, n2, n3):
            if n:
                all_norm_titles.add(n)

    generic_recipe_re = re.compile(
        r"^(\s*(?:\d+[\d\s/\.]*\s+)?(?:recipes?|batches?)\s+)([^,(;]+)(.*)$",
        re.IGNORECASE,
    )
    generic_qty_follows_re = re.compile(
        r"^(\s*(?:\d+[\d\s/\.]*\s+)?(?:cups?|tablespoons?|teaspoons?|ounces?|pounds?)\s+)([^,(;]+?)(\s*\((?:optional[;\s]*)?recipes?\s+follows?\b.*)$",
        re.IGNORECASE,
    )

    for mid in master_order:
        grp = groups_by_master[mid]
        master = grp["master"]
        subs = grp["subs"]
        component_subs = [s for s in subs if not s.get("parent_recipe_title")]
        if not component_subs:
            continue

        all_group_recipes = [master] + subs
        for src in all_group_recipes:
            src_is_variation = bool(src.get("parent_recipe_title"))
            for ing in src.get("ingredients", []):
                ing_low = _hyphen_space_lower(ing.get("text", ""))
                links = ing.setdefault("linked_recipes", [])
                for comp in component_subs:
                    if comp is src:
                        continue
                    if src_is_variation and any(
                        _hyphen_space_lower(src["title"]) in _hyphen_space_lower(ci.get("text", ""))
                        or src["title"] in ci.get("linked_recipes", [])
                        for ci in comp.get("ingredients", [])
                    ):
                        continue
                    ctitle = comp["title"]
                    c_stripped = re.sub(r"\s*\([^()]+\)\s*$", "", ctitle).strip()
                    candidates_low = [_hyphen_space_lower(ctitle)]
                    if (
                        len(c_stripped) >= 5
                        and _hyphen_space_lower(c_stripped) != candidates_low[0]
                    ):
                        candidates_low.append(_hyphen_space_lower(c_stripped))
                    if any(
                        cand
                        and re.search(rf"(?<![a-z0-9]){re.escape(cand)}(?![a-z0-9])", ing_low)
                        for cand in candidates_low
                    ):
                        if ctitle not in links:
                            links.append(ctitle)

        claimed_titles = {
            lt
            for ing in master.get("ingredients", [])
            for lt in ing.get("linked_recipes", [])
        }
        unclaimed_subs = [s for s in component_subs if s["title"] not in claimed_titles]

        for idx, ing in enumerate(master.get("ingredients", [])):
            if ing.get("linked_recipes"):
                continue
            text = ing.get("text", "")
            if re.search(r"\bor\s+(?:\d+[\d\s/\.]*\s+)?(?:recipes?|batches?)\b", text, re.IGNORECASE):
                continue

            m_gen = generic_recipe_re.match(text) or generic_qty_follows_re.match(text)
            if not m_gen:
                continue

            prefix, raw_cand, suffix = m_gen.groups()
            raw_cand_clean = re.sub(
                r"\s*\(\s*optional\s*\)\s*$", "", raw_cand, flags=re.IGNORECASE
            ).strip()
            raw_cand_norm = _norm_recipe_key(raw_cand_clean, strip_leading_the=True)
            if raw_cand_norm in all_norm_titles:
                continue

            pool = unclaimed_subs if unclaimed_subs else component_subs
            cand_words = _significant_words(raw_cand_clean)
            matched_subs = [
                s for s in pool if cand_words and (cand_words & _significant_words(s["title"]))
            ]
            has_follows = bool(re.search(r"\brecipes?\s+follows?\b", text, re.IGNORECASE))
            if not matched_subs and has_follows:
                matched_subs = list(pool)

            if matched_subs:
                links = ing.setdefault("linked_recipes", [])
                for ms in matched_subs:
                    if ms["title"] not in links:
                        links.append(ms["title"])
                        if ms in unclaimed_subs:
                            unclaimed_subs.remove(ms)
                primary_title = matched_subs[0]["title"]
                optional_tag = (
                    " (optional)"
                    if "(optional)" in raw_cand.lower() and "(optional" not in suffix.lower()
                    else ""
                )
                suffix_text = suffix
                if (
                    suffix_text
                    and not suffix_text[0].isspace()
                    and suffix_text[0] not in ",;.:"
                ):
                    suffix_text = " " + suffix_text
                alt_note = ""
                if len(matched_subs) > 1:
                    alt_note = f" (or see {'; '.join(s['title'] for s in matched_subs[1:])})"
                rewritten = (
                    f"{prefix}{primary_title}{optional_tag}{suffix_text}{alt_note}".strip()
                )
                ing["text"] = rewritten

                for var in variations_by_master.get(mid, []):
                    if idx < len(var.get("ingredients", [])) and var["ingredients"][idx].get("text") == text:
                        var["ingredients"][idx]["text"] = rewritten
                        var_links = var["ingredients"][idx].setdefault("linked_recipes", [])
                        for ms in matched_subs:
                            if ms["title"] not in var_links:
                                var_links.append(ms["title"])


def _norm_ingredient_sig(recipe):
    """Return a normalized tuple of ingredient lines for comparing duplicate recipes."""
    return tuple(
        re.sub(r"[^a-z0-9/]+", " ", _hyphen_space_lower(ing.get("text", ""))).strip()
        for ing in recipe.get("ingredients", [])
    )


def _norm_steps_text(recipe):
    """Return normalized concatenated step text for duplicate-recipe similarity comparison."""
    joined = " ".join(
        re.sub(r"^(\[[^\]]+\]\s*)?\d+\.\s*", "", step)
        for step in recipe.get("steps", [])
    )
    return re.sub(r"[^a-z0-9]+", " ", _hyphen_space_lower(joined)).strip()


def _recipes_are_identical_duplicates(first, second):
    """Return True when two same-titled recipes have identical ingredients and near-identical steps."""
    sig_a = _norm_ingredient_sig(first)
    sig_b = _norm_ingredient_sig(second)
    if sig_a != sig_b:
        return False
    steps_a = _norm_steps_text(first)
    steps_b = _norm_steps_text(second)
    if not sig_a and not steps_a:
        return False
    if steps_a == steps_b:
        return True
    if not steps_a or not steps_b:
        return False
    return difflib.SequenceMatcher(None, steps_a, steps_b).ratio() >= 0.85


def _build_disambiguated_title(recipe, used_keys):
    """Generate a unique, ATK-style disambiguated title (<=200 chars) for a colliding recipe."""
    base_title = recipe["title"].strip()
    parent = recipe.get("source_parent_recipe")
    candidates = []
    if parent and parent.get("title"):
        parent_title = parent["title"].strip()
        if parent_title.lower() not in base_title.lower():
            candidates.append(f"{base_title} for {parent_title}")
    chapter = (recipe.get("chapter") or "").strip()
    if chapter:
        ch_short = re.sub(r"^Chapter\s+\d+\s*:\s*", "", chapter, flags=re.IGNORECASE).strip()
        if ch_short:
            candidates.append(f"{base_title} ({ch_short})")
    if recipe.get("page"):
        candidates.append(f"{base_title} (p. {recipe['page']})")

    for cand in candidates:
        cand_trimmed = cand[:200].strip()
        key = norm_dedupe_title(cand_trimmed)
        if key and key not in used_keys:
            return cand_trimmed

    suffix = 2
    while True:
        tag = f" ({suffix})"
        cand_trimmed = (base_title[: 200 - len(tag)].rstrip() + tag).strip()
        key = norm_dedupe_title(cand_trimmed)
        if key and key not in used_keys:
            return cand_trimmed
        suffix += 1


def _rewrite_group_subrecipe_title(group_recipes, target_recipe, old_title, new_title):
    """Update references within a recipe's master/sibling group when a colliding title is renamed."""
    old_norm = norm_dedupe_title(old_title)
    new_norm = norm_dedupe_title(new_title)
    if not old_norm or old_norm == new_norm:
        return

    tokens = [t for t in re.split(r"[-–—\s]+", old_title.strip()) if t]
    title_re = (
        re.compile(
            r"(?<![A-Za-z0-9])" + r"[-–—\s]+".join(re.escape(t) for t in tokens) + r"(?![A-Za-z0-9])",
            re.IGNORECASE,
        )
        if tokens
        else None
    )

    for member in group_recipes:
        if member is target_recipe:
            continue
        if member.get("parent_recipe_title") and norm_dedupe_title(member["parent_recipe_title"]) == old_norm:
            member["parent_recipe_title"] = new_title
        if member.get("variations"):
            member["variations"] = [
                new_title if norm_dedupe_title(v) == old_norm else v
                for v in member["variations"]
            ]
        for ing in member.get("ingredients", []):
            links = ing.get("linked_recipes")
            if not links:
                continue
            matched = any(norm_dedupe_title(lt) == old_norm for lt in links)
            if not matched:
                continue
            updated_links = []
            for lt in links:
                rep = new_title if norm_dedupe_title(lt) == old_norm else lt
                if rep not in updated_links:
                    updated_links.append(rep)
            ing["linked_recipes"] = updated_links

            text = ing.get("text", "")
            if title_re and new_title.lower() not in text.lower():
                ing["text"] = title_re.sub(lambda _: new_title, text, count=1)


def _dedupe_and_disambiguate_titles(final_recipes):
    """
    Resolve intra-book normalized title collisions:
    - Prune genuinely identical duplicate recipes (e.g., identical Whipped Cream sub-recipes).
    - Disambiguate distinct recipes that share a title (e.g., 'Spice Rub for <Parent>')
      and rewrite references within their master/sibling recipe group to match.
    Returns: (resolved_recipes, pruned_duplicates, disambiguated_titles)
    """
    group_members = defaultdict(list)
    for r in final_recipes:
        root_obj = r.get("source_parent_recipe") or r
        group_members[id(root_obj)].append(r)

    resolved = []
    used_keys = set()
    kept_by_orig_key = defaultdict(list)
    pruned_duplicates = []
    disambiguated_titles = []

    for r in final_recipes:
        orig_title = r["title"].strip()
        orig_key = norm_dedupe_title(orig_title[:200])
        root_obj = r.get("source_parent_recipe") or r
        grp = group_members[id(root_obj)]

        if not orig_key or orig_key not in used_keys:
            if orig_key:
                used_keys.add(orig_key)
                kept_by_orig_key[orig_key].append(r)
            resolved.append(r)
            continue

        matched_existing = next(
            (existing for existing in kept_by_orig_key[orig_key] if _recipes_are_identical_duplicates(existing, r)),
            None,
        )
        if matched_existing is not None:
            if not matched_existing.get("image_path") and r.get("image_path"):
                matched_existing["image_path"] = r["image_path"]
            if norm_dedupe_title(matched_existing["title"]) != orig_key:
                _rewrite_group_subrecipe_title(grp, r, orig_title, matched_existing["title"])
            pruned_duplicates.append((orig_title, matched_existing["title"]))
            continue

        new_title = _build_disambiguated_title(r, used_keys)
        r["title"] = new_title
        new_key = norm_dedupe_title(new_title)
        if new_key:
            used_keys.add(new_key)
        kept_by_orig_key[orig_key].append(r)
        _rewrite_group_subrecipe_title(grp, r, orig_title, new_title)
        disambiguated_titles.append((orig_title, new_title))
        resolved.append(r)

    return resolved, pruned_duplicates, disambiguated_titles


def parse_atk_epub(epub_path):
    """
    Parse the ATK EPUB archive into structured recipe dictionaries.
    Returns: list of parsed recipe dicts
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

        # Step 2b: Index XHTML IDs -> target recipe titles so cross-chapter <a href> links resolve
        id_to_recipe_target = _build_id_to_recipe_target(z, ns_x)

        # Step 3: Parse recipe chapters in reading order
        current_chapter_name = "General"

        for filename in sorted(z.namelist()):
            if _should_skip_epub_xhtml(filename):
                continue
            base = filename.replace("OEBPS/xhtml/", "")

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
            current_step_phase = None

            for elem in children:
                tag, cls, h2, h3, is_h2, is_h3 = _element_heading_flags(elem, ns_x)

                if is_h2 or is_h3:
                    t_elem = h2 if is_h2 else h3
                    t_type = "master" if is_h2 else "sub"
                    t_id = t_elem.get("id", "")
                    title = extract_node_text(t_elem)

                    # 1. Multi-day / multi-stage phase header inside active recipe (e.g. Beef Wellington Day One/Two/Three)
                    if (
                        is_h3
                        and current_recipe is not None
                        and re.match(
                            r"^(Day\s+(One|Two|Three|Four|Five|\d+)|Stage\s+\d+|Part\s+\d+)",
                            title,
                            re.IGNORECASE,
                        )
                    ):
                        current_step_phase = title
                        continue

                    # 2. Master recipe was an umbrella title with no direct ingredients/steps yet,
                    # and the subrecipe title is part of the master title (e.g. Breakfast Tacos: Scrambled Eggs...)
                    if (
                        is_h3
                        and parent_master_recipe is not None
                        and len(parent_master_recipe["ingredients"]) == 0
                        and len(parent_master_recipe["steps"]) == 0
                        and title.lower() in parent_master_recipe["title"].lower()
                    ):
                        current_recipe = parent_master_recipe
                        current_step_phase = None
                        continue

                    current_step_phase = None

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

                    # If subrecipe under an empty master umbrella (e.g. Nontraditional Pestos), inherit master headnote
                    desc_init = None
                    if (
                        is_h3
                        and parent_master_recipe
                        and len(parent_master_recipe["ingredients"]) == 0
                        and len(parent_master_recipe["steps"]) == 0
                    ):
                        desc_init = parent_master_recipe.get("description")

                    new_recipe = {
                        "id": t_id,
                        "type": t_type,
                        "title": title,
                        "page": page_no,
                        "yield": yield_text,
                        "base_servings": base_servings,
                        "chapter": current_chapter_name,
                        "description": desc_init,
                        "ingredients": [],  # list of { section, text, linked_recipes }
                        "steps": [],
                        "variations": [],
                        "image_path": matched_img,
                        "parent_recipe": parent_master_recipe if is_h3 else None,
                        "source_parent_recipe": parent_master_recipe if is_h3 else None,
                    }

                    current_recipe = new_recipe
                    if is_h2:
                        parent_master_recipe = new_recipe
                        recipes.append(new_recipe)
                    else:
                        # Sub-recipe: we will check if it has standalone ingredients or is a variation
                        recipes.append(new_recipe)
                elif current_recipe is not None:
                    if "yield" in cls and not current_recipe["yield"]:
                        ytext = extract_node_text(elem)
                        if ytext:
                            current_recipe["yield"] = ytext
                            current_recipe["base_servings"] = parse_servings_from_yield(ytext)
                    elif "head_note" in cls:
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
                            if "head" in pcls or "section" in pcls:
                                ptext = extract_node_text(p)
                                if ptext:
                                    current_section = ptext
                            else:
                                ptext, linked = extract_ingredient_links_and_text(
                                    p,
                                    base,
                                    current_recipe["title"],
                                    id_to_recipe_target,
                                    ns_x,
                                )
                                if not ptext:
                                    continue
                                current_recipe["ingredients"].append(
                                    {
                                        "section": current_section,
                                        "text": ptext,
                                        "linked_recipes": linked,
                                    }
                                )
                    elif "method" in cls or "indent" in cls:
                        mtext = extract_node_text(elem)
                        if mtext:
                            if current_step_phase:
                                mtext = f"[{current_step_phase}] {mtext}"
                                current_step_phase = None
                            current_recipe["steps"].append(mtext)

    # Post-process: extract zero-ingredient variations as standalone recipes inheriting parent properties & prune empty placeholders
    final_recipes = []
    variation_count = 0
    empty_pruned_count = 0

    for r in recipes:
        if r["type"] == "sub" and len(r["ingredients"]) == 0:
            parent = r.get("parent_recipe")
            # If parent recipe actually references this sub-recipe as a component ingredient
            # (e.g. "Toasted Mung Beans (recipe follows)"), this sub-recipe is NOT a variation
            # inheriting parent's ingredients; it is an independent component technique!
            is_referenced_by_parent = bool(
                parent
                and any(
                    r["title"].lower() in ing.get("text", "").lower()
                    for ing in parent.get("ingredients", [])
                )
            )

            if parent and not is_referenced_by_parent and (len(r["steps"]) > 0 or r.get("description")):
                variation_count += 1
                if not r["yield"] and parent.get("yield"):
                    r["yield"] = parent["yield"]
                    r["base_servings"] = parent.get("base_servings", 4.0)
                elif not r["base_servings"] and parent.get("base_servings"):
                    r["base_servings"] = parent["base_servings"]

                if not r["page"] and parent.get("page"):
                    r["page"] = parent["page"]

                if not r["image_path"] and parent.get("image_path"):
                    r["image_path"] = parent["image_path"]

                r["ingredients"] = [
                    {
                        **ing,
                        "linked_recipes": list(ing.get("linked_recipes", [])),
                    }
                    for ing in parent.get("ingredients", [])
                ]
                r["parent_recipe_title"] = parent["title"]
                r["parent_recipe_id"] = parent.get("id")
                r["parent_steps"] = list(parent.get("steps", []))

                parent["variations"].append(r["title"])

                if not r["description"]:
                    r["description"] = f"Variation of {parent['title']}."

                final_recipes.append(r)
            elif len(r["steps"]) > 0 or r.get("description"):
                # Standalone component technique that was parsed as sub without separate ingredients
                r["parent_recipe"] = None
                r["parent_recipe_title"] = None
                final_recipes.append(r)
            else:
                empty_pruned_count += 1
        elif len(r["ingredients"]) == 0 and len(r["steps"]) == 0:
            empty_pruned_count += 1
        else:
            final_recipes.append(r)

    _resolve_sibling_subrecipe_references(final_recipes)
    final_recipes, pruned_duplicates, disambiguated_titles = _dedupe_and_disambiguate_titles(
        final_recipes
    )

    summary_notes = [
        f"{variation_count} variations extracted as standalone recipes",
        f"{empty_pruned_count} empty placeholders pruned",
    ]
    if pruned_duplicates:
        summary_notes.append(f"{len(pruned_duplicates)} identical duplicate(s) pruned")
    if disambiguated_titles:
        summary_notes.append(f"{len(disambiguated_titles)} duplicate title(s) disambiguated")

    print(f"Parsed {len(final_recipes)} complete recipes ({', '.join(summary_notes)}).")
    for old_t, new_t in disambiguated_titles:
        print(f"  Disambiguated duplicate title: '{old_t}' -> '{new_t}'")
    for dup_t, kept_t in pruned_duplicates:
        print(f"  Pruned identical duplicate: '{dup_t}' (matches '{kept_t}')")
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

    if r.get("parent_recipe_title"):
        lines.append("")
        lines.append(f"Master Recipe: {r['parent_recipe_title']}")
        lines.append("Base Ingredients:")
    else:
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
    if r.get("parent_recipe_title"):
        lines.append(f"Variation Instructions ({r['title']}):")
    else:
        lines.append("Instructions:")
    for i, step_text in enumerate(r["steps"]):
        clean_step = re.sub(r"^(\[[^\]]+\]\s*)?\d+\.\s*", r"\1", step_text)
        lines.append(f"{i + 1}. {clean_step}")

    if r.get("parent_steps"):
        lines.append("")
        lines.append(f"Base Master Instructions (from {r['parent_recipe_title']}):")
        for i, step_text in enumerate(r["parent_steps"]):
            clean_step = re.sub(r"^(\[[^\]]+\]\s*)?\d+\.\s*", r"\1", step_text)
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
        clean_step = re.sub(r"^(\[[^\]]+\]\s*)?\d+\.\s*", r"\1", step_text)
        steps_input.append({"stepNumber": i + 1, "instruction": clean_step[:4000]})

    if r.get("parent_steps"):
        offset = len(steps_input)
        for j, step_text in enumerate(r["parent_steps"]):
            clean_step = re.sub(r"^(\[[^\]]+\]\s*)?\d+\.\s*", r"\1", step_text)
            steps_input.append({
                "stepNumber": offset + j + 1,
                "instruction": f"[{r['parent_recipe_title']} Base Step {j + 1}] {clean_step}"[:4000],
            })

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
    """Ensure API URL ends with /api (stripping any /api/mcp or /mcp suffix)."""
    u = (api_url or "").strip().rstrip("/")
    if not u:
        return "http://localhost:5041/api"
    lower = u.lower()
    if lower.endswith("/api/mcp"):
        u = u[:-4]
    elif lower.endswith("/mcp"):
        u = u[:-4].rstrip("/") + "/api"
    elif not lower.endswith("/api"):
        u += "/api"
    return u


def _strip_bearer(val):
    if not val or not isinstance(val, str):
        return None
    cleaned = re.sub(r"^bearer\s+", "", val.strip(), flags=re.IGNORECASE).strip()
    return cleaned or None


def _extract_from_mcp_entry(entry):
    raw_url = None
    for k in ("url", "serverUrl", "httpUrl", "endpoint"):
        v = entry.get(k)
        if isinstance(v, str) and v.strip():
            raw_url = v.strip()
            break
    args_list = entry.get("args") if isinstance(entry.get("args"), list) else []
    if not raw_url:
        for a in args_list:
            if isinstance(a, str) and re.match(r"^https?://", a.strip(), re.IGNORECASE):
                raw_url = a.strip()
                break

    tok = None
    headers = entry.get("headers") if isinstance(entry.get("headers"), dict) else {}
    for hk, hv in headers.items():
        if str(hk).lower() == "authorization" and isinstance(hv, str):
            tok = _strip_bearer(hv)
            if tok:
                break
    if not tok:
        for k in ("bearerToken", "token", "apiKey"):
            v = entry.get(k)
            if isinstance(v, str):
                tok = _strip_bearer(v)
                if tok:
                    break
    if not tok and args_list:
        for idx, a in enumerate(args_list):
            if not isinstance(a, str):
                continue
            header_val = None
            if a == "--header" and idx + 1 < len(args_list) and isinstance(args_list[idx + 1], str):
                header_val = args_list[idx + 1]
            elif a.startswith("--header="):
                header_val = a[len("--header="):]
            elif a.lower().startswith("authorization:"):
                header_val = a
            if header_val:
                m = re.match(r"^authorization\s*:\s*(.+)$", header_val.strip(), re.IGNORECASE)
                if m:
                    tok = _strip_bearer(m.group(1))
                    if tok:
                        break
    return raw_url, tok


def parse_mcp_config(config_path):
    """Parse LocalRoast server URL and Bearer token from a standard MCP config JSON file."""
    with open(config_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValueError(f"Invalid MCP config JSON in '{config_path}'.")

    servers = data.get("mcpServers") or data.get("servers")
    if isinstance(servers, dict) and servers:
        candidates = []
        for name, entry in servers.items():
            if not isinstance(entry, dict):
                continue
            url_cand, tok_cand = _extract_from_mcp_entry(entry)
            if not url_cand and not tok_cand:
                continue
            score = 0
            if "localroast" in str(name).lower():
                score += 10
            if url_cand and "localroast" in url_cand.lower():
                score += 8
            if tok_cand and tok_cand.startswith("lr_pat_"):
                score += 5
            if url_cand and url_cand.lower().rstrip("/").endswith("/mcp"):
                score += 2
            candidates.append((score, str(name), entry))
        if candidates:
            candidates.sort(key=lambda x: -x[0])
            return _extract_from_mcp_entry(candidates[0][2])

    return _extract_from_mcp_entry(data)


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


def verify_worker_and_pause(api_base, token, pause=True):
    """Verify /api/bulk-import connectivity and optionally pause the background worker."""
    state = api_json_request("GET", f"{api_base}/bulk-import/ping", token=token)
    print(
        f"Connected to {api_base} (EnableIngestion={bool((state or {}).get('enableIngestion'))})."
    )
    if pause and (state or {}).get("enableIngestion"):
        updated = api_json_request(
            "PUT",
            f"{api_base}/bulk-import/worker",
            token=token,
            payload_obj={"enableIngestion": False},
        )
        print(
            f"Paused remote/local background ingestion worker (EnableIngestion={bool((updated or {}).get('enableIngestion'))})."
        )


def fetch_existing_titles(api_base, token):
    """Fetch normalized titles of active ingestion jobs and existing recipes for idempotent resuming."""
    existing = set()
    job_count = 0
    page = 1
    while True:
        res = api_json_request(
            "GET",
            f"{api_base}/bulk-import/jobs?page={page}&pageSize=100",
            token=token,
        ) or {}
        items = res.get("items") or []
        for j in items:
            job_count += 1
            st = str(j.get("status") or "").strip().lower()
            if st in ("failed", "discarded", "6", "7"):
                continue
            nt = norm_dedupe_title(j.get("displayTitle") or "")
            if nt:
                existing.add(nt)
        if not items or not (res.get("hasNextPage") or res.get("hasMore")):
            break
        page += 1

    recipe_count = 0
    page = 1
    while True:
        res = api_json_request(
            "GET",
            f"{api_base}/bulk-import/recipes?page={page}&pageSize=100",
            token=token,
        ) or {}
        items = res.get("items") or []
        for r in items:
            recipe_count += 1
            nt = norm_dedupe_title(r.get("title") or "")
            if nt:
                existing.add(nt)
        if not items or not (res.get("hasMore") or res.get("hasNextPage")):
            break
        page += 1

    return existing, job_count, recipe_count


def ensure_book_collection(api_base, token, collection_name=BOOK_TITLE):
    """Ensure a single LocalRoast Collection exists for the cookbook (by default BOOK_TITLE)."""
    clean_name = (collection_name or BOOK_TITLE).strip()[:100]
    if not clean_name:
        return None

    existing = api_json_request("GET", f"{api_base}/bulk-import/collections", token=token) or []
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
        f"{api_base}/bulk-import/collections",
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
    """Extract an image from an open EPUB zip handle and upload to /api/bulk-import/media/recipe-cover."""
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

        media_url = f"{api_base}/bulk-import/media/recipe-cover"
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
    """Enqueue a parsed recipe into LocalRoast POST /api/bulk-import/jobs/text for LLM extraction."""
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
    return api_json_request("POST", f"{api_base}/bulk-import/jobs/text", token=token, payload_obj=payload)


def post_recipe_directly_to_api(api_base, token, recipe_payload):
    """Fallback direct creation to LocalRoast POST /api/recipes (skips LLM queue)."""
    clean_payload = {k: v for k, v in recipe_payload.items() if not k.startswith("_")}
    return api_json_request("POST", f"{api_base}/recipes", token=token, payload_obj=clean_payload)


def upload_source_text_attachment(api_base, token, recipe_id, recipe_dict):
    """Upload formatted raw source text as a RecipeAttachment."""
    try:
        raw_text = format_recipe_as_llm_raw_text(recipe_dict)
        clean_title = re.sub(r"[^\w\s-]", "", recipe_dict["title"]).strip().replace(" ", "_")[:50]
        filename = f"{clean_title}_Source.txt"
        boundary = "----WebKitFormBoundaryLocalRoastImport777Text"
        body = io.BytesIO()

        # Part: file
        body.write(f"--{boundary}\r\n".encode("utf-8"))
        body.write(f'Content-Disposition: form-data; name="file"; filename="{filename}"\r\n'.encode("utf-8"))
        body.write(b"Content-Type: text/plain\r\n\r\n")
        body.write(raw_text.encode("utf-8"))
        body.write(f"\r\n--{boundary}\r\n".encode("utf-8"))

        # Part: label
        body.write(b'Content-Disposition: form-data; name="label"\r\n\r\n')
        body.write(b"Original Source Text\r\n")
        body.write(f"--{boundary}--\r\n".encode("utf-8"))

        req = urllib.request.Request(
            f"{api_base}/recipes/{recipe_id}/attachments",
            data=body.getvalue(),
            headers={
                "Authorization": f"Bearer {token}",
                "Content-Type": f"multipart/form-data; boundary={boundary}",
            },
            method="POST",
        )
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception as ex:
        print(f"    (Notice: source text attachment upload failed: {ex})")
        return None



def sort_recipes_topologically(recipes):
    """Sort recipes so component/prerequisite recipes come before composite recipes, and parent recipes come before variations."""
    title_to_indices = defaultdict(list)
    norm_to_indices = defaultdict(list)

    def _add_norm(key, idx):
        if key and idx not in norm_to_indices[key]:
            norm_to_indices[key].append(idx)

    for i, r in enumerate(recipes):
        t = r["title"]
        title_to_indices[t.strip().lower()].append(i)
        _add_norm(_norm_recipe_key(t, strip_leading_the=False), i)
        _add_norm(_norm_recipe_key(t, strip_leading_the=True), i)
        stripped = re.sub(r"\s*\([^()]+\)\s*$", "", t).strip()
        stripped_norm = _norm_recipe_key(stripped, strip_leading_the=True)
        if len(stripped_norm) >= 4:
            _add_norm(stripped_norm, i)

    def pick_best_target_idx(indices, src_idx):
        candidates = [idx for idx in indices if idx != src_idx]
        if not candidates:
            return None
        src = recipes[src_idx]
        src_group = src.get("source_parent_recipe") or src.get("parent_recipe") or src
        src_chapter = src.get("chapter")

        def rank(idx):
            tgt = recipes[idx]
            tgt_group = tgt.get("source_parent_recipe") or tgt.get("parent_recipe") or tgt
            same_group = 0 if tgt_group is src_group else 1
            same_chapter = 0 if (src_chapter and tgt.get("chapter") == src_chapter) else 1
            not_preceding = 0 if idx < src_idx else 1
            return (same_group, same_chapter, not_preceding, abs(src_idx - idx), idx)

        return min(candidates, key=rank)

    def resolve_title_to_idx(title_str, src_idx):
        if not title_str:
            return None
        exact_list = title_to_indices.get(title_str.strip().lower())
        if exact_list:
            picked = pick_best_target_idx(exact_list, src_idx)
            if picked is not None:
                return picked
        for strip_the in (False, True):
            k = _norm_recipe_key(title_str, strip_leading_the=strip_the)
            if k and k in norm_to_indices:
                picked = pick_best_target_idx(norm_to_indices[k], src_idx)
                if picked is not None:
                    return picked
        return None

    trailing_prep_re = re.compile(
        r",\s*(?:for\s+(?:serving|garnish\w*)|warmed|cooled|chilled|softened|melted|room temperature|plus\s+.*|broken\s+.*|rolled\s+.*|fitted\s+.*|baked\s+.*|peeled\s+.*|cut\s+.*|at\s+room\s+temperature.*)$",
        re.IGNORECASE,
    )
    leading_qty_unit_re = re.compile(
        r"^(?:(?:\d+[\d\s\/\.]*|\b(?:one|two|three|four|five|six|seven|eight|nine|ten)\b)\s*)?(?:recipes?|batches?|cups?|teaspoons?|tsps?|tablespoons?|tbsps?|ounces?|oz|pounds?|lbs?|packages?|cans?|bottles?|pinches?|cloves?|slices?|stalks?|sprigs?|heads?|pieces?)\s+(?:of\s+)?",
        re.IGNORECASE,
    )

    def find_referenced_recipes(text, src_idx):
        matched_indices = set()
        candidates = []

        m = re.search(
            r"([A-Za-z0-9\s\',–—-]+?)\s*(?:\(optional\)\s*)?\((?:optional[;\s]*)?(?:this page|recipes?\s+follows?|recipe on page|\bsee page)\b",
            text,
            re.IGNORECASE,
        )
        if m:
            cand_m = re.sub(r"\s*\(\s*optional\s*\)\s*$", "", m.group(1), flags=re.IGNORECASE).strip()
            if cand_m:
                candidates.append(cand_m)

        m2 = re.search(r"\b(?:recipes?|batches?)\s+([^;(]+)", text, re.IGNORECASE)
        if m2:
            raw_m2 = m2.group(1).strip()
            # Apply repeatedly in case multiple trailing prep clauses are chained with commas
            cleaned_m2 = raw_m2
            while True:
                next_m2 = trailing_prep_re.sub("", cleaned_m2).strip()
                if next_m2 == cleaned_m2:
                    break
                cleaned_m2 = next_m2
            if cleaned_m2:
                candidates.append(cleaned_m2)

        for cand in list(candidates):
            if " or " in cand:
                candidates.append(cand.split(" or ")[-1].strip())

        for cand in candidates:
            cand_clean = leading_qty_unit_re.sub("", cand).strip()
            cand_norm = _norm_recipe_key(cand_clean, strip_leading_the=False)
            cand_norm_no_the = _norm_recipe_key(cand_clean, strip_leading_the=True)
            if not cand_norm or len(cand_norm) < 3:
                continue

            exact_picked = None
            for k in (cand_norm, cand_norm_no_the):
                if k in norm_to_indices:
                    exact_picked = pick_best_target_idx(norm_to_indices[k], src_idx)
                    if exact_picked is not None:
                        matched_indices.add(exact_picked)
                        break
            if exact_picked is not None:
                continue

            if len(cand_norm_no_the) >= 6:
                prefix_matches = []
                for norm, idx_list in norm_to_indices.items():
                    if len(norm) >= 6 and (
                        norm.startswith(cand_norm_no_the) or cand_norm_no_the.startswith(norm)
                    ):
                        prefix_matches.extend(idx_list)
                picked = pick_best_target_idx(prefix_matches, src_idx)
                if picked is not None:
                    matched_indices.add(picked)

        return matched_indices

    deps_of = [set() for _ in range(len(recipes))]
    dependents = defaultdict(list)

    for i, r in enumerate(recipes):
        deps_for_i = deps_of[i]
        if r.get("parent_recipe_title"):
            parent_idx = resolve_title_to_idx(r["parent_recipe_title"], i)
            if parent_idx is not None and parent_idx != i:
                deps_for_i.add(parent_idx)

        for ing in r.get("ingredients", []):
            for linked_title in ing.get("linked_recipes", []):
                ref_idx = resolve_title_to_idx(linked_title, i)
                if ref_idx is not None and ref_idx != i and i not in deps_of[ref_idx]:
                    deps_for_i.add(ref_idx)

            ref_indices = find_referenced_recipes(ing.get("text", ""), i)
            for ref_idx in ref_indices:
                if ref_idx != i and i not in deps_of[ref_idx]:
                    deps_for_i.add(ref_idx)

    # Guard against two-way cycles where an earlier processed node added i before i processed it
    for i, deps_for_i in enumerate(deps_of):
        to_drop = {u for u in deps_for_i if i in deps_of[u]}
        if to_drop:
            deps_for_i -= to_drop
        for u in deps_for_i:
            dependents[u].append(i)

    in_degree = [len(d) for d in deps_of]

    # Min-heap ordered by original reading index ensures recipes without dependencies
    # maintain their natural chapter/reading order
    heap = [i for i in range(len(recipes)) if in_degree[i] == 0]
    heapq.heapify(heap)

    sorted_indices = []
    while heap:
        u = heapq.heappop(heap)
        sorted_indices.append(u)
        for v in dependents[u]:
            in_degree[v] -= 1
            if in_degree[v] == 0:
                heapq.heappush(heap, v)

    # If any unexpected dependency cycle prevented visiting all recipes, warn and append remaining in original order
    if len(sorted_indices) < len(recipes):
        seen = set(sorted_indices)
        cycle_titles = [recipes[i]["title"] for i in range(len(recipes)) if i not in seen]
        print(
            f"Warning: topological sort detected dependency cycle involving {len(cycle_titles)} recipe(s): {cycle_titles[:10]}"
        )
        for i in range(len(recipes)):
            if i not in seen:
                sorted_indices.append(i)

    return [recipes[idx] for idx in sorted_indices]


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
    parser.add_argument(
        "--mcp-config",
        type=str,
        default=None,
        help="Path to LocalRoast MCP config JSON containing server URL and Bearer lr_pat_... token",
    )
    parser.add_argument(
        "--url",
        default=None,
        help="LocalRoast base URL (overrides MCP config URL; default: http://localhost:5041)",
    )
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
    parser.add_argument(
        "--no-skip-existing",
        action="store_true",
        help="Enqueue all selected recipes even if a matching job or recipe title already exists.",
    )

    args = parser.parse_args()

    # 1. Parse EPUB
    recipes = parse_atk_epub(args.epub)
    recipes = sort_recipes_topologically(recipes)

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

    mcp_cfg_path = args.mcp_config or os.environ.get("LOCALROAST_MCP_CONFIG")
    env_token = os.environ.get("LOCALROAST_TOKEN")
    should_call_api = (
        bool(args.import_api or args.email or args.token or env_token or mcp_cfg_path)
        and not args.dry_run
    )

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
        mcp_url = None
        mcp_tok = None
        if mcp_cfg_path:
            mcp_url, mcp_tok = parse_mcp_config(mcp_cfg_path)

        raw_api_url = args.url or os.environ.get("LOCALROAST_API") or mcp_url or "http://localhost:5041"
        api_base = normalize_api_base(raw_api_url)
        token = _strip_bearer(args.token) or _strip_bearer(env_token) or mcp_tok
        if not token:
            if not args.email or not args.password:
                print(
                    "Error: --mcp-config, --token (or LOCALROAST_TOKEN), or --email + --password are required for API import."
                )
                sys.exit(1)
            token = authenticate_api(api_base, args.email, args.password)

        use_llm_queue = not args.direct_import
        upload_images = not args.no_images
        auto_publish = bool(args.auto_publish) and not args.review_only
        skip_existing = use_llm_queue and not args.no_skip_existing

        verify_worker_and_pause(api_base, token, pause=use_llm_queue)
        if use_llm_queue:
            mode_desc = (
                "LLM Queue (Force Auto-Publish ON)"
                if auto_publish
                else "LLM Queue (Follows LLM Settings Auto-Publish Toggle)"
            )
        else:
            mode_desc = "Direct Recipe Creation (Bypassing LLM)"

        existing_titles = set()
        queued_this_run_titles = set()
        if skip_existing:
            existing_titles, existing_jobs, existing_recipes = fetch_existing_titles(api_base, token)
            print(
                f"Preflight scan: found {existing_jobs} job(s) and {existing_recipes} recipe(s) "
                f"({len(existing_titles)} unique titles eligible to skip)."
            )

        book_collection_id = ensure_book_collection(api_base, token, args.collection)

        print(f"\nStarting {mode_desc} for {len(recipes)} recipe(s) -> {api_base}...")
        success_count = 0
        skipped_count = 0
        skipped_duplicate_count = 0
        error_count = 0

        with zipfile.ZipFile(args.epub, "r") as epub_zip:
            for i, r in enumerate(recipes):
                title = r["title"][:200]
                col_id = book_collection_id

                if skip_existing:
                    dedupe_key = norm_dedupe_title(title)
                    if dedupe_key and dedupe_key in existing_titles:
                        skipped_count += 1
                        continue
                    if dedupe_key and dedupe_key in queued_this_run_titles:
                        skipped_duplicate_count += 1
                        print(f"[{i+1}/{len(recipes)}] Skipping (duplicate title in run): {title}")
                        continue

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
                        dedupe_key = norm_dedupe_title(title)
                        if dedupe_key:
                            queued_this_run_titles.add(dedupe_key)
                        success_count += 1
                        print(f" Queued! (Job ID: {job.get('id')})")
                    else:
                        payload = format_recipe_for_localroast(r, collection_id=col_id)
                        if cover_path:
                            payload["coverImagePath"] = cover_path
                            payload["thumbnailImagePath"] = thumb_path
                        created = post_recipe_directly_to_api(api_base, token, payload)
                        if created and created.get("id"):
                            upload_source_text_attachment(api_base, token, created["id"], r)
                        success_count += 1
                        print(f" Created! (Recipe ID: {created.get('id')})")
                except urllib.error.HTTPError as http_ex:
                    error_count += 1
                    err_body = http_ex.read().decode("utf-8", errors="ignore")
                    print(f" FAILED ({http_ex.code}): {err_body}")
                except Exception as ex:
                    error_count += 1
                    print(f" FAILED: {ex}")

        skip_summary = f"{skipped_count} skipped (already present)"
        if skipped_duplicate_count:
            skip_summary += f", {skipped_duplicate_count} skipped (duplicate in run)"
        print(f"\nFinished! {success_count} succeeded, {skip_summary}, {error_count} failed.")
        if use_llm_queue and success_count > 0:
            print(
                "Background worker is now extracting recipes sequentially, reusing and creating "
                "canonical FoodItems and Tags as each recipe completes."
            )


if __name__ == "__main__":
    main()
