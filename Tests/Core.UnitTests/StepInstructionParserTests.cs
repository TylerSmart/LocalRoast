using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class StepInstructionParserTests
{
    [TestMethod]
    public void ParseSegments_WithNoTokens_ReturnsSinglePlainSegment()
    {
        const string text = "Preheat the oven to 350 degrees.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.AreEqual(1, segments.Count);
        Assert.AreEqual(text, segments[0].Text);
        Assert.IsNull(segments[0].Token);
        Assert.IsFalse(segments[0].IsToken);
    }

    [TestMethod]
    public void ParseSegments_WithIngredientToken_ParsesCorrectly()
    {
        const string text =
            "Top each of 4 pancakes with ||{\"ingredient\": \"butter\", \"amount\": 4, \"unit\": \"teaspoon\", \"displayAmount\": 1, \"displayUnit\": \"teaspoon\"}|| and serve.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.AreEqual(3, segments.Count);
        Assert.AreEqual("Top each of 4 pancakes with ", segments[0].Text);
        Assert.IsNull(segments[0].Token);

        Assert.IsTrue(segments[1].IsToken);
        var token = segments[1].Token as StepIngredientToken;
        Assert.IsNotNull(token);
        Assert.AreEqual("butter", token.Ingredient);
        Assert.AreEqual(4m, token.Amount);
        Assert.AreEqual(MeasurementUnit.Tsp, token.Unit);
        Assert.AreEqual(1m, token.DisplayAmount);
        Assert.AreEqual(MeasurementUnit.Tsp, token.DisplayUnit);

        Assert.AreEqual(" and serve.", segments[2].Text);
        Assert.IsNull(segments[2].Token);
    }

    [TestMethod]
    public void ParseSegments_WithTimerToken_ParsesCorrectly()
    {
        const string text =
            "Bake for ||{\"type\": \"timer\", \"seconds\": 1200, \"label\": \"Bake\"}|| until golden.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.AreEqual(3, segments.Count);
        Assert.IsTrue(segments[1].IsToken);

        var timer = segments[1].Token as StepTimerToken;
        Assert.IsNotNull(timer);
        Assert.AreEqual(1200, timer.Seconds);
        Assert.AreEqual("Bake", timer.Label);
    }

    [TestMethod]
    public void ParseSegments_WithTemperatureToken_ParsesCorrectly()
    {
        const string text = "Heat oven to ||{\"type\": \"temperature\", \"fahrenheit\": 425}||.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.AreEqual(3, segments.Count);
        Assert.IsTrue(segments[1].IsToken);

        var temp = segments[1].Token as StepTemperatureToken;
        Assert.IsNotNull(temp);
        Assert.AreEqual(425, temp.Fahrenheit);
    }

    [TestMethod]
    public void ParseSegments_WithMalformedJson_FallsBackToPlainTextWithoutThrowing()
    {
        const string text = "Add ||{\"broken: json}|| and stir.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.IsFalse(segments.Any(s => s.IsToken));
    }

    [TestMethod]
    public void ToPlainText_FormatsDisplayAmountAndIngredient()
    {
        const string text =
            "Top each pancake with ||{\"ingredient\": \"butter\", \"amount\": 4, \"unit\": \"tsp\", \"displayAmount\": 1, \"displayUnit\": \"tsp\"}||.";
        var plain = StepInstructionParser.ToPlainText(text);

        Assert.AreEqual("Top each pancake with 1 tsp butter.", plain);
    }

    [TestMethod]
    public void FormatToken_SerializesIngredientToken()
    {
        var token = new StepIngredientToken(
            Ingredient: "butter",
            Amount: 4m,
            Unit: MeasurementUnit.Tsp,
            DisplayAmount: 1m,
            DisplayUnit: MeasurementUnit.Tsp
        );

        var formatted = StepInstructionParser.FormatToken(token);
        Assert.IsTrue(formatted.StartsWith("||{") && formatted.EndsWith("}||"));
        Assert.IsTrue(formatted.Contains("\"ingredient\":\"butter\""));
        Assert.IsTrue(formatted.Contains("\"amount\":4"));
        Assert.IsTrue(formatted.Contains("\"displayAmount\":1"));
    }

    [TestMethod]
    public void ParseSegments_WithTrailingPunctuationInsideDelimiters_ExtractsPunctuationAsText()
    {
        const string text =
            "Process until combined, ||{\"type\": \"timer\", \"seconds\": 10}!!.|| and rest.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.IsTrue(segments.Any(s => s.Token is StepTimerToken));
        var timer = segments.First(s => s.Token is StepTimerToken).Token as StepTimerToken;
        Assert.AreEqual(10, timer!.Seconds);

        // Punctuation "!!." must be preserved as trailing text segment
        Assert.IsTrue(segments.Any(s => s.Text == "!!." && s.Token == null));
    }

    [TestMethod]
    public void ParseSegments_WithMissingIngredientProperty_FallsBackToIngredientToken()
    {
        const string text =
            "Reserve ||{\"amount\": 1, \"unit\": \"tbsp\", \"displayAmount\": 1, \"displayUnit\": \"tbsp\"}|| fat.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.IsTrue(segments.Any(s => s.Token is StepIngredientToken));
        var ing = segments.First(s => s.Token is StepIngredientToken).Token as StepIngredientToken;
        Assert.IsNotNull(ing);
        Assert.AreEqual(1m, ing.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, ing.Unit);
    }

    [TestMethod]
    public void CleanStepInstruction_StripsPrecedingDuplicateMeasurement()
    {
        const string text =
            "Transfer onion mixture to bowl and stir in 1 1/2 tablespoons ||{\"ingredient\": \"lime juice\", \"amount\": 1.5, \"unit\": \"tbsp\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(
            "Transfer onion mixture to bowl and stir in ||{\"ingredient\": \"lime juice\", \"amount\": 1.5, \"unit\": \"tbsp\"}||.",
            cleaned
        );
    }

    [TestMethod]
    public void CleanStepInstruction_PreservesWordsEndingInUnitLetters()
    {
        const string text =
            "While onion cooks, whisk eggs, remaining ||{\"ingredient\": \"pepper\", \"amount\": 0.25, \"unit\": \"tsp\"}||, and cook in oil ||{\"ingredient\": \"oil\", \"amount\": 1, \"unit\": \"tbsp\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(
            "While onion cooks, whisk eggs, remaining ||{\"ingredient\": \"pepper\", \"amount\": 0.25, \"unit\": \"tsp\"}||, and cook in oil ||{\"ingredient\": \"oil\", \"amount\": 1, \"unit\": \"tbsp\"}||.",
            cleaned
        );
    }

    [TestMethod]
    public void CleanStepInstruction_StripsPrecedingDuplicateMeasurement_WithRemaining()
    {
        const string text =
            "While onion cooks, whisk eggs, remaining 1/8 teaspoon ||{\"ingredient\": \"salt\", \"amount\": 0.125, \"unit\": \"tsp\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(
            "While onion cooks, whisk eggs, remaining ||{\"ingredient\": \"salt\", \"amount\": 0.125, \"unit\": \"tsp\"}||.",
            cleaned
        );
    }

    [TestMethod]
    public void CleanStepInstruction_WithRedundantDurationTimerLabel_StripsLabel()
    {
        const string text =
            "Let sit for ||{\"type\": \"timer\", \"seconds\": 300, \"label\": \"5 minutes\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual("Let sit for ||{\"seconds\":300,\"type\":\"Timer\"}||.", cleaned);

        var plain = StepInstructionParser.ToPlainText(cleaned);
        Assert.AreEqual("Let sit for 5 minutes.", plain);
    }

    [TestMethod]
    public void CleanStepInstruction_WithActionTimerLabel_PreservesLabel()
    {
        const string text =
            "Simmer for ||{\"type\": \"timer\", \"seconds\": 300, \"label\": \"Simmer sauce\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(text, cleaned);

        var plain = StepInstructionParser.ToPlainText(cleaned);
        Assert.AreEqual("Simmer for Simmer sauce.", plain);
    }

    [TestMethod]
    public void NormalizeBareTokens_WrapsBareTimerToken()
    {
        const string text =
            "Broil until softened, about {\"type\": \"timer\", \"seconds\": 1500, \"label\": \"Broil\"}.";
        var normalized = StepInstructionParser.NormalizeBareTokens(text);

        Assert.AreEqual(
            "Broil until softened, about ||{\"type\": \"timer\", \"seconds\": 1500, \"label\": \"Broil\"}||.",
            normalized
        );
    }

    [TestMethod]
    public void NormalizeBareTokens_DoesNotTouchAlreadyWrappedTokens()
    {
        const string text =
            "Bake for ||{\"type\": \"timer\", \"seconds\": 1200}|| at ||{\"type\": \"temperature\", \"fahrenheit\": 400}||.";
        var normalized = StepInstructionParser.NormalizeBareTokens(text);

        Assert.AreEqual(text, normalized);
    }

    [TestMethod]
    public void ParseSegments_WithBareTimerToken_AutoHealsAndParses()
    {
        const string text =
            "Broil until softened, about {\"type\": \"timer\", \"seconds\": 1500, \"label\": \"Broil\"}.";
        var segments = StepInstructionParser.ParseSegments(text);

        Assert.AreEqual(3, segments.Count);
        Assert.IsTrue(segments[1].IsToken);
        var timer = segments[1].Token as StepTimerToken;
        Assert.IsNotNull(timer);
        Assert.AreEqual(1500, timer.Seconds);
        Assert.AreEqual("Broil", timer.Label);
    }

    [TestMethod]
    public void CleanStepInstruction_WithBareTimerToken_NormalizesWithDelimiters()
    {
        const string text =
            "Broil until softened, about {\"type\": \"timer\", \"seconds\": 1500, \"label\": \"Broil\"}.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(
            "Broil until softened, about ||{\"type\": \"timer\", \"seconds\": 1500, \"label\": \"Broil\"}||.",
            cleaned
        );
    }

    [TestMethod]
    public void CleanStepInstruction_CollapsesAdjacentRangeTimerTokens_ToLowerBound()
    {
        const string text =
            "Fry potatoes until golden, ||{\"type\": \"timer\", \"seconds\": 1200, \"label\": \"Fry potatoes\"}|| to ||{\"type\": \"timer\", \"seconds\": 1500, \"label\": \"Fry potatoes\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(
            "Fry potatoes until golden, ||{\"seconds\":1200,\"label\":\"Fry potatoes\",\"type\":\"Timer\"}||.",
            cleaned
        );
    }

    [TestMethod]
    public void CleanStepInstruction_CollapsesDualFahrenheitCelsiusTokens_ToSingleFahrenheitToken()
    {
        const string text =
            "Bring water to ||{\"type\": \"temperature\", \"fahrenheit\": 140}||/||{\"type\": \"temperature\", \"fahrenheit\": 60}|| in container.";
        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual(
            "Bring water to ||{\"fahrenheit\":140,\"type\":\"Temperature\"}|| in container.",
            cleaned
        );
    }

    [TestMethod]
    public void ParseSegments_RepairsUnderscoreDelimiters_ToCanonicalTokens()
    {
        const string text =
            "Add ||{\"ingredient\": \"vanilla extract\", \"amount\": 2, \"unit\": \"tsp\"}__. Season with ||{\"ingredient\": \"table salt\"}__||.";

        var segments = StepInstructionParser.ParseSegments(text);
        var tokens = segments.Where(s => s.Token is not null).Select(s => s.Token).ToList();

        Assert.HasCount(2, tokens);
        var vanilla = tokens[0] as StepIngredientToken;
        Assert.IsNotNull(vanilla);
        Assert.AreEqual("vanilla extract", vanilla.Ingredient);
        Assert.AreEqual(2m, vanilla.Amount);
        Assert.AreEqual(MeasurementUnit.Tsp, vanilla.Unit);

        var salt = tokens[1] as StepIngredientToken;
        Assert.IsNotNull(salt);
        Assert.AreEqual("table salt", salt.Ingredient);

        var cleaned = StepInstructionParser.CleanStepInstruction(text);
        Assert.DoesNotContain("__", cleaned);
        Assert.Contains("||{\"ingredient\": \"vanilla extract\"", cleaned);
        Assert.Contains("||{\"ingredient\": \"table salt\"}||", cleaned);
    }

    [TestMethod]
    public void ParseSegments_MergesSplitJsonObjectsInsideDelimiters()
    {
        const string text =
            "Melt ||{\"ingredient\": \"unsalted butter\", \"amount\": 6, \"unit\": \"tbsp\"}, {\"displayAmount\": 6, \"displayUnit\": \"tbsp\"}|| in a skillet.";

        var segments = StepInstructionParser.ParseSegments(text);
        var token = segments.Select(s => s.Token).OfType<StepIngredientToken>().Single();

        Assert.AreEqual("unsalted butter", token.Ingredient);
        Assert.AreEqual(6m, token.Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, token.Unit);
        Assert.AreEqual(6m, token.DisplayAmount);
        Assert.AreEqual(MeasurementUnit.Tbsp, token.DisplayUnit);
    }

    [TestMethod]
    public void ParseSegments_RepairsMissingColonsAndClosingBraces()
    {
        const string text =
            "Whisk ||{\"ingredient\"cornstarch\", \"amount\": 2, \"unit\": \"tbsp\"}|| and ||{\"ingredient\"Chicken thighs\"|| with ||{\"ingredient\": \"salt\", \"amount\": 1, \"unit\"tbsp\"}||.";

        var segments = StepInstructionParser.ParseSegments(text);
        var tokens = segments.Select(s => s.Token).OfType<StepIngredientToken>().ToList();

        Assert.HasCount(3, tokens);
        Assert.AreEqual("cornstarch", tokens[0].Ingredient);
        Assert.AreEqual(2m, tokens[0].Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, tokens[0].Unit);

        Assert.AreEqual("Chicken thighs", tokens[1].Ingredient);

        Assert.AreEqual("salt", tokens[2].Ingredient);
        Assert.AreEqual(1m, tokens[2].Amount);
        Assert.AreEqual(MeasurementUnit.Tbsp, tokens[2].Unit);
    }

    [TestMethod]
    public void ParseSegments_SupportsPinchAndDashStringAmounts()
    {
        const string text =
            "Season with ||{\"ingredient\": \"salt\", \"amount\": \"pinch\", \"unit\": \"pinch\"}|| and ||{\"ingredient\": \"cayenne\", \"amount\": \"dash\", \"unit\": \"dash\"}||.";

        var segments = StepInstructionParser.ParseSegments(text);
        var tokens = segments.Select(s => s.Token).OfType<StepIngredientToken>().ToList();

        Assert.HasCount(2, tokens);
        Assert.AreEqual(1m, tokens[0].Amount);
        Assert.AreEqual(MeasurementUnit.Pinch, tokens[0].Unit);
        Assert.AreEqual(1m, tokens[1].Amount);
        Assert.AreEqual(MeasurementUnit.Dash, tokens[1].Unit);
    }

    [TestMethod]
    public void ParseSegments_FallsBackToCanonicalAndDisplayNameKeys()
    {
        const string text =
            "Boil ||{\"amount\": 4, \"unit\": \"qt\", \"canonicalFoodName\": \"Water\", \"displayName\": \"Water\"}||.";

        var segments = StepInstructionParser.ParseSegments(text);
        var token = segments.Select(s => s.Token).OfType<StepIngredientToken>().Single();

        Assert.AreEqual("Water", token.Ingredient);
        Assert.AreEqual(4m, token.Amount);
        Assert.AreEqual(MeasurementUnit.Qt, token.Unit);
    }

    [TestMethod]
    public void CleanStepInstruction_ConvertsNamelessIngredientTokenToPlainText()
    {
        const string text =
            "Pour ||{\"amount\": 1.5, \"unit\": \"cup\", \"displayAmount\": 1.5, \"displayUnit\": \"cup\"}|| into the bowl.";

        var cleaned = StepInstructionParser.CleanStepInstruction(text);

        Assert.AreEqual("Pour 1.5 cup into the bowl.", cleaned);
    }

    [TestMethod]
    public void CleanStepInstruction_StripsTrailingDuplicateTimeUnitAndNounEcho()
    {
        const string timerEcho =
            "Blend for about ||{\"seconds\":30,\"label\":\"Blend mixture\",\"type\":\"Timer\"}|| seconds until coarse.";
        Assert.AreEqual(
            "Blend for about ||{\"seconds\":30,\"label\":\"Blend mixture\",\"type\":\"Timer\"}|| until coarse.",
            StepInstructionParser.CleanStepInstruction(timerEcho)
        );

        const string ingEcho =
            "Bring ||{\"ingredient\":\"cold water\",\"amount\":1,\"customUnitName\":\"inch\",\"type\":\"Ingredient\"}|| water to a boil.";
        Assert.AreEqual(
            "Bring ||{\"ingredient\":\"cold water\",\"amount\":1,\"customUnitName\":\"inch\",\"type\":\"Ingredient\"}|| to a boil.",
            StepInstructionParser.CleanStepInstruction(ingEcho)
        );
    }

    [TestMethod]
    public void CleanStepInstruction_PreservesCompoundWordsAfterIngredientToken()
    {
        const string eggWhites =
            "Beat ||{\"ingredient\":\"egg\",\"amount\":2,\"unit\":\"whole\",\"type\":\"Ingredient\"}|| whites until stiff.";
        Assert.AreEqual(eggWhites, StepInstructionParser.CleanStepInstruction(eggWhites));

        const string waterBath =
            "Set pan in ||{\"ingredient\":\"hot water\",\"amount\":2,\"unit\":\"cup\",\"type\":\"Ingredient\"}|| water bath.";
        Assert.AreEqual(waterBath, StepInstructionParser.CleanStepInstruction(waterBath));
    }

    [TestMethod]
    public void CleanStepInstruction_StripsTrailingDegreesAfterTemperatureToken()
    {
        const string single =
            "Bake at ||{\"type\":\"temperature\",\"fahrenheit\":375}|| degrees until golden.";
        Assert.AreEqual(
            "Bake at ||{\"type\":\"temperature\",\"fahrenheit\":375}|| until golden.",
            StepInstructionParser.CleanStepInstruction(single)
        );

        const string range =
            "Heat oven between ||{\"type\":\"temperature\",\"fahrenheit\":350}|| and ||{\"type\":\"temperature\",\"fahrenheit\":375}|| degrees F.";
        Assert.AreEqual(
            "Heat oven between ||{\"type\":\"temperature\",\"fahrenheit\":350}|| and ||{\"type\":\"temperature\",\"fahrenheit\":375}||.",
            StepInstructionParser.CleanStepInstruction(range)
        );
    }

    [TestMethod]
    public void CleanStepInstruction_StripsRedundantParentheticals_WhilePreservingUsefulNotes()
    {
        const string redundantIng =
            "Whisk ||{\"ingredient\":\"cornstarch\",\"amount\":2,\"unit\":\"tbsp\",\"type\":\"Ingredient\"}|| (2 tbsp) and ||{\"ingredient\":\"buttermilk\",\"amount\":0.25,\"unit\":\"cup\",\"type\":\"Ingredient\"}|| (1/4 cup).";
        Assert.AreEqual(
            "Whisk ||{\"ingredient\":\"cornstarch\",\"amount\":2,\"unit\":\"tbsp\",\"type\":\"Ingredient\"}|| and ||{\"ingredient\":\"buttermilk\",\"amount\":0.25,\"unit\":\"cup\",\"type\":\"Ingredient\"}||.",
            StepInstructionParser.CleanStepInstruction(redundantIng)
        );

        const string distinctCanNote =
            "Drain ||{\"ingredient\":\"chickpeas\",\"amount\":1,\"unit\":\"can\",\"type\":\"Ingredient\"}|| (15 ounces).";
        Assert.AreEqual(
            distinctCanNote,
            StepInstructionParser.CleanStepInstruction(distinctCanNote)
        );

        const string redundantTimer =
            "Simmer for ||{\"seconds\":120,\"type\":\"Timer\"}|| (2 minutes) until thickened.";
        Assert.AreEqual(
            "Simmer for ||{\"seconds\":120,\"type\":\"Timer\"}|| until thickened.",
            StepInstructionParser.CleanStepInstruction(redundantTimer)
        );

        const string nonNumericTimerParen =
            "Cook for ||{\"seconds\":120,\"type\":\"Timer\"}|| (or until golden).";
        Assert.AreEqual(
            nonNumericTimerParen,
            StepInstructionParser.CleanStepInstruction(nonNumericTimerParen)
        );
    }

    [TestMethod]
    public void CleanStepInstruction_StripsDuplicateUnitEchoAndQualifierEcho_WithoutTouchingWholeTomatoSlices()
    {
        const string duplicateCustomUnit =
            "Place ||{\"ingredient\":\"feta\",\"amount\":1,\"customUnitName\":\"block\",\"type\":\"Ingredient\"}|| block in skillet and add ||{\"ingredient\":\"tomatoes\",\"amount\":2,\"unit\":\"slice\",\"type\":\"Ingredient\"}|| slices.";
        Assert.AreEqual(
            "Place ||{\"ingredient\":\"feta\",\"amount\":1,\"customUnitName\":\"block\",\"type\":\"Ingredient\"}|| in skillet and add ||{\"ingredient\":\"tomatoes\",\"amount\":2,\"unit\":\"slice\",\"type\":\"Ingredient\"}||.",
            StepInstructionParser.CleanStepInstruction(duplicateCustomUnit)
        );

        const string wholeTomatoSlices =
            "Arrange ||{\"ingredient\":\"tomato\",\"amount\":1,\"unit\":\"whole\",\"type\":\"Ingredient\"}|| slices over feta.";
        Assert.AreEqual(
            wholeTomatoSlices,
            StepInstructionParser.CleanStepInstruction(wholeTomatoSlices)
        );

        const string duplicateQualifier =
            "Top with remaining ||{\"ingredient\":\"feta\",\"displayText\":\"remaining cheese\",\"type\":\"Ingredient\"}|| and ||{\"ingredient\":\"remaining parsley\",\"type\":\"Ingredient\"}||.";
        var cleaned = StepInstructionParser.CleanStepInstruction(duplicateQualifier);
        Assert.AreEqual(
            "Top with remaining cheese and remaining parsley.",
            StepInstructionParser.ToPlainText(cleaned)
        );

        const string unquantifiedIngredientEcho =
            "Stir in remaining ||{\"ingredient\":\"remaining fresh parsley\",\"type\":\"Ingredient\"}||.";
        var cleanedUnquantified = StepInstructionParser.CleanStepInstruction(
            unquantifiedIngredientEcho
        );
        Assert.AreEqual(
            "Stir in remaining ||{\"ingredient\":\"fresh parsley\",\"type\":\"Ingredient\"}||.",
            cleanedUnquantified
        );
    }

    [TestMethod]
    public void CleanStepInstruction_CollapsesBetweenTimerRangeAndDuplicatePrepQualifiers()
    {
        const string betweenTimers =
            "Let yeast stand between ||{\"type\":\"timer\",\"seconds\":120,\"label\":\"Rest yeast\"}|| and ||{\"type\":\"timer\",\"seconds\":300,\"label\":\"Rest yeast\"}|| until foamy.";
        Assert.AreEqual(
            "Let yeast stand about ||{\"seconds\":120,\"label\":\"Rest yeast\",\"type\":\"Timer\"}|| until foamy.",
            StepInstructionParser.CleanStepInstruction(betweenTimers)
        );

        const string duplicatePrep =
            "Add grated ||{\"ingredient\":\"grated butter\",\"displayText\":\"grated butter\",\"type\":\"Ingredient\"}|| and chilled ||{\"ingredient\":\"chilled butter\",\"type\":\"Ingredient\"}||.";
        var cleanedPrep = StepInstructionParser.CleanStepInstruction(duplicatePrep);
        Assert.AreEqual(
            "Add grated butter and chilled butter.",
            StepInstructionParser.ToPlainText(cleanedPrep)
        );
    }

    [TestMethod]
    public void ToPlainText_WithCustomUnitName_UsesCustomUnitInsteadOfLiteralCustom()
    {
        const string text =
            "Place ||{\"ingredient\":\"Brie cheese\",\"amount\":1,\"unit\":\"custom\",\"customUnitName\":\"wheel\",\"type\":\"Ingredient\"}|| in center.";

        Assert.AreEqual(
            "Place 1 wheel Brie cheese in center.",
            StepInstructionParser.ToPlainText(text)
        );
    }

    [TestMethod]
    public void CleanStepInstruction_FixesDuplicateTempPreposition_DoubleOfThe_AndRedundantLeadingMeasure()
    {
        const string duplicateTemp =
            "Heat oven to at ||{\"fahrenheit\":350,\"type\":\"Temperature\"}|| and brush with 2 tablespoons beaten ||{\"ingredient\":\"large egg\",\"amount\":2,\"unit\":\"tbsp\",\"type\":\"Ingredient\"}||.";
        var cleanedTemp = StepInstructionParser.CleanStepInstruction(duplicateTemp);
        Assert.AreEqual(
            "Heat oven to 350°F and brush with beaten 2 tbsp large egg.",
            StepInstructionParser.ToPlainText(cleanedTemp)
        );

        const string duplicateOfThe =
            "Add ||{\"ingredient\":\"ice water\",\"amount\":2,\"unit\":\"tbsp\",\"type\":\"Ingredient\"}|| of the ||{\"ingredient\":\"ice water\",\"amount\":1,\"unit\":\"tbsp\",\"type\":\"Ingredient\"}||.";
        var cleanedOfThe = StepInstructionParser.CleanStepInstruction(duplicateOfThe);
        Assert.AreEqual("Add 2 tbsp ice water.", StepInstructionParser.ToPlainText(cleanedOfThe));
    }
}
