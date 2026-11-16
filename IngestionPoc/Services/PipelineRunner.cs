using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using IngestionPoc.Models;

namespace IngestionPoc.Services;

public sealed record PocRunOptions(
    string RunDirectory,
    int MaxDescriptionLength,
    bool SessionCatalog,
    bool SkipSteps,
    bool SkipIngredients,
    PolishMode PolishMode = PolishMode.None
);

public sealed record RecipeRunSummary(
    string Id,
    string Title,
    string SourceKind,
    long TotalElapsedMs,
    bool Stage1Success,
    int Stage1Sections,
    int Stage1Ingredients,
    int Stage1Steps,
    bool Stage2Ran,
    int? OriginalDescriptionLength,
    int? FinalDescriptionLength,
    int ExistingIngredientMatches,
    int ExistingRecipeMatches,
    int NewIngredientsProposed,
    int UnresolvedRecipes,
    int IngredientValidationWarnings,
    int StepCount,
    int StepValidationWarnings,
    IReadOnlyList<string> ProseOnlyIngredients,
    string? FatalError,
    PolishSummaryMetrics? TargetedPolish = null,
    PolishSummaryMetrics? FullPolish = null
);

public sealed partial class PipelineRunner(
    LlmRunnerClient llmClient,
    PromptSet prompts,
    PocLlmConfig config,
    CatalogSnapshot catalog,
    PocRunOptions options
)
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    public async Task<IReadOnlyList<RecipeRunSummary>> RunAllAsync(
        IReadOnlyList<RecipeInputItem> inputs,
        CancellationToken cancellationToken = default
    )
    {
        Directory.CreateDirectory(options.RunDirectory);
        await WriteJsonAsync(
            Path.Combine(options.RunDirectory, "run-config.json"),
            new
            {
                createdUtc = DateTimeOffset.UtcNow,
                llm = new
                {
                    provider = config.Provider.ToString(),
                    endpointStyle = config.EndpointStyle.ToString(),
                    config.BaseUrl,
                    config.TextModel,
                    config.VisionModel,
                    config.EnableThinking,
                    config.AuxThinkingOverride,
                    config.Temperature,
                    config.TopP,
                    config.TopK,
                    config.ContextWindowTokens,
                    config.RequestTimeoutSeconds,
                    config.OllamaGpuLayers,
                    config.OllamaCpuOnly,
                },
                options = new
                {
                    options.MaxDescriptionLength,
                    options.SessionCatalog,
                    options.SkipIngredients,
                    options.SkipSteps,
                    polishMode = options.PolishMode.ToString(),
                    catalogLoadedFromDb = catalog.LoadedFromDatabase,
                    catalogFoodItemCount = catalog.FoodItems.Count,
                    catalogRecipeCount = catalog.Recipes.Count,
                },
                prompts = prompts.All.Select(p => new
                {
                    p.Name,
                    p.FilePath,
                    p.Sha256,
                }),
                inputs = inputs.Select(i => new
                {
                    i.Id,
                    i.Title,
                    i.SourceKind,
                    i.SourceReference,
                }),
            },
            cancellationToken
        );

        var summaries = new List<RecipeRunSummary>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var slug = Slugify(input.Title);
            var recipeDir = Path.Combine(options.RunDirectory, $"{i + 1:00}_{slug}");
            Console.WriteLine(
                $"\n[{i + 1}/{inputs.Count}] Running pipeline for: {input.Title} ({input.SourceKind})"
            );
            var summary = await RunSingleRecipeAsync(input, recipeDir, cancellationToken);
            summaries.Add(summary);
        }

        await WriteJsonAsync(
            Path.Combine(options.RunDirectory, "summary.json"),
            summaries,
            cancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(options.RunDirectory, "summary.md"),
            BuildMarkdownSummary(summaries),
            Encoding.UTF8,
            cancellationToken
        );

        return summaries;
    }

    private async Task<RecipeRunSummary> RunSingleRecipeAsync(
        RecipeInputItem input,
        string recipeDir,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(recipeDir);
        await File.WriteAllTextAsync(
            Path.Combine(recipeDir, "00-normalized-input.txt"),
            input.NormalizedText,
            Encoding.UTF8,
            cancellationToken
        );

        var sw = Stopwatch.StartNew();

        // ── Stage 1: Raw text -> Basic shape ────────────────────────────────────
        Console.Write("  Stage 1 (Extract recipe)... ");
        var stage1Dir = Path.Combine(recipeDir, "01-extract-recipe");
        Directory.CreateDirectory(stage1Dir);

        var stage1Result = await llmClient.CompleteStructuredAsync<Stage1RecipeDto>(
            config,
            "extract_recipe",
            PipelineSchemas.Stage1ExtractRecipeSchema(),
            prompts.ExtractRecipe.Content,
            input.NormalizedText,
            isAuxiliaryStep: false,
            cancellationToken: cancellationToken
        );

        await WriteExchangesAsync(stage1Dir, stage1Result.Exchanges, cancellationToken);
        await WriteJsonAsync(
            Path.Combine(stage1Dir, "output.json"),
            new
            {
                stage1Result.TotalElapsedMs,
                stage1Result.ParseError,
                recipe = stage1Result.Parsed,
            },
            cancellationToken
        );

        if (stage1Result.Parsed is null)
        {
            sw.Stop();
            Console.WriteLine($"FAILED ({stage1Result.ParseError})");
            return new RecipeRunSummary(
                input.Id,
                input.Title,
                input.SourceKind,
                sw.ElapsedMilliseconds,
                Stage1Success: false,
                0,
                0,
                0,
                false,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                stage1Result.ParseError ?? "Stage 1 extraction returned null."
            );
        }

        var recipe = stage1Result.Parsed;
        var flatIngredients = recipe
            .Sections.SelectMany(s =>
                s.Ingredients.Select(ing => (Section: s.Name, Ingredient: ing))
            )
            .ToList();
        var flatSteps = recipe
            .Sections.SelectMany(s =>
                s.Instructions.Select(instr => (Section: s.Name, Instruction: instr))
            )
            .ToList();

        Console.WriteLine(
            $"OK ({stage1Result.TotalElapsedMs / 1000.0:F1}s | {flatIngredients.Count} ingredients, {flatSteps.Count} steps, {recipe.Tags.Count} tags)"
        );

        // ── Stage 2: Shorten description if over varchar bounds ─────────────────
        var stage2Dir = Path.Combine(recipeDir, "02-shorten-description");
        Directory.CreateDirectory(stage2Dir);
        var origDescLen = recipe.Description?.Length;
        var finalDescription = recipe.Description;
        var stage2Ran = false;

        if (
            !string.IsNullOrWhiteSpace(recipe.Description)
            && recipe.Description.Length > options.MaxDescriptionLength
        )
        {
            stage2Ran = true;
            Console.Write(
                $"  Stage 2 (Shorten description {recipe.Description.Length} -> <= {options.MaxDescriptionLength})... "
            );
            var targetLength = Math.Max(100, (int)Math.Round(options.MaxDescriptionLength * 0.75));
            var stage2Prompt = prompts
                .ShortenDescription.Content.Replace(
                    "{{MAX_LENGTH}}",
                    options.MaxDescriptionLength.ToString(),
                    StringComparison.Ordinal
                )
                .Replace("{{TARGET_LENGTH}}", targetLength.ToString(), StringComparison.Ordinal);
            var stage2User = JsonSerializer.Serialize(
                new
                {
                    title = recipe.Title,
                    maxLength = options.MaxDescriptionLength,
                    targetLength,
                    currentLength = recipe.Description.Length,
                    description = recipe.Description,
                },
                PrettyJson
            );

            var stage2Result =
                await llmClient.CompleteStructuredMultiTurnAsync<Stage2ShortenedDescriptionDto>(
                    config,
                    "shorten_description",
                    PipelineSchemas.Stage2ShortenDescriptionSchema(),
                    stage2Prompt,
                    [("user", stage2User)],
                    isAuxiliaryStep: true,
                    validateRetryFeedback: dto =>
                        dto.Description.Length > options.MaxDescriptionLength
                            ? $"Your shortened description is {dto.Description.Length} characters, which still exceeds the {options.MaxDescriptionLength}-character limit by {dto.Description.Length - options.MaxDescriptionLength} characters. Trim it down to roughly {targetLength} characters (strictly <= {options.MaxDescriptionLength})."
                            : null,
                    maxCorrectionTurns: 4,
                    cancellationToken: cancellationToken
                );

            await WriteExchangesAsync(stage2Dir, stage2Result.Exchanges, cancellationToken);
            if (stage2Result.Parsed is not null)
            {
                finalDescription = stage2Result.Parsed.Description.Trim();
            }
            await WriteJsonAsync(
                Path.Combine(stage2Dir, "output.json"),
                new
                {
                    skipped = false,
                    originalLength = origDescLen,
                    finalLength = finalDescription?.Length,
                    maxLength = options.MaxDescriptionLength,
                    stage2Result.TotalElapsedMs,
                    stage2Result.ParseError,
                    description = finalDescription,
                },
                cancellationToken
            );
            var statusLabel =
                finalDescription?.Length <= options.MaxDescriptionLength ? "OK" : "OVER LIMIT";
            Console.WriteLine(
                $"{statusLabel} ({stage2Result.TotalElapsedMs / 1000.0:F1}s | {origDescLen} -> {finalDescription?.Length} chars, {stage2Result.Exchanges.Count} attempt(s))"
            );
        }
        else
        {
            await WriteJsonAsync(
                Path.Combine(stage2Dir, "status.json"),
                new
                {
                    skipped = true,
                    originalLength = origDescLen ?? 0,
                    maxLength = options.MaxDescriptionLength,
                },
                cancellationToken
            );
            Console.WriteLine(
                $"  Stage 2 (Shorten description)... skipped ({origDescLen ?? 0} <= {options.MaxDescriptionLength} chars)"
            );
        }

        // ── Stage 3: Ingredient & sub-recipe search + DB matching ───────────────
        var resolvedIngredients = new List<ResolvedIngredientResult>();
        if (!options.SkipIngredients && flatIngredients.Count > 0)
        {
            var stage3Dir = Path.Combine(recipeDir, "03-ingredients");
            Directory.CreateDirectory(stage3Dir);
            Console.WriteLine($"  Stage 3 (Resolve {flatIngredients.Count} ingredients)...");

            for (var idx = 0; idx < flatIngredients.Count; idx++)
            {
                var (secName, ing) = flatIngredients[idx];
                var ingSlug = Slugify(ing.Name);
                var ingDir = Path.Combine(stage3Dir, $"{idx + 1:00}_{ingSlug}");
                Directory.CreateDirectory(ingDir);

                var searchInputJson = JsonSerializer.Serialize(
                    new
                    {
                        name = ing.Name,
                        quantity = ing.Quantity,
                        maxQuantity = ing.MaxQuantity,
                        unit = ing.Unit,
                        prepNote = ing.PrepNote,
                    },
                    PrettyJson
                );

                var searchResult = await llmClient.CompleteStructuredAsync<Stage3SearchTermsDto>(
                    config,
                    "ingredient_search_terms",
                    PipelineSchemas.Stage3SearchTermsSchema(),
                    prompts.SearchTerms.Content,
                    searchInputJson,
                    isAuxiliaryStep: true,
                    cancellationToken: cancellationToken
                );

                await WriteExchangesAsync(
                    ingDir,
                    searchResult.Exchanges,
                    cancellationToken,
                    prefix: "03a-search-"
                );
                var rawTerms = searchResult.Parsed?.SearchTerms is { Count: > 0 }
                    ? searchResult.Parsed.SearchTerms
                    : [ing.Name];
                await WriteJsonAsync(
                    Path.Combine(ingDir, "03a-search-output.json"),
                    new
                    {
                        searchResult.TotalElapsedMs,
                        searchResult.ParseError,
                        searchTerms = rawTerms,
                    },
                    cancellationToken
                );

                var dbCandidates = CatalogSearchService.Search(catalog, rawTerms, recipe.Title);
                await WriteJsonAsync(
                    Path.Combine(ingDir, "db-candidates.json"),
                    dbCandidates,
                    cancellationToken
                );

                var matchPayload = JsonSerializer.Serialize(
                    new
                    {
                        ingredient = new
                        {
                            ing.Name,
                            ing.Quantity,
                            ing.MaxQuantity,
                            ing.Unit,
                            ing.PrepNote,
                        },
                        existingIngredients = dbCandidates.ExistingIngredients.Select(c => new
                        {
                            id = c.Id.ToString(),
                            c.Name,
                            category = c.Category.ToString(),
                            c.Aliases,
                        }),
                        existingRecipes = dbCandidates.ExistingRecipes.Select(r => new
                        {
                            id = r.Id.ToString(),
                            r.Title,
                        }),
                    },
                    PrettyJson
                );

                var matchResult = await llmClient.CompleteStructuredAsync<Stage3MatchDecisionDto>(
                    config,
                    "ingredient_match",
                    PipelineSchemas.Stage3MatchIngredientSchema(),
                    prompts.MatchIngredient.Content,
                    matchPayload,
                    isAuxiliaryStep: true,
                    cancellationToken: cancellationToken
                );

                await WriteExchangesAsync(
                    ingDir,
                    matchResult.Exchanges,
                    cancellationToken,
                    prefix: "03b-match-"
                );

                var decision =
                    matchResult.Parsed
                    ?? new Stage3MatchDecisionDto("new_ingredient", null, ing.Name, "Other");
                var ingWarnings = ValidateIngredientDecision(decision, dbCandidates);
                if (matchResult.ParseError is not null)
                {
                    ingWarnings.Add(matchResult.ParseError);
                }

                await WriteJsonAsync(
                    Path.Combine(ingDir, "03b-match-output.json"),
                    new
                    {
                        matchResult.TotalElapsedMs,
                        matchResult.ParseError,
                        decision,
                        validationWarnings = ingWarnings,
                    },
                    cancellationToken
                );

                if (options.SessionCatalog)
                {
                    CatalogSearchService.RegisterSessionDecision(catalog, decision);
                }

                resolvedIngredients.Add(
                    new ResolvedIngredientResult(
                        idx,
                        secName,
                        ing,
                        rawTerms,
                        dbCandidates,
                        decision,
                        ingWarnings
                    )
                );

                Console.WriteLine(
                    $"    [{idx + 1}/{flatIngredients.Count}] {ing.Name} -> [{string.Join(", ", rawTerms)}] => {decision.MatchType}: {decision.Name}"
                        + (decision.Category is not null ? $" ({decision.Category})" : string.Empty)
                        + $" ({(searchResult.TotalElapsedMs + matchResult.TotalElapsedMs) / 1000.0:F1}s)"
                );
            }

            await WriteJsonAsync(
                Path.Combine(stage3Dir, "summary.json"),
                resolvedIngredients.Select(r => new
                {
                    r.GlobalIndex,
                    r.SectionName,
                    inputName = r.Extracted.Name,
                    r.Extracted.Quantity,
                    r.Extracted.Unit,
                    r.SearchTerms,
                    foodCandidateCount = r.DbCandidates.ExistingIngredients.Count,
                    recipeCandidateCount = r.DbCandidates.ExistingRecipes.Count,
                    r.Decision,
                    r.ValidationWarnings,
                }),
                cancellationToken
            );
        }

        // ── Stage 4: Instruction step tokenization ──────────────────────────────
        var stepResults = new List<StepTokenizeResult>();
        if (!options.SkipSteps && flatSteps.Count > 0)
        {
            var stage4Dir = Path.Combine(recipeDir, "04-steps");
            Directory.CreateDirectory(stage4Dir);
            Console.WriteLine($"  Stage 4 (Tokenize {flatSteps.Count} instruction steps)...");

            var stepIngredientsPayload = flatIngredients
                .Select(
                    (pair, i) =>
                    {
                        var resolved = resolvedIngredients.FirstOrDefault(r => r.GlobalIndex == i);
                        return new
                        {
                            index = i,
                            section = pair.Section,
                            name = pair.Ingredient.Name,
                            resolvedName = resolved?.Decision.Name ?? pair.Ingredient.Name,
                            quantity = pair.Ingredient.Quantity,
                            maxQuantity = pair.Ingredient.MaxQuantity,
                            unit = pair.Ingredient.Unit,
                            prepNote = pair.Ingredient.PrepNote,
                            alternateMeasurements = pair.Ingredient.AlternateMeasurements,
                        };
                    }
                )
                .ToList();

            var priorSteps = new List<object>();

            for (var s = 0; s < flatSteps.Count; s++)
            {
                var (secName, instr) = flatSteps[s];
                var stepNum = s + 1;
                var stepDir = Path.Combine(stage4Dir, $"step-{stepNum:00}");
                Directory.CreateDirectory(stepDir);

                var stepUserPayload = JsonSerializer.Serialize(
                    new
                    {
                        recipeTitle = recipe.Title,
                        stepNumber = stepNum,
                        sectionName = secName,
                        instruction = instr,
                        ingredients = stepIngredientsPayload,
                        priorSteps,
                    },
                    PrettyJson
                );

                var stepCall = await llmClient.CompleteStructuredAsync<JsonObject>(
                    config,
                    "tokenize_step",
                    PipelineSchemas.Stage4TokenizeStepSchema(),
                    prompts.TokenizeStep.Content,
                    stepUserPayload,
                    isAuxiliaryStep: false,
                    cancellationToken: cancellationToken
                );

                await WriteExchangesAsync(stepDir, stepCall.Exchanges, cancellationToken);

                var rawTokensArray = stepCall.Parsed?["tokens"]?.AsArray() ?? new JsonArray();
                var stepRes = BuildStepResult(
                    stepNum,
                    secName,
                    instr,
                    rawTokensArray,
                    flatIngredients.Count,
                    stepCall.ParseError
                );
                stepResults.Add(stepRes);

                await WriteJsonAsync(
                    Path.Combine(stepDir, "output.json"),
                    new
                    {
                        stepNumber = stepNum,
                        sectionName = secName,
                        elapsedMs = stepCall.TotalElapsedMs,
                        inputInstruction = instr,
                        tokens = rawTokensArray,
                        rendered = new
                        {
                            original = stepRes.RenderedOriginal,
                            scaled2x = stepRes.RenderedScaled2x,
                            metric1x = stepRes.RenderedMetric1x,
                        },
                        validationWarnings = stepRes.ValidationWarnings,
                    },
                    cancellationToken
                );

                priorSteps.Add(new { stepNumber = stepNum, instruction = instr });

                var tokenCount = stepRes.ParsedTokens.Count(t => t is not PocTextToken);
                Console.WriteLine(
                    $"    Step {stepNum}/{flatSteps.Count}: {tokenCount} token(s), {stepRes.ValidationWarnings.Count} warning(s) ({stepCall.TotalElapsedMs / 1000.0:F1}s)"
                );
            }

            await WriteJsonAsync(
                Path.Combine(stage4Dir, "summary.json"),
                stepResults.Select(r => new
                {
                    r.StepNumber,
                    r.SectionName,
                    r.OriginalInstruction,
                    tokens = r.RawTokensJson,
                    rendered = new
                    {
                        original = r.RenderedOriginal,
                        scaled2x = r.RenderedScaled2x,
                        metric1x = r.RenderedMetric1x,
                    },
                    r.ValidationWarnings,
                }),
                cancellationToken
            );
        }

        var baselineIngredients = flatIngredients
            .Select(
                (pair, i) =>
                {
                    var res = resolvedIngredients.FirstOrDefault(r => r.GlobalIndex == i);
                    return new AssembledIngredient(i, pair.Section, pair.Ingredient, res?.Decision);
                }
            )
            .ToList();

        var proseOnlyBaseline =
            !options.SkipSteps && flatSteps.Count > 0
                ? ComputeProseOnlyIngredients(baselineIngredients, stepResults)
                : [];

        var baseline = new AssembledRecipeState(
            recipe.Title,
            finalDescription,
            recipe.Servings,
            recipe.Yield,
            recipe.Timing,
            recipe.Tags,
            baselineIngredients,
            stepResults,
            proseOnlyBaseline
        );

        await WriteAssembledRecipeJsonAsync(
            Path.Combine(recipeDir, "final-recipe.json"),
            baseline,
            polishNotes: null,
            cancellationToken
        );

        PolishSummaryMetrics? targetedPolish = null;
        PolishSummaryMetrics? fullPolish = null;
        AssembledRecipeState? targetedState = null;
        AssembledRecipeState? fullState = null;

        if (options.PolishMode is not PolishMode.None && !options.SkipSteps && flatSteps.Count > 0)
        {
            var stage5Payload = BuildStage5UserPayload(input, baseline);

            if (options.PolishMode is PolishMode.Targeted or PolishMode.Both)
            {
                (targetedPolish, targetedState) = await RunTargetedPolishAsync(
                    recipeDir,
                    stage5Payload,
                    baseline,
                    cancellationToken
                );
            }

            if (options.PolishMode is PolishMode.Full or PolishMode.Both)
            {
                (fullPolish, fullState) = await RunFullPolishAsync(
                    recipeDir,
                    stage5Payload,
                    baseline,
                    cancellationToken
                );
            }

            if (options.PolishMode == PolishMode.Both)
            {
                await WritePolishComparisonMarkdownAsync(
                    Path.Combine(recipeDir, "polish-comparison.md"),
                    baseline,
                    targetedPolish,
                    targetedState,
                    fullPolish,
                    fullState,
                    cancellationToken
                );
            }
        }

        sw.Stop();
        return new RecipeRunSummary(
            input.Id,
            recipe.Title,
            input.SourceKind,
            sw.ElapsedMilliseconds,
            Stage1Success: true,
            recipe.Sections.Count,
            flatIngredients.Count,
            flatSteps.Count,
            stage2Ran,
            origDescLen,
            finalDescription?.Length,
            resolvedIngredients.Count(r =>
                string.Equals(
                    r.Decision.MatchType,
                    "existing_ingredient",
                    StringComparison.OrdinalIgnoreCase
                )
            ),
            resolvedIngredients.Count(r =>
                string.Equals(
                    r.Decision.MatchType,
                    "existing_recipe",
                    StringComparison.OrdinalIgnoreCase
                )
            ),
            resolvedIngredients.Count(r =>
                string.Equals(
                    r.Decision.MatchType,
                    "new_ingredient",
                    StringComparison.OrdinalIgnoreCase
                )
            ),
            resolvedIngredients.Count(r =>
                string.Equals(
                    r.Decision.MatchType,
                    "unresolved_recipe",
                    StringComparison.OrdinalIgnoreCase
                )
            ),
            resolvedIngredients.Sum(r => r.ValidationWarnings.Count),
            stepResults.Count,
            stepResults.Sum(s => s.ValidationWarnings.Count),
            proseOnlyBaseline,
            null,
            targetedPolish,
            fullPolish
        );
    }

    private sealed record AssembledIngredient(
        int Index,
        string? Section,
        Stage1IngredientDto Extracted,
        Stage3MatchDecisionDto? Resolution
    );

    private sealed record AssembledRecipeState(
        string Title,
        string? Description,
        decimal? Servings,
        string? Yield,
        Stage1TimingDto? Timing,
        IReadOnlyList<string> Tags,
        IReadOnlyList<AssembledIngredient> Ingredients,
        IReadOnlyList<StepTokenizeResult> Steps,
        IReadOnlyList<string> ProseOnlyIngredients
    );

    private static StepTokenizeResult BuildStepResult(
        int stepNumber,
        string? sectionName,
        string originalInstruction,
        JsonArray rawTokensJson,
        int ingredientCount,
        string? extraParseError = null
    )
    {
        var parsedTokens = StepTokenParserAndRenderer.ParseTokens(
            rawTokensJson,
            out var parseErrors
        );
        if (extraParseError is not null)
        {
            parseErrors.Add(extraParseError);
        }

        var rendered1x = StepTokenParserAndRenderer.Render(
            parsedTokens,
            scaleFactor: 1m,
            convertToMetric: false
        );
        var rendered2x = StepTokenParserAndRenderer.Render(
            parsedTokens,
            scaleFactor: 2m,
            convertToMetric: false
        );
        var renderedMetric = StepTokenParserAndRenderer.Render(
            parsedTokens,
            scaleFactor: 1m,
            convertToMetric: true
        );
        var warnings = StepTokenParserAndRenderer.ValidateStepTokens(
            originalInstruction,
            parsedTokens,
            ingredientCount,
            parseErrors
        );

        return new StepTokenizeResult(
            stepNumber,
            sectionName,
            originalInstruction,
            (JsonArray)rawTokensJson.DeepClone(),
            parsedTokens,
            rendered1x,
            rendered2x,
            renderedMetric,
            warnings
        );
    }

    private static List<string> ComputeProseOnlyIngredients(
        IReadOnlyList<AssembledIngredient> ingredients,
        IReadOnlyList<StepTokenizeResult> steps
    )
    {
        var referencedIndices = steps
            .SelectMany(s => s.ParsedTokens)
            .OfType<PocIngredientToken>()
            .Where(t => t.IngredientIndex.HasValue)
            .Select(t => t.IngredientIndex!.Value)
            .ToHashSet();

        var proseOnly = new List<string>();
        foreach (var ing in ingredients)
        {
            if (!ing.Extracted.IsOptional && !referencedIndices.Contains(ing.Index))
            {
                proseOnly.Add($"[{ing.Index}] {ing.Extracted.Name}");
            }
        }
        return proseOnly;
    }

    private string BuildStage5UserPayload(RecipeInputItem input, AssembledRecipeState baseline)
    {
        return JsonSerializer.Serialize(
            new
            {
                originalSourceText = input.NormalizedText,
                currentRecipe = new
                {
                    baseline.Title,
                    baseline.Description,
                    baseline.Servings,
                    baseline.Yield,
                    baseline.Timing,
                    baseline.Tags,
                    ingredients = baseline.Ingredients.Select(i => new
                    {
                        i.Index,
                        i.Section,
                        i.Extracted.Name,
                        i.Extracted.Quantity,
                        i.Extracted.MaxQuantity,
                        i.Extracted.Unit,
                        i.Extracted.PrepNote,
                        i.Extracted.IsOptional,
                        i.Extracted.Replacements,
                        i.Extracted.AlternateMeasurements,
                        dbMatch = i.Resolution,
                    }),
                    steps = baseline.Steps.Select(s => new
                    {
                        s.StepNumber,
                        s.SectionName,
                        s.OriginalInstruction,
                        renderedOriginal = s.RenderedOriginal,
                        tokens = s.RawTokensJson,
                    }),
                },
            },
            PrettyJson
        );
    }

    private async Task<(
        PolishSummaryMetrics Metrics,
        AssembledRecipeState? State
    )> RunTargetedPolishAsync(
        string recipeDir,
        string userPayload,
        AssembledRecipeState baseline,
        CancellationToken cancellationToken
    )
    {
        var dir = Path.Combine(recipeDir, "05-polish-targeted");
        Directory.CreateDirectory(dir);
        Console.Write("  Stage 5a (Targeted polish)... ");

        var call = await llmClient.CompleteStructuredAsync<PolishTargetedResponseDto>(
            config,
            "polish_targeted",
            PipelineSchemas.Stage5PolishTargetedSchema(),
            prompts.PolishTargeted.Content,
            userPayload,
            isAuxiliaryStep: false,
            cancellationToken: cancellationToken
        );

        await WriteExchangesAsync(dir, call.Exchanges, cancellationToken);
        await WriteJsonAsync(
            Path.Combine(dir, "output.json"),
            new
            {
                call.TotalElapsedMs,
                call.ParseError,
                patches = call.Parsed,
            },
            cancellationToken
        );

        if (call.Parsed is null)
        {
            Console.WriteLine($"FAILED ({call.ParseError})");
            return (
                new PolishSummaryMetrics(
                    "targeted",
                    Success: false,
                    call.TotalElapsedMs,
                    0,
                    0,
                    0,
                    [],
                    call.ParseError
                ),
                null
            );
        }

        var polished = ApplyTargetedPatches(baseline, call.Parsed);
        await WriteAssembledRecipeJsonAsync(
            Path.Combine(dir, "final-recipe.json"),
            polished,
            call.Parsed.PolishNotes,
            cancellationToken
        );

        var stepWarnings = polished.Steps.Sum(s => s.ValidationWarnings.Count);
        Console.WriteLine(
            $"OK ({call.TotalElapsedMs / 1000.0:F1}s | ingPatches={call.Parsed.IngredientPatches.Count}, stepPatches={call.Parsed.StepPatches.Count}, stepWarn={stepWarnings})"
        );

        return (
            new PolishSummaryMetrics(
                "targeted",
                Success: true,
                call.TotalElapsedMs,
                call.Parsed.PolishNotes.Count,
                stepWarnings,
                polished.ProseOnlyIngredients.Count,
                call.Parsed.PolishNotes,
                call.ParseError
            ),
            polished
        );
    }

    private static AssembledRecipeState ApplyTargetedPatches(
        AssembledRecipeState baseline,
        PolishTargetedResponseDto patch
    )
    {
        var patchedIngredients = baseline.Ingredients.ToList();
        foreach (var ingPatch in patch.IngredientPatches)
        {
            var idx = patchedIngredients.FindIndex(i => i.Index == ingPatch.Index);
            if (idx < 0)
            {
                continue;
            }

            var current = patchedIngredients[idx];
            var ext = current.Extracted;
            var updatedExt = ext with
            {
                Name = ingPatch.Name ?? ext.Name,
                Quantity = ingPatch.Quantity ?? ext.Quantity,
                MaxQuantity = ingPatch.MaxQuantity ?? ext.MaxQuantity,
                Unit = ingPatch.Unit ?? ext.Unit,
                PrepNote = ingPatch.PrepNote ?? ext.PrepNote,
                IsOptional = ingPatch.IsOptional ?? ext.IsOptional,
            };
            patchedIngredients[idx] = current with { Extracted = updatedExt };
        }

        var stepPatchByNum = patch
            .StepPatches.GroupBy(s => s.StepNumber)
            .ToDictionary(g => g.Key, g => g.Last());

        var patchedSteps = baseline
            .Steps.Select(s =>
            {
                if (!stepPatchByNum.TryGetValue(s.StepNumber, out var stepPatch))
                {
                    return s;
                }

                return BuildStepResult(
                    s.StepNumber,
                    s.SectionName,
                    s.OriginalInstruction,
                    stepPatch.Tokens,
                    patchedIngredients.Count
                );
            })
            .ToList();

        var proseOnly = ComputeProseOnlyIngredients(patchedIngredients, patchedSteps);
        return new AssembledRecipeState(
            patch.Title ?? baseline.Title,
            patch.Description ?? baseline.Description,
            patch.Servings ?? baseline.Servings,
            patch.Yield ?? baseline.Yield,
            patch.Timing ?? baseline.Timing,
            patch.Tags ?? baseline.Tags,
            patchedIngredients,
            patchedSteps,
            proseOnly
        );
    }

    private async Task<(
        PolishSummaryMetrics Metrics,
        AssembledRecipeState? State
    )> RunFullPolishAsync(
        string recipeDir,
        string userPayload,
        AssembledRecipeState baseline,
        CancellationToken cancellationToken
    )
    {
        var dir = Path.Combine(recipeDir, "05-polish-full");
        Directory.CreateDirectory(dir);
        Console.Write("  Stage 5b (Full polish)... ");

        var call = await llmClient.CompleteStructuredAsync<PolishFullResponseDto>(
            config,
            "polish_full",
            PipelineSchemas.Stage5PolishFullSchema(),
            prompts.PolishFull.Content,
            userPayload,
            isAuxiliaryStep: false,
            cancellationToken: cancellationToken
        );

        await WriteExchangesAsync(dir, call.Exchanges, cancellationToken);
        await WriteJsonAsync(
            Path.Combine(dir, "output.json"),
            new
            {
                call.TotalElapsedMs,
                call.ParseError,
                polished = call.Parsed,
            },
            cancellationToken
        );

        if (call.Parsed is null)
        {
            Console.WriteLine($"FAILED ({call.ParseError})");
            return (
                new PolishSummaryMetrics(
                    "full",
                    Success: false,
                    call.TotalElapsedMs,
                    0,
                    0,
                    0,
                    [],
                    call.ParseError
                ),
                null
            );
        }

        var polished = MapFullPolishState(baseline, call.Parsed);
        await WriteAssembledRecipeJsonAsync(
            Path.Combine(dir, "final-recipe.json"),
            polished,
            call.Parsed.PolishNotes,
            cancellationToken
        );

        var stepWarnings = polished.Steps.Sum(s => s.ValidationWarnings.Count);
        Console.WriteLine(
            $"OK ({call.TotalElapsedMs / 1000.0:F1}s | ings={polished.Ingredients.Count}, steps={polished.Steps.Count}, stepWarn={stepWarnings})"
        );

        return (
            new PolishSummaryMetrics(
                "full",
                Success: true,
                call.TotalElapsedMs,
                call.Parsed.PolishNotes.Count,
                stepWarnings,
                polished.ProseOnlyIngredients.Count,
                call.Parsed.PolishNotes,
                call.ParseError
            ),
            polished
        );
    }

    private static AssembledRecipeState MapFullPolishState(
        AssembledRecipeState baseline,
        PolishFullResponseDto full
    )
    {
        var baselineByIdx = baseline.Ingredients.ToDictionary(i => i.Index);
        var mappedIngredients = full
            .Ingredients.Select(ing =>
            {
                Stage3MatchDecisionDto? resolution = null;
                if (
                    baselineByIdx.TryGetValue(ing.Index, out var orig)
                    && string.Equals(
                        orig.Extracted.Name.Trim(),
                        ing.Name.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    resolution = orig.Resolution;
                }

                return new AssembledIngredient(
                    ing.Index,
                    ing.Section,
                    new Stage1IngredientDto(
                        ing.Name,
                        ing.Quantity,
                        ing.MaxQuantity,
                        ing.Unit,
                        ing.PrepNote,
                        ing.IsOptional,
                        ing.Replacements,
                        ing.AlternateMeasurements
                    ),
                    resolution
                );
            })
            .ToList();

        var baselineStepsByNum = baseline.Steps.ToDictionary(s => s.StepNumber);
        var mappedSteps = full
            .Steps.Select(step =>
            {
                baselineStepsByNum.TryGetValue(step.StepNumber, out var origStep);
                return BuildStepResult(
                    step.StepNumber,
                    step.SectionName ?? origStep?.SectionName,
                    origStep?.OriginalInstruction ?? string.Empty,
                    step.Tokens,
                    mappedIngredients.Count
                );
            })
            .ToList();

        var proseOnly = ComputeProseOnlyIngredients(mappedIngredients, mappedSteps);
        return new AssembledRecipeState(
            full.Title,
            full.Description,
            full.Servings,
            full.Yield,
            full.Timing,
            full.Tags,
            mappedIngredients,
            mappedSteps,
            proseOnly
        );
    }

    private static Task WriteAssembledRecipeJsonAsync(
        string path,
        AssembledRecipeState state,
        IReadOnlyList<string>? polishNotes,
        CancellationToken cancellationToken
    )
    {
        return WriteJsonAsync(
            path,
            new
            {
                state.Title,
                state.Description,
                state.Servings,
                state.Yield,
                state.Timing,
                state.Tags,
                ingredients = state.Ingredients.Select(i => new
                {
                    i.Index,
                    i.Section,
                    extracted = i.Extracted,
                    resolution = i.Resolution,
                }),
                steps = state.Steps.Select(s => new
                {
                    s.StepNumber,
                    s.SectionName,
                    s.OriginalInstruction,
                    tokens = s.RawTokensJson,
                    rendered = new
                    {
                        original = s.RenderedOriginal,
                        scaled2x = s.RenderedScaled2x,
                        metric1x = s.RenderedMetric1x,
                    },
                    s.ValidationWarnings,
                }),
                proseOnlyIngredients = state.ProseOnlyIngredients,
                polishNotes,
            },
            cancellationToken
        );
    }

    private static Task WritePolishComparisonMarkdownAsync(
        string path,
        AssembledRecipeState baseline,
        PolishSummaryMetrics? targetedMetrics,
        AssembledRecipeState? targetedState,
        PolishSummaryMetrics? fullMetrics,
        AssembledRecipeState? fullState,
        CancellationToken cancellationToken
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Stage 5 Polish Comparison: {baseline.Title}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Baseline (Stages 1-4) | Targeted Polish | Full Polish |");
        sb.AppendLine("| --- | --- | --- | --- |");

        var baseWarn = baseline.Steps.Sum(s => s.ValidationWarnings.Count);
        var tgtTime = targetedMetrics is not null
            ? $"{targetedMetrics.ElapsedMs / 1000.0:F1}s"
            : "n/a";
        var fullTime = fullMetrics is not null ? $"{fullMetrics.ElapsedMs / 1000.0:F1}s" : "n/a";
        sb.AppendLine($"| Polish Time | — | {tgtTime} | {fullTime} |");
        sb.AppendLine(
            $"| Ingredients Count | {baseline.Ingredients.Count} | {targetedState?.Ingredients.Count.ToString() ?? "failed"} | {fullState?.Ingredients.Count.ToString() ?? "failed"} |"
        );
        sb.AppendLine(
            $"| Step Validation Warnings | {baseWarn} | {targetedMetrics?.StepWarningsAfterPolish.ToString() ?? "failed"} | {fullMetrics?.StepWarningsAfterPolish.ToString() ?? "failed"} |"
        );
        sb.AppendLine(
            $"| Prose-Only Master Ingredients | {baseline.ProseOnlyIngredients.Count} | {targetedState?.ProseOnlyIngredients.Count.ToString() ?? "failed"} | {fullState?.ProseOnlyIngredients.Count.ToString() ?? "failed"} |"
        );
        sb.AppendLine(
            $"| Tags | {string.Join(", ", baseline.Tags)} | {(targetedState is not null ? string.Join(", ", targetedState.Tags) : "failed")} | {(fullState is not null ? string.Join(", ", fullState.Tags) : "failed")} |"
        );

        sb.AppendLine();
        sb.AppendLine("## Polish Notes");
        sb.AppendLine();
        sb.AppendLine("### Targeted Polish");
        if (targetedMetrics?.PolishNotes.Count > 0)
        {
            foreach (var note in targetedMetrics.PolishNotes)
            {
                sb.AppendLine($"- {note}");
            }
        }
        else
        {
            sb.AppendLine($"- *(none / parse error: {targetedMetrics?.ParseError})*");
        }

        sb.AppendLine();
        sb.AppendLine("### Full Polish");
        if (fullMetrics?.PolishNotes.Count > 0)
        {
            foreach (var note in fullMetrics.PolishNotes)
            {
                sb.AppendLine($"- {note}");
            }
        }
        else
        {
            sb.AppendLine($"- *(none / parse error: {fullMetrics?.ParseError})*");
        }

        sb.AppendLine();
        sb.AppendLine("## Step-by-Step Rendered Changes");
        foreach (var baseStep in baseline.Steps)
        {
            var tgtStep = targetedState?.Steps.FirstOrDefault(s =>
                s.StepNumber == baseStep.StepNumber
            );
            var fullStep = fullState?.Steps.FirstOrDefault(s =>
                s.StepNumber == baseStep.StepNumber
            );

            var baseTokensJson = baseStep.RawTokensJson.ToJsonString();
            var changed =
                (
                    tgtStep is not null
                    && (
                        tgtStep.RenderedOriginal != baseStep.RenderedOriginal
                        || tgtStep.RawTokensJson.ToJsonString() != baseTokensJson
                    )
                )
                || (
                    fullStep is not null
                    && (
                        fullStep.RenderedOriginal != baseStep.RenderedOriginal
                        || fullStep.RawTokensJson.ToJsonString() != baseTokensJson
                    )
                );

            if (!changed)
            {
                continue;
            }

            static int CountNonText(StepTokenizeResult s) =>
                s.ParsedTokens.Count(t => t is not PocTextToken);

            sb.AppendLine();
            sb.AppendLine($"### Step {baseStep.StepNumber}");
            sb.AppendLine($"- **Source:** {baseStep.OriginalInstruction}");
            sb.AppendLine(
                $"- **Baseline ({CountNonText(baseStep)} tokens, {baseStep.ValidationWarnings.Count} warn):** {baseStep.RenderedOriginal}"
            );
            if (tgtStep is not null)
            {
                sb.AppendLine(
                    $"- **Targeted ({CountNonText(tgtStep)} tokens, {tgtStep.ValidationWarnings.Count} warn):** {tgtStep.RenderedOriginal}"
                );
            }
            if (fullStep is not null)
            {
                sb.AppendLine(
                    $"- **Full ({CountNonText(fullStep)} tokens, {fullStep.ValidationWarnings.Count} warn):** {fullStep.RenderedOriginal}"
                );
            }
        }

        return File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static List<string> ValidateIngredientDecision(
        Stage3MatchDecisionDto decision,
        DbSearchCandidates candidates
    )
    {
        var warnings = new List<string>();
        switch (decision.MatchType?.Trim().ToLowerInvariant())
        {
            case "existing_ingredient":
                if (
                    !Guid.TryParse(decision.Id, out var foodGuid)
                    || !candidates.ExistingIngredients.Any(c => c.Id == foodGuid)
                )
                {
                    warnings.Add(
                        $"Hallucinated or missing FoodItem id '{decision.Id}' not in DB candidates."
                    );
                }
                break;
            case "existing_recipe":
                if (
                    !Guid.TryParse(decision.Id, out var recipeGuid)
                    || !candidates.ExistingRecipes.Any(r => r.Id == recipeGuid)
                )
                {
                    warnings.Add(
                        $"Hallucinated or missing Recipe id '{decision.Id}' not in DB candidates."
                    );
                }
                break;
            case "new_ingredient":
                if (string.IsNullOrWhiteSpace(decision.Name))
                {
                    warnings.Add("new_ingredient proposed with blank name.");
                }
                if (FoodCategories.Parse(decision.Category) is null)
                {
                    warnings.Add($"Invalid FoodCategory '{decision.Category}'.");
                }
                break;
            case "unresolved_recipe":
                if (string.IsNullOrWhiteSpace(decision.Name))
                {
                    warnings.Add("unresolved_recipe proposed with blank name.");
                }
                break;
            default:
                warnings.Add($"Unknown matchType '{decision.MatchType}'.");
                break;
        }
        return warnings;
    }

    private static async Task WriteExchangesAsync(
        string dir,
        IReadOnlyList<LlmExchangeRecord> exchanges,
        CancellationToken cancellationToken,
        string prefix = ""
    )
    {
        for (var i = 0; i < exchanges.Count; i++)
        {
            var ex = exchanges[i];
            var suffix = exchanges.Count == 1 ? string.Empty : $"-attempt-{i + 1}";
            await WriteJsonAsync(
                Path.Combine(dir, $"{prefix}request{suffix}.json"),
                ex.RequestPayload,
                cancellationToken
            );

            JsonNode? parsedRaw = null;
            if (!string.IsNullOrWhiteSpace(ex.RawResponseBody))
            {
                try
                {
                    parsedRaw = JsonNode.Parse(ex.RawResponseBody);
                }
                catch
                {
                    // fallback to string
                }
            }

            await WriteJsonAsync(
                Path.Combine(dir, $"{prefix}response{suffix}.json"),
                new
                {
                    ex.Attempt,
                    ex.ThinkingUsed,
                    ex.ElapsedMs,
                    ex.HttpStatus,
                    ex.PromptEvalCount,
                    ex.EvalCount,
                    ex.DoneReason,
                    ex.Error,
                    ex.ExtractedThinking,
                    ex.ExtractedContent,
                    rawBody = parsedRaw ?? (object)ex.RawResponseBody,
                },
                cancellationToken
            );
        }
    }

    private static Task WriteJsonAsync(
        string path,
        object? payload,
        CancellationToken cancellationToken
    )
    {
        var json = JsonSerializer.Serialize(payload, PrettyJson);
        return File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
    }

    private static string Slugify(string text)
    {
        var slug = SlugRegex().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrEmpty(slug))
        {
            return "item";
        }
        return slug.Length > 45 ? slug[..45].TrimEnd('-') : slug;
    }

    private string BuildMarkdownSummary(IReadOnlyList<RecipeRunSummary> summaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Ingestion PoC Run Summary");
        sb.AppendLine();
        sb.AppendLine(
            $"- **Provider / Endpoint / Base URL:** `{config.Provider}` (`{config.EndpointStyle}`) @ `{config.BaseUrl}`"
        );
        sb.AppendLine(
            $"- **Model:** `{config.TextModel}` (thinking: `{config.EnableThinking}`, auxThinking: `{config.AuxThinkingOverride?.ToString() ?? "inherit"}`, context: `{config.ContextWindowTokens}`)"
        );
        sb.AppendLine($"- **Polish Mode:** `{options.PolishMode}`");
        sb.AppendLine(
            $"- **Database Catalog:** {(catalog.LoadedFromDatabase ? $"{catalog.FoodItems.Count} food items, {catalog.Recipes.Count} recipes" : "Offline / Disabled")}"
        );
        sb.AppendLine();
        sb.AppendLine(
            "| Recipe | Time | Stage 1 | Stage 2 | DB Ing | DB Rec | New Ing | Unres Rec | Ing Warn | Step Warn | Prose-Only Ing | Targeted Polish | Full Polish |"
        );
        sb.AppendLine(
            "| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |"
        );

        static string FormatPolishCell(PolishSummaryMetrics? m) =>
            m is null ? "—"
            : m.Success
                ? $"{m.ElapsedMs / 1000.0:F1}s ({m.StepWarningsAfterPolish}w / {m.PolishNotesCount}n)"
            : "FAILED";

        foreach (var s in summaries)
        {
            var s1 = s.Stage1Success ? $"{s.Stage1Ingredients}i / {s.Stage1Steps}s" : "FAILED";
            var s2 = s.Stage2Ran
                ? $"{s.OriginalDescriptionLength}->{s.FinalDescriptionLength}"
                : "skip";
            sb.AppendLine(
                $"| {s.Title} | {s.TotalElapsedMs / 1000.0:F1}s | {s1} | {s2} | {s.ExistingIngredientMatches} | {s.ExistingRecipeMatches} | {s.NewIngredientsProposed} | {s.UnresolvedRecipes} | {s.IngredientValidationWarnings} | {s.StepValidationWarnings} | {s.ProseOnlyIngredients.Count} | {FormatPolishCell(s.TargetedPolish)} | {FormatPolishCell(s.FullPolish)} |"
            );
        }

        foreach (
            var s in summaries.Where(x =>
                x.FatalError is not null
                || x.ProseOnlyIngredients.Count > 0
                || x.TargetedPolish is not null
                || x.FullPolish is not null
            )
        )
        {
            sb.AppendLine();
            sb.AppendLine($"### {s.Title}");
            if (s.FatalError is not null)
            {
                sb.AppendLine($"- **Fatal Error:** {s.FatalError}");
            }
            if (s.ProseOnlyIngredients.Count > 0)
            {
                sb.AppendLine(
                    $"- **Prose-Only Master Ingredients (no quantitative quantity in steps):** {string.Join(", ", s.ProseOnlyIngredients)}"
                );
            }
            if (s.TargetedPolish is { } tgt)
            {
                sb.AppendLine(
                    $"- **Targeted Polish Notes ({tgt.ElapsedMs / 1000.0:F1}s):** {(tgt.PolishNotes.Count > 0 ? string.Join(" | ", tgt.PolishNotes) : tgt.ParseError ?? "none")}"
                );
            }
            if (s.FullPolish is { } full)
            {
                sb.AppendLine(
                    $"- **Full Polish Notes ({full.ElapsedMs / 1000.0:F1}s):** {(full.PolishNotes.Count > 0 ? string.Join(" | ", full.PolishNotes) : full.ParseError ?? "none")}"
                );
            }
        }

        return sb.ToString();
    }
}
