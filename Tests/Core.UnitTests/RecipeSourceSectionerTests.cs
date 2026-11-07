using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeSourceSectionerTests
{
    [TestMethod]
    [DataRow("Ingredients:\n- 1 cup flour\nInstructions:\n1. Mix.", true)]
    [DataRow("Title: x\r\n  ingredients:  \r\n- 1 cup flour\r\nINSTRUCTIONS:\r\nMix.", true)]
    [DataRow("Ingredients:\n1 cup flour\nInstructions:\nMix.", false)]
    [DataRow("Instructions:\nMix.\nIngredients:\n- 1 cup flour", false)]
    [DataRow("Ingredients\n- 1 cup flour\nDirections\nMix.", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void HasCanonicalSections_RequiresAnIngredientListBeforeInstructions(
        string? source,
        bool expected
    ) => Assert.AreEqual(expected, RecipeSourceSectioner.HasCanonicalSections(source));

    [TestMethod]
    public void GetContentLines_TrimsAndDropsBlankLines()
    {
        var lines = RecipeSourceSectioner.GetContentLines("  A  \r\n\r\n\tB\n   \nC");
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, lines.ToArray());
        Assert.AreEqual("1| A\n2| B\n3| C", RecipeSourceSectioner.NumberLines(lines));
    }

    [TestMethod]
    [DataRow("ingredient_wrap", RecipeSourceLineLabel.IngredientWrap)]
    [DataRow(" Title ", RecipeSourceLineLabel.Title)]
    [DataRow("INSTRUCTION", RecipeSourceLineLabel.Instruction)]
    [DataRow("heading", RecipeSourceLineLabel.Skip)]
    [DataRow("", RecipeSourceLineLabel.Skip)]
    [DataRow(null, RecipeSourceLineLabel.Skip)]
    public void ParseLabel_MapsKnownNamesAndSkipsUnknownOnes(
        string? name,
        RecipeSourceLineLabel expected
    ) => Assert.AreEqual(expected, RecipeSourceSectioner.ParseLabel(name));

    [TestMethod]
    public void LabelNames_RoundTripThroughParseLabel()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "title",
                "detail",
                "description",
                "note",
                "ingredient_group",
                "ingredient",
                "ingredient_wrap",
                "instruction",
                "skip",
            },
            RecipeSourceSectioner.LabelNames.ToArray()
        );
        CollectionAssert.AllItemsAreUnique(
            RecipeSourceSectioner.LabelNames.Select(RecipeSourceSectioner.ParseLabel).ToArray()
        );
    }

    [TestMethod]
    public void BuildStructuredText_CopiesLabeledLinesIntoTheCanonicalLayout()
    {
        string[] lines =
        [
            "Weeknight Chili",
            "Prep 10 min | Serves 6",
            "Skip to recipe",
            "This chili is ready in under an hour.",
            "Ingredients",
            "For the chili:",
            "• 2 tbsp olive oil",
            "▢ 1 large onion, finely",
            "chopped",
            "Toppings",
            "- 1 cup sour cream",
            "Method",
            "1. Heat the oil.",
            "2. Add the onion.",
            "Make ahead: refrigerate up to 3 days.",
            "Rate this recipe",
        ];
        var text = RecipeSourceSectioner.BuildStructuredText(
            lines,
            Labels(
                "title",
                "detail",
                "skip",
                "description",
                "skip",
                "ingredient_group",
                "ingredient",
                "ingredient",
                "ingredient_wrap",
                "ingredient_group",
                "ingredient",
                "skip",
                "instruction",
                "instruction",
                "note",
                "skip"
            )
        );
        Assert.AreEqual(
            """
            Title: Weeknight Chili
            Prep 10 min | Serves 6

            Description:
            This chili is ready in under an hour.

            Notes:
            Make ahead: refrigerate up to 3 days.

            Ingredients:
            For the chili:
            - 2 tbsp olive oil
            - 1 large onion, finely chopped
            Toppings
            - 1 cup sour cream

            Instructions:
            1. Heat the oil.
            2. Add the onion.
            """.ReplaceLineEndings("\n"),
            text
        );
        Assert.IsTrue(RecipeSourceSectioner.HasCanonicalSections(text));
    }

    [TestMethod]
    public void BuildStructuredText_TreatsMissingLabelsAsSkipAndKeepsATitlePrefix()
    {
        string[] lines = ["Title: Toast", "1 slice bread", "Toast the bread.", "Rate this recipe"];
        Assert.AreEqual(
            "Title: Toast\n\nIngredients:\n- 1 slice bread\n\nInstructions:\nToast the bread.",
            RecipeSourceSectioner.BuildStructuredText(
                lines,
                Labels("title", "ingredient", "instruction")
            )
        );
    }

    [TestMethod]
    public void BuildStructuredText_JoinsTitleLinesAndSkipsBreadcrumbTitles()
    {
        string[] lines =
        [
            "Home > Pies > Apple Pie",
            "Grandma's",
            "Apple Pie",
            "6 apples",
            "Bake the pie.",
        ];
        var text = RecipeSourceSectioner.BuildStructuredText(
            lines,
            Labels("title", "title", "title", "ingredient", "instruction")
        );
        Assert.AreEqual(
            "Title: Grandma's Apple Pie\n\nIngredients:\n- 6 apples\n\nInstructions:\nBake the pie.",
            text
        );
    }

    [TestMethod]
    public void BuildStructuredText_ReturnsNullWithoutIngredientsOrInstructions()
    {
        string[] lines = ["Toast", "1 slice bread", "Toast the bread."];
        Assert.IsNull(
            RecipeSourceSectioner.BuildStructuredText(lines, Labels("title", "ingredient", "skip"))
        );
        Assert.IsNull(
            RecipeSourceSectioner.BuildStructuredText(lines, Labels("title", "skip", "instruction"))
        );
    }

    [TestMethod]
    public void ApplyLayoutRules_SkipsHeadingsButKeepsLoneAmounts()
    {
        string[] lines =
        [
            "Cornbread",
            "Ingredients:",
            "2",
            "cups cornmeal",
            "Step 1",
            "Mix the cornmeal.",
        ];
        AssertRules(
            lines,
            Labels(
                "title",
                "ingredient",
                "ingredient",
                "ingredient_wrap",
                "instruction",
                "instruction"
            ),
            Labels("title", "skip", "ingredient", "ingredient_wrap", "skip", "instruction")
        );
        StringAssert.Contains(
            RecipeSourceSectioner.BuildStructuredText(
                lines,
                Labels(
                    "title",
                    "ingredient",
                    "ingredient",
                    "ingredient_wrap",
                    "instruction",
                    "instruction"
                )
            ),
            "Ingredients:\n- 2 cups cornmeal\n\nInstructions:\nMix the cornmeal."
        );
    }

    [TestMethod]
    public void ApplyLayoutRules_MethodTextAboveTheIngredientListIsAHeadnote()
    {
        string[] lines =
        [
            "Pan Sauce",
            "We deglazed the pan with wine",
            "for a glossy sauce.",
            "1 cup wine",
            "Simmer the wine.",
        ];
        AssertRules(
            lines,
            Labels("title", "instruction", "instruction", "ingredient", "instruction"),
            Labels("title", "description", "description", "ingredient", "instruction")
        );
    }

    [TestMethod]
    public void ApplyLayoutRules_QuantityLinesBeforeStepOneAreIngredients()
    {
        string[] lines =
        [
            "Crepes",
            "1 cup flour",
            "2 eggs",
            "1 1/4 cups milk",
            "1. Whisk the batter.",
            "2. Cook the crepes.",
        ];
        var labels = Labels(
            "title",
            "ingredient",
            "ingredient",
            "instruction",
            "instruction",
            "instruction"
        );
        AssertRules(
            lines,
            labels,
            Labels("title", "ingredient", "ingredient", "ingredient", "instruction", "instruction")
        );

        // An unnumbered sentence before step 1 is method text, so the run is left alone.
        string[] withIntro = [.. lines[..3], "Heat a crepe pan.", .. lines[4..]];
        AssertRules(withIntro, labels, labels);
    }

    [TestMethod]
    public void ApplyLayoutRules_JoinsHardWrappedContinuations()
    {
        string[] lines =
        [
            "Garlic Bread",
            "Makes about",
            "two loaves",
            "1 loaf bread, split",
            "lengthwise",
            "1 cup chopped walnuts",
            "(optional)",
            "4 tablespoons butter or",
            "olive oil",
            "2 cloves garlic",
            "and a pinch of salt",
            "Spread the butter over the",
            "bread and bake until golden.",
            "Store leftovers in",
            "an airtight container.",
        ];
        var labels = Labels(
            "title",
            "detail",
            "description",
            "ingredient",
            "instruction",
            "ingredient",
            "note",
            "ingredient",
            "ingredient",
            "ingredient",
            "ingredient",
            "instruction",
            "skip",
            "note",
            "ingredient"
        );
        AssertRules(
            lines,
            labels,
            Labels(
                "title",
                "detail",
                "detail",
                "ingredient",
                "ingredient_wrap",
                "ingredient",
                "ingredient_wrap",
                "ingredient",
                "ingredient_wrap",
                "ingredient",
                "ingredient_wrap",
                "instruction",
                "instruction",
                "note",
                "note"
            )
        );
        Assert.AreEqual(
            """
            Title: Garlic Bread
            Makes about two loaves

            Notes:
            Store leftovers in an airtight container.

            Ingredients:
            - 1 loaf bread, split lengthwise
            - 1 cup chopped walnuts (optional)
            - 4 tablespoons butter or olive oil
            - 2 cloves garlic and a pinch of salt

            Instructions:
            Spread the butter over the bread and bake until golden.
            """.ReplaceLineEndings("\n"),
            RecipeSourceSectioner.BuildStructuredText(lines, labels)
        );
    }

    [TestMethod]
    public void ApplyLayoutRules_NumberContinuationsKeepTheSentenceLabel()
    {
        string[] lines =
        [
            "Beef Wellington",
            "1 beef roast",
            "Spread the duxelles over the prosciutto, leaving a",
            "1-inch border on all sides (if it is cold, microwave",
            "for 1 minute).",
        ];
        var labels = Labels("title", "ingredient", "instruction", "note", "note");
        AssertRules(
            lines,
            labels,
            Labels("title", "ingredient", "instruction", "instruction", "instruction")
        );
        StringAssert.EndsWith(
            RecipeSourceSectioner.BuildStructuredText(lines, labels),
            "Instructions:\nSpread the duxelles over the prosciutto, leaving a 1-inch border on all sides (if it is cold, microwave for 1 minute)."
        );
    }

    [TestMethod]
    public void BuildStructuredText_RejoinsNumberedStepsButKeepsMethodHeadings()
    {
        string[] lines =
        [
            "Lemon Squares",
            "1 cup flour",
            "2 lemons",
            "1. Heat the oven to 350 degrees. Press the",
            "crust into the pan.",
            "Bake for 20 minutes.",
            "For the filling",
            "2. Whisk the lemons and pour over the crust.",
            "Serve cold.",
        ];
        var text = RecipeSourceSectioner.BuildStructuredText(
            lines,
            Labels(
                "title",
                "ingredient",
                "ingredient",
                "instruction",
                "instruction",
                "instruction",
                "instruction",
                "instruction",
                "instruction"
            )
        );
        StringAssert.EndsWith(
            text,
            """
            Instructions:
            1. Heat the oven to 350 degrees. Press the crust into the pan. Bake for 20 minutes.
            For the filling
            2. Whisk the lemons and pour over the crust. Serve cold.
            """.ReplaceLineEndings("\n")
        );
    }

    [TestMethod]
    public void BuildStructuredText_RejoinsOnlyWrappedSentencesInUnnumberedText()
    {
        string[] lines =
        [
            "Weeknight Soup",
            "This soup comes together in about thirty minutes with",
            "Parmesan rinds for depth.",
            "1 onion",
            "4 cups broth",
            "Make the base",
            "Cook the onion in a large pot over medium heat until soft, about",
            "8 minutes.",
            "Add the broth and simmer.",
        ];
        var text = RecipeSourceSectioner.BuildStructuredText(
            lines,
            Labels(
                "title",
                "description",
                "description",
                "ingredient",
                "ingredient",
                "instruction",
                "instruction",
                "instruction",
                "instruction"
            )
        );
        Assert.AreEqual(
            """
            Title: Weeknight Soup

            Description:
            This soup comes together in about thirty minutes with Parmesan rinds for depth.

            Ingredients:
            - 1 onion
            - 4 cups broth

            Instructions:
            Make the base
            Cook the onion in a large pot over medium heat until soft, about 8 minutes.
            Add the broth and simmer.
            """.ReplaceLineEndings("\n"),
            text
        );
    }

    [TestMethod]
    public void ApplyLayoutRules_SplitsAndJoinsIngredientsByQuantityShape()
    {
        string[] lines =
        [
            "Pancakes",
            "1 1/2 cups",
            "all-purpose flour",
            "2 eggs",
            "Whisk and cook.",
        ];
        var labels = Labels(
            "title",
            "ingredient_wrap",
            "ingredient",
            "ingredient_wrap",
            "instruction"
        );
        AssertRules(
            lines,
            labels,
            Labels("title", "ingredient", "ingredient_wrap", "ingredient", "instruction")
        );
        StringAssert.Contains(
            RecipeSourceSectioner.BuildStructuredText(lines, labels),
            "Ingredients:\n- 1 1/2 cups all-purpose flour\n- 2 eggs\n"
        );
    }

    [TestMethod]
    public void ApplyLayoutRules_FixesGroupHeadingShapes()
    {
        string[] lines =
        [
            "Noodle Bowl",
            "SAUCE",
            "2 tbsp soy sauce",
            "1 tsp sesame oil",
            "2 cups noodles",
            "Toss everything together.",
        ];
        AssertRules(
            lines,
            Labels(
                "title",
                "ingredient",
                "ingredient",
                "ingredient",
                "ingredient_group",
                "instruction"
            ),
            Labels(
                "title",
                "ingredient_group",
                "ingredient",
                "ingredient",
                "ingredient",
                "instruction"
            )
        );

        // When the whole list is in capitals, capitals do not mark a heading.
        string[] upperCase = ["PANCAKES", "FLOUR", "MILK", "EGGS", "MIX AND COOK."];
        var labels = Labels("title", "ingredient", "ingredient", "ingredient", "instruction");
        AssertRules(upperCase, labels, labels);
    }

    [TestMethod]
    public void BuildStructuredText_UnmarkedLinesInAMarkedListContinueTheItemAbove()
    {
        string[] lines =
        [
            "Hot Cocoa",
            "For the cocoa:",
            "- 1 cup milk, warmed",
            "until steaming",
            "- 2 tbsp cocoa powder",
            "- 1 tbsp sugar",
            "- pinch of salt",
            "Warm the milk and whisk in the cocoa.",
        ];
        var text = RecipeSourceSectioner.BuildStructuredText(
            lines,
            Labels(
                "title",
                "ingredient",
                "ingredient",
                "ingredient",
                "ingredient",
                "ingredient",
                "ingredient",
                "instruction"
            )
        );
        StringAssert.Contains(
            text,
            "Ingredients:\nFor the cocoa:\n- 1 cup milk, warmed until steaming\n- 2 tbsp cocoa powder\n- 1 tbsp sugar\n- pinch of salt\n"
        );
    }

    [TestMethod]
    public void BuildStructuredText_KeepsTheSourceIngredientLinesForDiagnostics()
    {
        string[] lines = ["Pancakes", "1 cup flour", "1 egg", "Whisk everything and cook."];
        var text = RecipeSourceSectioner.BuildStructuredText(
            lines,
            Labels("title", "ingredient", "ingredient", "instruction")
        );
        var recipe = new LlmExtractedRecipePayload(
            Title: "Pancakes",
            ConfidenceScore: 0.9m,
            Ingredients:
            [
                new(null, 1, null, "cup", "flour", "flour", OriginalRawText: "1 cup flour"),
                new(null, 1, null, null, "egg", "egg", OriginalRawText: "1 egg"),
            ],
            Steps: [new(null, "Whisk everything and cook.", [0, 1])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, text));
        var missingEgg = recipe with { Ingredients = [recipe.Ingredients![0]] };
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(missingEgg with { Steps = [new(null, "Whisk.", [0])] }, text)
                .Any(issue => issue.StartsWith("Source ingredient line 2:"))
        );
    }

    private static RecipeSourceLineLabel[] Labels(params string[] names) =>
        names.Select(RecipeSourceSectioner.ParseLabel).ToArray();

    private static void AssertRules(
        string[] lines,
        RecipeSourceLineLabel[] labels,
        RecipeSourceLineLabel[] expected
    ) =>
        CollectionAssert.AreEqual(
            expected,
            RecipeSourceSectioner.ApplyLayoutRules(lines, labels).ToArray()
        );
}
