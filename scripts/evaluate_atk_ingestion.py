#!/usr/bin/env python3
"""Serial, database-free evaluation using the ATK importer and production .NET extraction path."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

from import_atk_epub import (
    DEFAULT_EPUB_PATH,
    format_recipe_as_llm_raw_text,
    parse_atk_epub,
)

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "scripts" / "IngestionEvaluator" / "IngestionEvaluator.csproj"
DLL = PROJECT.parent / "bin" / "Debug" / "net10.0" / "IngestionEvaluator.dll"
AMOUNT_IN_INSTRUCTION = re.compile(
    r"\b\d+(?:[./]\d+)?(?:\s+\d+/\d+)?\s*"
    r"(?:teaspoons?|tablespoons?|cups?|ounces?|pounds?|tsp|tbsp|lbs?|grams?|kilograms?|milliliters?|liters?)\b",
    re.IGNORECASE,
)
UNIT_FACTORS = {
    "Tsp": ("volume", 1), "Tbsp": ("volume", 3), "Cup": ("volume", 48),
    "FlOz": ("volume", 6), "Pt": ("volume", 96), "Qt": ("volume", 192),
    "Oz": ("mass", 1), "Lb": ("mass", 16),
    "G": ("metric-mass", 1), "Kg": ("metric-mass", 1000),
    "Ml": ("metric-volume", 1), "L": ("metric-volume", 1000),
}


def converted_amount(ingredient, key, unit):
    value = ingredient.get(key) or 0
    if not value:
        return 0
    actual = ingredient.get("unit")
    if actual is None and unit == "Whole":
        return value
    if actual == unit:
        return value
    if actual in UNIT_FACTORS and unit in UNIT_FACTORS:
        dimension, factor = UNIT_FACTORS[actual]
        target_dimension, target_factor = UNIT_FACTORS[unit]
        if dimension == target_dimension:
            return value * factor / target_factor
    raise ValueError(f"Cannot convert {actual} to {unit}")


def ingredient_amounts_in_prose(instruction):
    return [
        m.group() for m in AMOUNT_IN_INSTRUCTION.finditer(instruction)
        if not re.search(
            r"\b(?:reduces?|reduced|reduction|obtain|yield|measures?)\b[^.;]{0,55}$",
            instruction[:m.start()], re.IGNORECASE,
        )
    ]


def score_result(case, source, result):
    """Checks are deliberately independent of the model's self-reported confidence."""
    issues = []
    if result.get("error"):
        return [result["error"]]
    draft = result.get("draft") or {}
    ingredients = draft.get("ingredients") or []
    steps = draft.get("steps") or []
    if not ingredients or not steps:
        issues.append("Empty ingredients or steps")
    if draft.get("title", "").casefold() != source["title"].casefold():
        issues.append("Title differs from source")
    for index, step in enumerate(steps):
        if ingredient_amounts_in_prose(step["instruction"]):
            issues.append(f"Step {index + 1}: hard-coded ingredient amount")
        indices = step.get("ingredientIndices") or []
        if any(i < 0 or i >= len(ingredients) for i in indices):
            issues.append(f"Step {index + 1}: invalid ingredient index")
        timers = step.get("timers") or []
        labels = [t["label"].casefold() for t in timers]
        if len(labels) != len(set(labels)):
            issues.append(f"Step {index + 1}: duplicate timer labels")
    for index, ingredient in enumerate(ingredients):
        name = ingredient["displayName"]
        if re.search(r"\b(?:salt and pepper|salt & pepper)\b", name, re.IGNORECASE):
            issues.append(f"Ingredient {index}: combined salt/pepper")
        if re.match(r"^\d", name):
            issues.append(f"Ingredient {index}: quantity in display name")
        if ingredient.get("quantity") is not None and not any(
            index in (s.get("ingredientIndices") or []) for s in steps
        ):
            issues.append(f"Ingredient {index}: measured ingredient not linked to any step")
    for index, step in enumerate((result.get("raw") or {}).get("steps") or []):
        if any(i < 0 or i >= len(ingredients) for i in (step.get("ingredientIndices") or [])):
            issues.append(f"Raw step {index + 1}: invalid index discarded by normalizer")
    source_lines = {re.sub(r"\s+", " ", i["text"]).strip() for i in source["ingredients"]}
    extracted_lines = {
        re.sub(r"\s+", " ", i.get("originalRawText") or "").strip().removeprefix("- ")
        for i in ingredients
    }
    for line in sorted(source_lines - extracted_lines):
        issues.append(f"Source ingredient not preserved verbatim: {line}")
    for expected in case.get("ingredients", []):
        matching_indices = [
            index for index, i in enumerate(ingredients)
            if re.search(r"\b(?:" + expected["name"] + ")", i["displayName"], re.IGNORECASE)
        ]
        matches = [ingredients[index] for index in matching_indices]
        if not matches:
            issues.append(f"Missing ingredient: {expected['name']}")
            continue
        unit = expected.get("unit")
        try:
            amounts = [converted_amount(i, "quantity", unit) for i in matches]
            maxima = [converted_amount(i, "maxQuantity" if i.get("maxQuantity") else "quantity", unit) for i in matches]
        except ValueError:
            issues.append(f"Wrong unit: {expected['name']}")
            continue
        if "quantity" in expected:
            total = sum(amounts)
            if abs(total - expected["quantity"]) > 0.001:
                issues.append(f"Wrong total: {expected['name']} ({total}, expected {expected['quantity']})")
        if "parts" in expected:
            try:
                portions = []
                for index, amount in zip(matching_indices, amounts):
                    step_amounts = [
                        converted_amount(a["measurement"], "quantity", unit)
                        for s in steps for a in (s.get("ingredientAmounts") or [])
                        if a["ingredientIndex"] == index
                    ]
                    portions.extend(step_amounts or [amount])
                actual = sorted(a for a in portions if a > 0)
            except ValueError:
                issues.append(f"Wrong portion unit: {expected['name']}")
                actual = []
            if len(actual) != len(expected["parts"]) or any(
                abs(a - b) > 0.001 for a, b in zip(actual, sorted(expected["parts"]))
            ):
                issues.append(f"Wrong portions: {expected['name']} ({actual})")
        if "alternativeMeasurement" in expected:
            alternative = expected["alternativeMeasurement"]
            try:
                actual_alternatives = [
                    converted_amount(i["alternativeMeasurement"], "quantity", alternative["unit"])
                    for i in matches if i.get("alternativeMeasurement")
                ]
            except ValueError:
                issues.append(f"Wrong alternative unit: {expected['name']}")
                actual_alternatives = []
            if not any(abs(a - alternative["quantity"]) <= 0.001 for a in actual_alternatives):
                issues.append(f"Missing or incorrect alternative measurement: {expected['name']}")
        if "range" in expected:
            lower = sum(a for a, i in zip(amounts, matches) if not i.get("isOptional"))
            upper = sum(maxima)
            if abs(lower - expected["range"][0]) > 0.001 or abs(upper - expected["range"][1]) > 0.001:
                issues.append(f"Wrong range: {expected['name']} ({lower}-{upper})")
        if "prep" in expected and not any(
            re.search(expected["prep"], i.get("preparationNote") or "", re.IGNORECASE) for i in matches
        ):
            issues.append(f"Missing preparation: {expected['name']} / {expected['prep']}")
    for excluded in case.get("excluded", []):
        if any(re.search(excluded, i["displayName"], re.IGNORECASE) for i in ingredients):
            issues.append(f"Alternative incorrectly added as ingredient: {excluded}")
    for expected in case.get("timers", []):
        if not any(
            t["durationSeconds"] == expected["seconds"]
            and re.search(expected["label"], t["label"], re.IGNORECASE)
            for step in steps for t in (step.get("timers") or [])
        ):
            issues.append(f"Missing timer: {expected['label']} / {expected['seconds']}s")
    return issues


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--epub", default=DEFAULT_EPUB_PATH)
    parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("atk_ingestion_cases.json"))
    parser.add_argument("--ids", nargs="+")
    parser.add_argument("--models", nargs="+", default=["gemma4:e2b", "gemma4:e4b"])
    parser.add_argument("--base-url", default="http://localhost:11434")
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--thinking", action="store_true")
    parser.add_argument("--ollama-options", default="{}", help="Experimental overrides; recorded in every result")
    parser.add_argument("--output", type=Path, required=True, help="Private results directory; contains source text")
    parser.add_argument("--score-only", action="store_true")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if args.timeout < 15:
        parser.error("--timeout must be at least 15 seconds")
    if not isinstance(json.loads(args.ollama_options), dict):
        parser.error("--ollama-options must be a JSON object")
    cases = json.loads(args.cases.read_text(encoding="utf-8"))
    if args.ids:
        cases = [c for c in cases if c["id"] in args.ids]
        if set(args.ids) != {c["id"] for c in cases}:
            parser.error("An --ids value is missing from the case manifest")
    recipes = {r["id"]: r for r in parse_atk_epub(args.epub)}
    if not cases or any(c["id"] not in recipes for c in cases):
        parser.error("Empty case selection or case ID missing from this EPUB edition")
    args.output.mkdir(parents=True, exist_ok=True)
    input_path = args.output / "input.json"
    if not args.score_only:
        if input_path.exists():
            parser.error("Use a new output directory for each run (or --score-only)")
        if not args.no_build:
            subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "-v:q"], check=True)
        input_path.write_text(
            json.dumps([
                {
                    "id": c["id"],
                    "title": recipes[c["id"]]["title"],
                    "sourceContent": format_recipe_as_llm_raw_text(recipes[c["id"]]),
                    "foodNames": c.get("foodNames", []),
                } for c in cases
            ], ensure_ascii=False, indent=2), encoding="utf-8"
        )
        files = [
            ROOT / "Infrastructure" / "Ingestion" / "LlmExtractionClient.cs",
            ROOT / "Core" / "Ingestion" / "RecipeDraftNormalizer.cs",
            ROOT / "Core" / "Ingestion" / "RecipeExtractionDiagnostics.cs",
            ROOT / "Core" / "Ingestion" / "StepTimerExtractor.cs",
            args.cases,
        ]
        metadata = {
            "createdUtc": datetime.now(timezone.utc).isoformat(),
            "baseCommit": subprocess.check_output(
                ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
            ).strip(),
            "filesSha256": {str(p.relative_to(ROOT) if p.is_relative_to(ROOT) else p.name):
                            hashlib.sha256(p.read_bytes()).hexdigest() for p in files},
            "models": args.models, "thinking": args.thinking,
            "ollamaOptions": json.loads(args.ollama_options),
            "caseIds": [c["id"] for c in cases],
        }
        (args.output / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    summary = []
    for model in args.models:
        model_dir = args.output / re.sub(r"[^a-zA-Z0-9_.-]", "_", model)
        if not args.score_only:
            # Intentionally serial: no concurrent requests or competing loaded models.
            completed = subprocess.run([
                "dotnet", str(DLL), str(input_path), str(model_dir),
                args.base_url, model, str(args.timeout), str(args.thinking).lower(),
                args.ollama_options,
            ])
            if completed.returncode not in (0, 1):
                raise RuntimeError(f"Evaluator exited with code {completed.returncode}")
        for case in cases:
            result = json.loads((model_dir / f"{case['id']}.json").read_text(encoding="utf-8"))
            issues = score_result(case, recipes[case["id"]], result)
            row = {
                "model": model, "id": case["id"], "split": case["split"],
                "seconds": round(result["elapsedSeconds"], 1), "issues": issues,
                "validationIssues": result.get("validationIssues"),
                "requests": len(result.get("exchanges", [])),
            }
            summary.append(row)
            print(f"{model} {case['id']}: {len(issues)} issues ({row['seconds']}s)", flush=True)
            for issue in issues:
                print(f"  - {issue}", flush=True)
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return 1 if any(row["issues"] for row in summary) else 0


if __name__ == "__main__":
    sys.exit(main())