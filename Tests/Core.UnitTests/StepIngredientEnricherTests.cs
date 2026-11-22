using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class StepIngredientEnricherTests
{
    [TestMethod]
    public void EnrichSteps_SingleUseUnquantified_InjectsQuantityCleansTheAndModifierAndAvoidsDuplicateUnit()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                Quantity: 2m,
                Unit: MeasurementUnit.Tbsp,
                DisplayName: "unsalted butter",
                PreparationNote: "melted"
            ),
            new(Quantity: 4m, Unit: MeasurementUnit.Clove, DisplayName: "garlic cloves"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Stir in the melted ||{\"ingredient\":\"butter\"}|| and add the ||{\"ingredient\":\"garlic cloves\"}||."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        Assert.AreEqual("Stir in 2 tbsp melted butter and add 4 clove garlic.", plain);

        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(2, tokens);
        Assert.AreEqual(0, tokens[0].IngredientIndex);
        Assert.AreEqual(2m, tokens[0].Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, tokens[0].Unit);
        Assert.AreEqual(1, tokens[1].IngredientIndex);
        Assert.AreEqual(4m, tokens[1].Amount);
        Assert.AreEqual(MeasurementUnit.Clove, tokens[1].Unit);
        Assert.AreEqual("garlic", tokens[1].Ingredient);
    }

    [TestMethod]
    public void EnrichSteps_HummusPostPrepSubset_NeitherInjectsNorWarns()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                Quantity: 2m,
                Unit: MeasurementUnit.Custom,
                CustomUnitName: "can",
                DisplayName: "chickpeas"
            ),
            new(Quantity: 4m, Unit: MeasurementUnit.Clove, DisplayName: "garlic"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Combine ||{\"ingredient\":\"chickpeas\",\"ingredientIndex\":0}|| and reserve ||{\"ingredient\":\"chickpeas\",\"amount\":2,\"unit\":\"tbsp\",\"ingredientIndex\":0}|| for topping."
            ),
            new(
                Instruction: "Mince ||{\"ingredient\":\"garlic\",\"ingredientIndex\":1}||, then measure out ||{\"ingredient\":\"garlic\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}||."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);
        Assert.AreEqual(
            "Combine chickpeas and reserve 2 tbsp chickpeas for topping.",
            StepInstructionParser.ToPlainText(enriched[0].Instruction)
        );
        Assert.AreEqual(
            "Mince garlic, then measure out 1 tbsp garlic.",
            StepInstructionParser.ToPlainText(enriched[1].Instruction)
        );
    }

    [TestMethod]
    public void EnrichSteps_FractionalAndRemaining_ResolvesDividedButterAmounts()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 4m, Unit: MeasurementUnit.Tbsp, DisplayName: "unsalted butter"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(Instruction: "Melt half of the ||{\"ingredient\":\"butter\"}|| in skillet."),
            new(Instruction: "Swirl in the remaining ||{\"ingredient\":\"butter\"}|| off heat."),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);
        Assert.AreEqual(
            "Melt 2 tbsp butter in skillet.",
            StepInstructionParser.ToPlainText(enriched[0].Instruction)
        );
        Assert.AreEqual(
            "Swirl in the remaining 2 tbsp butter off heat.",
            StepInstructionParser.ToPlainText(enriched[1].Instruction)
        );
    }

    [TestMethod]
    public void EnrichSteps_ToTasteGarnishAndExtra_DoNotInjectAndUnwrapToPlainText()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 1m, Unit: MeasurementUnit.Tsp, DisplayName: "kosher salt"),
            new(Quantity: 2m, Unit: MeasurementUnit.Tbsp, DisplayName: "fresh chives"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Season with ||{\"ingredient\":\"salt\"}|| to taste and sprinkle with ||{\"ingredient\":\"chives\"}|| for garnish."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();

        Assert.HasCount(0, tokens);
        Assert.AreEqual(
            "Season with salt to taste and sprinkle with chives for garnish.",
            enriched[0].Instruction
        );
    }

    [TestMethod]
    public void EnrichSteps_SubRecipeShortNounLinksPlainText_RespectsCountVsAbstractRecipeUnit()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                Quantity: 6m,
                Unit: null,
                DisplayName: "Easy-Peel Hard-Cooked Eggs",
                IsRecipeReference: true,
                ReferencedRecipeTitle: "Easy-Peel Hard-Cooked Eggs"
            ),
            new(
                Quantity: 1m,
                Unit: MeasurementUnit.Custom,
                CustomUnitName: "recipe",
                DisplayName: "Garlic Mayonnaise",
                IsRecipeReference: true,
                ReferencedRecipeTitle: "Garlic Mayonnaise"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(Instruction: "Peel eggs and fold into the mayonnaise."),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();

        Assert.HasCount(2, tokens);
        Assert.AreEqual(0, tokens[0].IngredientIndex);
        Assert.AreEqual("eggs", tokens[0].Ingredient);
        Assert.AreEqual(6m, tokens[0].Amount);

        Assert.AreEqual(1, tokens[1].IngredientIndex);
        Assert.AreEqual("mayonnaise", tokens[1].Ingredient);
        Assert.IsNull(tokens[1].Amount);
    }

    [TestMethod]
    public void EnrichSteps_DistributiveEachDoesNotWarn_TrueOverAllocationWarns()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 4m, Unit: MeasurementUnit.Tbsp, DisplayName: "ricotta"),
            new(Quantity: 2m, Unit: MeasurementUnit.Tbsp, DisplayName: "unsalted butter"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Top each toast with ||{\"ingredient\":\"ricotta\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":0}|| and spoon ||{\"ingredient\":\"ricotta\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":0}|| on each side (6 tbsp equivalent if non-distributive: ||{\"ingredient\":\"ricotta\",\"amount\":6,\"unit\":\"tbsp\",\"ingredientIndex\":0}|| per plate)."
            ),
            new(
                Instruction: "Melt ||{\"ingredient\":\"butter\",\"amount\":2,\"unit\":\"tbsp\",\"ingredientIndex\":1}||, then add ||{\"ingredient\":\"butter\",\"amount\":2,\"unit\":\"tbsp\",\"ingredientIndex\":1}||."
            ),
        };

        var warnings = new List<string>();
        _ = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(1, warnings);
        StringAssert.Contains(warnings[0], "unsalted butter");
    }

    [TestMethod]
    public void EnrichSteps_SkipsSentenceInitialSubjectAndEquipmentFalsePositives()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 4m, Unit: MeasurementUnit.Tbsp, DisplayName: "unsalted butter"),
            new(Quantity: 1m, Unit: MeasurementUnit.Cup, DisplayName: "ice"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "The ||{\"ingredient\":\"butter\"}|| should be softened. Shock bowls in an ice bath."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        Assert.AreEqual("The butter should be softened. Shock bowls in an ice bath.", plain);

        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(0, tokens);
        Assert.AreEqual(
            "The butter should be softened. Shock bowls in an ice bath.",
            enriched[0].Instruction
        );
    }

    [TestMethod]
    public void EnrichSteps_WholeSubRecipeToken_StripsOneWholeAndKeepsLink()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                Quantity: 1m,
                Unit: MeasurementUnit.Whole,
                DisplayName: "Vegan Ranch Dressing",
                IsRecipeReference: true,
                ReferencedRecipeTitle: "Vegan Ranch Dressing"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Serve immediately with ||{\"ingredient\":\"dressing\",\"amount\":1,\"unit\":\"whole\",\"ingredientIndex\":0}||."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        Assert.AreEqual("Serve immediately with dressing.", plain);

        var tok = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.AreEqual(0, tok.IngredientIndex);
        Assert.IsNull(tok.Amount);
        Assert.IsNull(tok.Unit);
    }

    [TestMethod]
    public void EnrichSteps_UnambiguousAliasesLinkPlainText_WithConflictCompoundAndOmitGuards()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 1m, Unit: MeasurementUnit.Tsp, DisplayName: "table salt"),
            new(Quantity: 1m, Unit: null, DisplayName: "plum tomato"),
            new(Quantity: 1m, Unit: null, DisplayName: "serrano chile"),
            new(
                Quantity: 1m,
                Unit: MeasurementUnit.Custom,
                CustomUnitName: "block",
                DisplayName: "feta cheese"
            ),
            new(Quantity: 0.25m, Unit: MeasurementUnit.Tsp, DisplayName: "cayenne pepper"),
            new(Quantity: 0.25m, Unit: null, DisplayName: "green bell pepper"),
            new(Quantity: 12m, Unit: null, DisplayName: "parsley leaves"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Combine chopped tomato, serrano, feta block, bell pepper, and pinch cayenne with salt. Avoid tomato paste and discard garlic powder. Omit parsley garnish."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();

        var linkedByIndex = tokens.ToDictionary(t => t.IngredientIndex!.Value);
        Assert.AreEqual("salt", linkedByIndex[0].Ingredient);
        Assert.AreEqual(1m, linkedByIndex[0].Amount);
        Assert.AreEqual("chopped tomato", linkedByIndex[1].Ingredient);
        Assert.AreEqual(1m, linkedByIndex[1].Amount);
        Assert.AreEqual("serrano", linkedByIndex[2].Ingredient);
        Assert.AreEqual("feta", linkedByIndex[3].Ingredient);
        Assert.AreEqual("cayenne", linkedByIndex[4].Ingredient);
        Assert.AreEqual("bell pepper", linkedByIndex[5].Ingredient);
        Assert.IsFalse(linkedByIndex.ContainsKey(6));

        // Verify compound phrase "tomato paste" stayed intact and wasn't turned into "1 plum tomato paste"
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        StringAssert.Contains(plain, "Avoid tomato paste");
        StringAssert.Contains(plain, "Omit parsley garnish.");
    }

    [TestMethod]
    public void EnrichSteps_RevalidatesDuplicateSectionAndQuantityBindings_EliminatesFalseWarnings()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                SectionName: "Topping Preparation",
                Quantity: 1m,
                Unit: MeasurementUnit.Tbsp,
                DisplayName: "extra-virgin olive oil"
            ),
            new(
                SectionName: null,
                Quantity: 5m,
                Unit: MeasurementUnit.Tbsp,
                DisplayName: "extra-virgin olive oil"
            ),
            new(
                Quantity: 0.25m,
                Unit: MeasurementUnit.Cup,
                DisplayName: "table salt",
                PreparationNote: "for brining"
            ),
            new(
                Quantity: 0.5m,
                Unit: MeasurementUnit.Tsp,
                DisplayName: "table salt",
                PreparationNote: "divided"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                SectionName: "Topping Preparation",
                // Stage 4 wrongly pointed to index 1 (5 tbsp unnamed) instead of index 0 (1 tbsp Topping Preparation)
                Instruction: "Combine ||{\"ingredient\":\"extra-virgin olive oil\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}||."
            ),
            new(
                SectionName: null,
                // Stage 4 wrongly pointed 0.25 cup brining salt to index 3 (0.5 tsp divided) instead of index 2
                Instruction: "Dissolve ||{\"ingredient\":\"salt\",\"amount\":0.25,\"unit\":\"cup\",\"ingredientIndex\":3}|| in water. Coat pan with ||{\"ingredient\":\"oil\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}|| and season with ||{\"ingredient\":\"salt\",\"amount\":0.5,\"unit\":\"tsp\",\"ingredientIndex\":3}||."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);
        var s1Tok = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.AreEqual(0, s1Tok.IngredientIndex);

        var s2Toks = StepInstructionParser
            .ExtractTokens(enriched[1].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.AreEqual(2, s2Toks[0].IngredientIndex);
        Assert.AreEqual(1, s2Toks[1].IngredientIndex);
        Assert.AreEqual(3, s2Toks[2].IngredientIndex);
    }

    [TestMethod]
    public void EnrichSteps_UnlinksDrippingsFatFromOilAndBoilingWaterFromIceWater()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 2.5m, Unit: MeasurementUnit.Tbsp, DisplayName: "vegetable oil"),
            new(
                SectionName: "Crust",
                Quantity: 0.5m,
                Unit: MeasurementUnit.Cup,
                DisplayName: "ice water"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                SectionName: null,
                Instruction: "Use ||{\"ingredient\":\"oil\",\"amount\":2.5,\"unit\":\"tbsp\",\"ingredientIndex\":0}||. Reserve ||{\"ingredient\":\"fat\",\"amount\":3,\"unit\":\"tbsp\",\"ingredientIndex\":0}||."
            ),
            new(
                SectionName: "Filling",
                Instruction: "Bring ||{\"ingredient\":\"water\",\"amount\":4,\"unit\":\"qt\",\"ingredientIndex\":1}|| to boil."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);
        var s1Toks = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(1, s1Toks);
        Assert.AreEqual(0, s1Toks[0].IngredientIndex);
        var s1Mix = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepMixtureToken>()
            .Single();
        Assert.AreEqual("fat", s1Mix.Name);
        Assert.AreEqual(3m, s1Mix.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, s1Mix.Unit);

        var s2Tok = StepInstructionParser
            .ExtractTokens(enriched[1].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.IsNull(s2Tok.IngredientIndex);
    }

    [TestMethod]
    public void EnrichSteps_CollapsesParentheticalDualEquivalentAndOmittingVerb()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 6m, Unit: MeasurementUnit.Oz, DisplayName: "shredded mozzarella"),
            new(Quantity: 1m, Unit: MeasurementUnit.Tsp, DisplayName: "kosher salt"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Follow master recipe, omitting kosher salt. Sprinkle with ||{\"ingredient\":\"shredded mozzarella\",\"amount\":6,\"unit\":\"oz\",\"ingredientIndex\":0}||(||{\"ingredient\":\"shredded mozzarella\",\"amount\":1.5,\"unit\":\"cup\",\"ingredientIndex\":0}||) shredded mozzarella."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        Assert.AreEqual(
            "Follow master recipe, omitting kosher salt. Sprinkle with 6 oz shredded mozzarella.",
            plain
        );

        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(1, tokens);
        Assert.AreEqual(0, tokens[0].IngredientIndex);
    }

    [TestMethod]
    public void EnrichSteps_LinksAccentedAliases_AndMatchesPluralCountForms()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 1m, Unit: null, DisplayName: "jalapeño chile"),
            new(Quantity: 1m, Unit: MeasurementUnit.Tbsp, DisplayName: "fresh parsley"),
            new(Quantity: 12m, Unit: null, DisplayName: "parsley leaves"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Mash avocado with jalapeño. Garnish with ||{\"ingredient\":\"parsley leaf\",\"amount\":1}||."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);
        var tokens = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();

        Assert.HasCount(2, tokens);
        Assert.AreEqual("jalapeño", tokens[0].Ingredient);
        Assert.AreEqual(0, tokens[0].IngredientIndex);
        Assert.AreEqual(1m, tokens[0].Amount);

        Assert.AreEqual("parsley leaf", tokens[1].Ingredient);
        Assert.AreEqual(2, tokens[1].IngredientIndex);
    }

    [TestMethod]
    public void EnrichSteps_SectionScopedAliasesLinkSameSectionMentions_WithoutCrossLeaking()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                SectionName: "Pork",
                Quantity: 5m,
                Unit: MeasurementUnit.Tsp,
                DisplayName: "kosher salt"
            ),
            new(
                SectionName: "Pork",
                Quantity: 2m,
                Unit: MeasurementUnit.Tsp,
                DisplayName: "light brown sugar"
            ),
            new(
                SectionName: "Vinegar Sauce",
                Quantity: 2m,
                Unit: MeasurementUnit.Tbsp,
                DisplayName: "light brown sugar"
            ),
            new(
                SectionName: "Vinegar Sauce",
                Quantity: 1m,
                Unit: MeasurementUnit.Tsp,
                DisplayName: "kosher salt"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(SectionName: "Pork", Instruction: "Combine salt and sugar in small bowl."),
            new(
                SectionName: "Vinegar Sauce",
                Instruction: "Whisk brown sugar and salt until dissolved."
            ),
        };

        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps);

        var porkToks = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(2, porkToks);
        Assert.AreEqual(0, porkToks[0].IngredientIndex);
        Assert.AreEqual(5m, porkToks[0].Amount);
        Assert.AreEqual(1, porkToks[1].IngredientIndex);
        Assert.AreEqual(2m, porkToks[1].Amount);

        var sauceToks = StepInstructionParser
            .ExtractTokens(enriched[1].Instruction)
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(2, sauceToks);
        Assert.AreEqual(2, sauceToks[0].IngredientIndex);
        Assert.AreEqual(2m, sauceToks[0].Amount);
        Assert.AreEqual(3, sauceToks[1].IngredientIndex);
        Assert.AreEqual(1m, sauceToks[1].Amount);
    }

    [TestMethod]
    public void EnrichSteps_RebalancesDuplicateIngredientSiblingAllocations_EliminatingFalseWarnings()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 5m, Unit: MeasurementUnit.Tbsp, DisplayName: "extra-virgin olive oil"),
            new(Quantity: 1m, Unit: MeasurementUnit.Tbsp, DisplayName: "extra-virgin olive oil"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Mix ||{\"ingredient\":\"extra-virgin olive oil\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}|| with tomatoes."
            ),
            new(
                // LLM matched both 1 tbsp uses onto index 1 instead of index 0
                Instruction: "Coat bowl with ||{\"ingredient\":\"of the oil\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}|| and pour ||{\"ingredient\":\"more oil\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}|| over dough."
            ),
            new(
                Instruction: "Coat sheet with ||{\"ingredient\":\"more oil\",\"amount\":2,\"unit\":\"tbsp\",\"ingredientIndex\":0}||."
            ),
            new(
                Instruction: "Brush with ||{\"ingredient\":\"oil\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":1}||."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);

        var allTokens = enriched
            .SelectMany(s => StepInstructionParser.ExtractTokens(s.Instruction))
            .OfType<StepIngredientToken>()
            .ToList();
        Assert.HasCount(5, allTokens);
        Assert.AreEqual(1m, allTokens.Where(t => t.IngredientIndex == 1).Sum(t => t.Amount ?? 0m));
        Assert.AreEqual(5m, allTokens.Where(t => t.IngredientIndex == 0).Sum(t => t.Amount ?? 0m));
    }

    [TestMethod]
    public void EnrichSteps_MarksDistributivePerRollPortionAsFixedIngredient()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 4m, Unit: MeasurementUnit.Tbsp, DisplayName: "unsalted butter"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Spread ||{\"type\":\"ingredient\",\"ingredient\":\"butter\",\"amount\":1,\"unit\":\"tbsp\",\"ingredientIndex\":0}|| on each roll."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings);
        var tok = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.AreEqual(0, tok.IngredientIndex);
        Assert.AreEqual(1m, tok.Amount);
        Assert.IsTrue(tok.Fixed);
    }

    [TestMethod]
    public void EnrichSteps_PreventsCrossSectionSoakingAndTemperingFalseWarnings()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                SectionName: "Slaw",
                Quantity: 0.5m,
                Unit: MeasurementUnit.Cup,
                DisplayName: "water"
            ),
            new(Quantity: 7m, Unit: MeasurementUnit.Cup, DisplayName: "water"),
            new(
                SectionName: "Pastry Cream",
                Quantity: 2.5m,
                Unit: MeasurementUnit.Cup,
                DisplayName: "whole milk"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                SectionName: "Beef",
                Instruction: "Add ||{\"ingredient\":\"water\",\"amount\":1,\"unit\":\"cup\",\"ingredientIndex\":0}||."
            ),
            new(
                SectionName: null,
                Instruction: "Soak lentils in ||{\"ingredient\":\"water\",\"amount\":4,\"unit\":\"cup\",\"ingredientIndex\":1}||; drain well."
            ),
            new(
                SectionName: null,
                Instruction: "Simmer lentils in ||{\"ingredient\":\"water\",\"amount\":7,\"unit\":\"cup\",\"ingredientIndex\":1}||."
            ),
            new(
                SectionName: "Pastry Cream",
                Instruction: "Heat ||{\"ingredient\":\"milk\",\"amount\":2,\"unit\":\"cup\",\"ingredientIndex\":2}||. Add remaining ||{\"ingredient\":\"milk\",\"amount\":0.5,\"unit\":\"cup\",\"ingredientIndex\":2}|| to flour. Slowly add ||{\"ingredient\":\"milk\",\"amount\":0.5,\"unit\":\"cup\",\"ingredientIndex\":2}|| to yolk mixture to temper."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings, string.Join(" | ", warnings));

        // Cross-section Beef water unlinked from Slaw water
        var beefTok = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.IsNull(beefTok.IngredientIndex);

        // Soak-and-drain 4 cup water unlinked while cooking 7 cup water stays linked
        var soakTok = StepInstructionParser
            .ExtractTokens(enriched[1].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.IsNull(soakTok.IngredientIndex);
        var cookTok = StepInstructionParser
            .ExtractTokens(enriched[2].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.AreEqual(1, cookTok.IngredientIndex);

        // Tempering milk transfer converted to StepMixtureToken
        var milkMixToks = StepInstructionParser
            .ExtractTokens(enriched[3].Instruction)
            .OfType<StepMixtureToken>()
            .ToList();
        Assert.HasCount(1, milkMixToks);
        Assert.AreEqual(0.5m, milkMixToks[0].Amount);
        Assert.AreEqual(MeasurementUnit.Cup, milkMixToks[0].Unit);
    }

    [TestMethod]
    public void EnrichSteps_MarksDiscreteSliceFixedAndRepeatPerItemMixtureFixed()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(
                Quantity: 4m,
                Unit: MeasurementUnit.Slice,
                DisplayName: "thin-sliced cooked deli ham"
            ),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Place ||{\"ingredient\":\"thin-sliced cooked deli ham\",\"amount\":1,\"ingredientIndex\":0}|| on each cutlet."
            ),
            new(
                Instruction: "Form ||{\"type\":\"mixture\",\"name\":\"pork mixture\",\"amount\":1,\"unit\":\"tbsp\"}|| into ||{\"type\":\"dimension\",\"value\":1,\"unit\":\"inch\",\"hyphenated\":true}|| meatball; repeat with remaining pork mixture to form 24 meatballs."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings, string.Join(" | ", warnings));
        var hamTok = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Single();
        Assert.IsTrue(hamTok.Fixed);

        var porkMixTok = StepInstructionParser
            .ExtractTokens(enriched[1].Instruction)
            .OfType<StepMixtureToken>()
            .Single();
        Assert.IsTrue(porkMixTok.Fixed);
    }

    [TestMethod]
    public void EnrichSteps_DisambiguatesRedOnionAndLinksSingleUseSpicesVanillaAndWater()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 2m, Unit: null, DisplayName: "onion"),
            new(Quantity: null, Unit: null, DisplayName: "red onion", IsOptional: true),
            new(Quantity: 3m, Unit: MeasurementUnit.Clove, DisplayName: "garlic cloves"),
            new(Quantity: 0.125m, Unit: MeasurementUnit.Tsp, DisplayName: "ground cloves"),
            new(Quantity: 2m, Unit: MeasurementUnit.Tbsp, DisplayName: "sweet smoked paprika"),
            new(Quantity: 2m, Unit: MeasurementUnit.Tsp, DisplayName: "vanilla extract"),
            new(Quantity: 12m, Unit: MeasurementUnit.Cup, DisplayName: "water"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Add onions, garlic, and water. Add paprika, cloves, and vanilla, then stir paprika mixture into pot. Sprinkle with red onion, if using."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings, string.Join(" | ", warnings));
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        Assert.DoesNotContain("red 2 onion", plain);
        StringAssert.Contains(plain, "Sprinkle with red onion, if using.");

        var byIdx = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Where(t => t.IngredientIndex.HasValue)
            .ToDictionary(t => t.IngredientIndex!.Value);

        Assert.AreEqual(2m, byIdx[0].Amount); // onions
        Assert.AreEqual(3m, byIdx[2].Amount); // garlic
        Assert.AreEqual(0.125m, byIdx[3].Amount); // cloves
        Assert.AreEqual(2m, byIdx[4].Amount); // paprika (ignoring "paprika mixture")
        Assert.AreEqual(2m, byIdx[5].Amount); // vanilla
        Assert.AreEqual(12m, byIdx[6].Amount); // water
    }

    [TestMethod]
    public void EnrichSteps_HandlesHyphenBoundarySherryAndPostAdditionStateOrDiscardMentions()
    {
        var ingredients = new List<IngestionDraftIngredientDto>
        {
            new(Quantity: 0.25m, Unit: MeasurementUnit.Cup, DisplayName: "almonds"),
            new(Quantity: 0.25m, Unit: MeasurementUnit.Cup, DisplayName: "dry sherry"),
            new(Quantity: 3m, Unit: MeasurementUnit.Oz, DisplayName: "bacon"),
            new(Quantity: 1m, Unit: null, DisplayName: "bay leaf"),
            new(Quantity: 6m, Unit: MeasurementUnit.Oz, DisplayName: "bittersweet chocolate"),
        };

        var steps = new List<IngestionDraftStepDto>
        {
            new(
                Instruction: "Process almonds until fine and transfer almond-bread mixture to bowl. Fry the bacon until the bacon is crisp. Whisk in the sherry. Stir in bay leaf and simmer; discard the bay leaf. Off heat, add chocolate and let stand until chocolate is melted."
            ),
        };

        var warnings = new List<string>();
        var enriched = StepIngredientEnricher.EnrichSteps(ingredients, steps, warnings);

        Assert.HasCount(0, warnings, string.Join(" | ", warnings));
        var plain = StepInstructionParser.ToPlainText(enriched[0].Instruction);
        StringAssert.Contains(plain, "almond-bread mixture");
        StringAssert.Contains(plain, "until the bacon is crisp");
        StringAssert.Contains(plain, "discard the bay leaf");
        StringAssert.Contains(plain, "until chocolate is melted");

        var byIdx = StepInstructionParser
            .ExtractTokens(enriched[0].Instruction)
            .OfType<StepIngredientToken>()
            .Where(t => t.IngredientIndex.HasValue)
            .ToDictionary(t => t.IngredientIndex!.Value);

        Assert.AreEqual(0.25m, byIdx[0].Amount); // almonds
        Assert.AreEqual(MeasurementUnit.Cup, byIdx[0].Unit);
        Assert.AreEqual(0.25m, byIdx[1].Amount); // dry sherry -> sherry
        Assert.AreEqual(MeasurementUnit.Cup, byIdx[1].Unit);
        Assert.AreEqual(3m, byIdx[2].Amount); // bacon
        Assert.AreEqual(MeasurementUnit.Oz, byIdx[2].Unit);
        Assert.AreEqual(1m, byIdx[3].Amount); // bay leaf
        Assert.AreEqual(6m, byIdx[4].Amount); // bittersweet chocolate -> chocolate
        Assert.AreEqual(MeasurementUnit.Oz, byIdx[4].Unit);
    }
}
