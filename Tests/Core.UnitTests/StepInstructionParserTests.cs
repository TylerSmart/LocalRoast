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
}
