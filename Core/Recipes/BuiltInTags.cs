namespace App.Core.Recipes;

public record BuiltInTagDefinition(string Name, TagCategory Category, int SortOrder);

public static class BuiltInTags
{
    public static readonly IReadOnlyList<BuiltInTagDefinition> All =
    [
        // ==========================================
        // 1. Meal & Course Type (Course = 0)
        // ==========================================
        // Meal Slots
        new("Breakfast", TagCategory.Course, 10),
        new("Brunch", TagCategory.Course, 20),
        new("Lunch", TagCategory.Course, 30),
        new("Dinner", TagCategory.Course, 40),
        new("Snack", TagCategory.Course, 50),
        new("Midnight Snack", TagCategory.Course, 60),
        new("Dessert", TagCategory.Course, 70),
        // Course Types
        new("Appetizer", TagCategory.Course, 80),
        new("Starter", TagCategory.Course, 90),
        new("Side Dish", TagCategory.Course, 100),
        new("Main Course", TagCategory.Course, 110),
        new("Salad", TagCategory.Course, 120),
        new("Soup", TagCategory.Course, 130),
        new("Stew", TagCategory.Course, 140),
        new("Bread", TagCategory.Course, 150),
        new("Baked Goods", TagCategory.Course, 160),
        new("Sandwich", TagCategory.Course, 170),
        new("Beverage", TagCategory.Course, 180),
        new("Drink", TagCategory.Course, 190),
        new("Cocktail", TagCategory.Course, 200),
        new("Mocktail", TagCategory.Course, 210),
        new("Condiment", TagCategory.Course, 220),
        new("Sauce", TagCategory.Course, 230),
        new("Dip", TagCategory.Course, 240),
        new("Dressing", TagCategory.Course, 250),
        new("Spread", TagCategory.Course, 260),
        new("Marinade", TagCategory.Course, 270),
        // ==========================================
        // 2. Cuisine & Regional Style (Cuisine = 1)
        // ==========================================
        // Americas
        new("American", TagCategory.Cuisine, 10),
        new("Southern", TagCategory.Cuisine, 20),
        new("BBQ", TagCategory.Cuisine, 30),
        new("Tex-Mex", TagCategory.Cuisine, 40),
        new("Mexican", TagCategory.Cuisine, 50),
        new("Cajun", TagCategory.Cuisine, 60),
        new("Creole", TagCategory.Cuisine, 70),
        new("Caribbean", TagCategory.Cuisine, 80),
        new("South American", TagCategory.Cuisine, 90),
        new("Cuban", TagCategory.Cuisine, 100),
        new("Brazilian", TagCategory.Cuisine, 110),
        new("Hawaiian", TagCategory.Cuisine, 120),
        // Europe
        new("Italian", TagCategory.Cuisine, 130),
        new("French", TagCategory.Cuisine, 140),
        new("Spanish", TagCategory.Cuisine, 150),
        new("Tapas", TagCategory.Cuisine, 160),
        new("Greek", TagCategory.Cuisine, 170),
        new("German", TagCategory.Cuisine, 180),
        new("Eastern European", TagCategory.Cuisine, 190),
        new("British", TagCategory.Cuisine, 200),
        new("Irish", TagCategory.Cuisine, 210),
        new("Scandinavian", TagCategory.Cuisine, 220),
        // Asia
        new("East Asian", TagCategory.Cuisine, 230),
        new("Chinese", TagCategory.Cuisine, 240),
        new("Japanese", TagCategory.Cuisine, 250),
        new("Korean", TagCategory.Cuisine, 260),
        new("Southeast Asian", TagCategory.Cuisine, 270),
        new("Thai", TagCategory.Cuisine, 280),
        new("Vietnamese", TagCategory.Cuisine, 290),
        new("Indonesian", TagCategory.Cuisine, 300),
        new("Filipino", TagCategory.Cuisine, 310),
        new("South Asian", TagCategory.Cuisine, 320),
        new("Indian", TagCategory.Cuisine, 330),
        new("Pakistani", TagCategory.Cuisine, 340),
        // Middle Eastern & Mediterranean
        new("Mediterranean", TagCategory.Cuisine, 350),
        new("Middle Eastern", TagCategory.Cuisine, 360),
        new("Lebanese", TagCategory.Cuisine, 370),
        new("Turkish", TagCategory.Cuisine, 380),
        new("Persian", TagCategory.Cuisine, 390),
        new("North African", TagCategory.Cuisine, 400),
        new("Moroccan", TagCategory.Cuisine, 410),
        // ==========================================
        // 3. Dietary Restrictions & Lifestyles (Dietary = 2)
        // ==========================================
        // Allergen & Intolerance
        new("Gluten-Free", TagCategory.Dietary, 10),
        new("Dairy-Free", TagCategory.Dietary, 20),
        new("Nut-Free", TagCategory.Dietary, 30),
        new("Peanut-Free", TagCategory.Dietary, 40),
        new("Tree-Nut-Free", TagCategory.Dietary, 50),
        new("Egg-Free", TagCategory.Dietary, 60),
        new("Soy-Free", TagCategory.Dietary, 70),
        new("Shellfish-Free", TagCategory.Dietary, 80),
        new("Fish-Free", TagCategory.Dietary, 90),
        new("Sesame-Free", TagCategory.Dietary, 100),
        // Dietary Patterns
        new("Vegetarian", TagCategory.Dietary, 110),
        new("Vegan", TagCategory.Dietary, 120),
        new("Pescatarian", TagCategory.Dietary, 130),
        new("Keto", TagCategory.Dietary, 140),
        new("Low-Carb", TagCategory.Dietary, 150),
        new("Paleo", TagCategory.Dietary, 160),
        new("Whole30", TagCategory.Dietary, 170),
        new("Halal", TagCategory.Dietary, 180),
        new("Kosher", TagCategory.Dietary, 190),
        new("Alcohol", TagCategory.Dietary, 195),
        // Health & Nutritional Focus
        new("High-Protein", TagCategory.Dietary, 200),
        new("Low-Sodium", TagCategory.Dietary, 210),
        new("Low-Fat", TagCategory.Dietary, 220),
        new("Low-Sugar", TagCategory.Dietary, 230),
        new("Diabetic-Friendly", TagCategory.Dietary, 240),
        new("Gut-Friendly", TagCategory.Dietary, 250),
        new("Heart-Healthy", TagCategory.Dietary, 260),
        new("High-Fiber", TagCategory.Dietary, 270),
        // ==========================================
        // 4. Cooking Method & Equipment (Equipment = 3)
        // ==========================================
        // Stovetop & Pans
        new("Cast Iron", TagCategory.Equipment, 10),
        new("Carbon Steel", TagCategory.Equipment, 20),
        new("Stainless Steel", TagCategory.Equipment, 30),
        new("Wok", TagCategory.Equipment, 40),
        new("Dutch Oven", TagCategory.Equipment, 50),
        new("Sauté", TagCategory.Equipment, 60),
        new("Braise", TagCategory.Equipment, 70),
        new("Deep-Fry", TagCategory.Equipment, 80),
        new("Shallow-Fry", TagCategory.Equipment, 90),
        new("Pan-Sear", TagCategory.Equipment, 100),
        new("Stir-Fry", TagCategory.Equipment, 110),
        new("Boil & Simmer", TagCategory.Equipment, 120),
        new("Steamed", TagCategory.Equipment, 130),
        // Oven
        new("Baking", TagCategory.Equipment, 140),
        new("Roasting", TagCategory.Equipment, 150),
        new("Broiling", TagCategory.Equipment, 160),
        new("Sheet Pan", TagCategory.Equipment, 170),
        // Appliances
        new("Instant Pot", TagCategory.Equipment, 180),
        new("Pressure Cooker", TagCategory.Equipment, 190),
        new("Slow Cooker", TagCategory.Equipment, 200),
        new("Air Fryer", TagCategory.Equipment, 210),
        new("Blender", TagCategory.Equipment, 220),
        new("Food Processor", TagCategory.Equipment, 230),
        new("Stand Mixer", TagCategory.Equipment, 240),
        new("Sous Vide", TagCategory.Equipment, 250),
        new("Toaster Oven", TagCategory.Equipment, 260),
        new("Microwave", TagCategory.Equipment, 270),
        new("Waffle Maker", TagCategory.Equipment, 280),
        // Outdoor & Open Flame
        new("Charcoal Grill", TagCategory.Equipment, 290),
        new("Gas Grill", TagCategory.Equipment, 300),
        new("Grill", TagCategory.Equipment, 310),
        new("Smoker", TagCategory.Equipment, 320),
        new("Campfire", TagCategory.Equipment, 330),
        // Cleanup Profile
        new("One-Pot", TagCategory.Equipment, 340),
        new("One-Pan", TagCategory.Equipment, 350),
        new("No-Cook", TagCategory.Equipment, 360),
        new("Minimal Dishes", TagCategory.Equipment, 370),
        // ==========================================
        // 5. Primary Protein & Base (Protein = 5)
        // ==========================================
        // Poultry
        new("Chicken Breast", TagCategory.Protein, 10),
        new("Chicken Thighs", TagCategory.Protein, 20),
        new("Chicken", TagCategory.Protein, 30),
        new("Turkey", TagCategory.Protein, 40),
        new("Duck", TagCategory.Protein, 50),
        // Red Meat
        new("Beef", TagCategory.Protein, 60),
        new("Steak", TagCategory.Protein, 70),
        new("Ground Beef", TagCategory.Protein, 80),
        new("Pork", TagCategory.Protein, 90),
        new("Pork Chops", TagCategory.Protein, 100),
        new("Pork Tenderloin", TagCategory.Protein, 110),
        new("Pork Shoulder", TagCategory.Protein, 120),
        new("Lamb", TagCategory.Protein, 130),
        new("Sausage", TagCategory.Protein, 140),
        new("Bacon", TagCategory.Protein, 150),
        new("Ham", TagCategory.Protein, 160),
        // Seafood
        new("Salmon", TagCategory.Protein, 170),
        new("White Fish", TagCategory.Protein, 180),
        new("Shrimp", TagCategory.Protein, 190),
        new("Scallops", TagCategory.Protein, 200),
        new("Tuna", TagCategory.Protein, 210),
        new("Crab", TagCategory.Protein, 220),
        new("Lobster", TagCategory.Protein, 230),
        new("Mussels", TagCategory.Protein, 240),
        // Plant Protein
        new("Tofu", TagCategory.Protein, 250),
        new("Tempeh", TagCategory.Protein, 260),
        new("Lentils", TagCategory.Protein, 270),
        new("Beans", TagCategory.Protein, 280),
        new("Chickpeas", TagCategory.Protein, 290),
        new("Edamame", TagCategory.Protein, 300),
        // Carb Bases
        new("Pasta", TagCategory.Protein, 310),
        new("Noodles", TagCategory.Protein, 320),
        new("Rice", TagCategory.Protein, 330),
        new("Potatoes", TagCategory.Protein, 340),
        new("Sweet Potatoes", TagCategory.Protein, 350),
        new("Grains", TagCategory.Protein, 360),
        new("Quinoa", TagCategory.Protein, 370),
        new("Farro", TagCategory.Protein, 380),
        new("Couscous", TagCategory.Protein, 390),
        new("Pizza", TagCategory.Protein, 400),
        new("Polenta", TagCategory.Protein, 410),
        // ==========================================
        // 6. Occasion, Season & Setting (Occasion = 6)
        // ==========================================
        // Seasons
        new("Spring", TagCategory.Occasion, 10),
        new("Summer", TagCategory.Occasion, 20),
        new("Autumn", TagCategory.Occasion, 30),
        new("Winter", TagCategory.Occasion, 40),
        // Weather & Vibe
        new("Comfort Food", TagCategory.Occasion, 50),
        new("Light & Refreshing", TagCategory.Occasion, 60),
        new("Cozy & Warming", TagCategory.Occasion, 70),
        new("Rainy Day", TagCategory.Occasion, 80),
        // Social & Events
        new("Crowd-Pleaser", TagCategory.Occasion, 90),
        new("Potluck", TagCategory.Occasion, 100),
        new("Date Night", TagCategory.Occasion, 110),
        new("Dinner Party", TagCategory.Occasion, 120),
        new("Holiday", TagCategory.Occasion, 130),
        new("Thanksgiving", TagCategory.Occasion, 140),
        new("Christmas", TagCategory.Occasion, 150),
        new("Game Day", TagCategory.Occasion, 160),
        new("Finger Food", TagCategory.Occasion, 170),
        new("Weeknight", TagCategory.Occasion, 180),
        // Outdoors & Portable
        new("Camping", TagCategory.Occasion, 190),
        new("Picnic", TagCategory.Occasion, 200),
        new("Road Trip", TagCategory.Occasion, 210),
        new("Packed Lunch", TagCategory.Occasion, 220),
        // ==========================================
        // 7. Meal Prep & Preservation (MealPrep = 7)
        // ==========================================
        // Storage Profile
        new("Freezer-Friendly", TagCategory.MealPrep, 10),
        new("Meal Prep", TagCategory.MealPrep, 20),
        new("Batch Cooking", TagCategory.MealPrep, 30),
        new("Leftover-Friendly", TagCategory.MealPrep, 40),
        new("Reheats Well", TagCategory.MealPrep, 50),
        // Timing
        new("Make-Ahead", TagCategory.MealPrep, 60),
        new("Overnight Marinate", TagCategory.MealPrep, 70),
        new("Quick Prep", TagCategory.MealPrep, 80),
        new("Slow-Cooked", TagCategory.MealPrep, 90),
        new("30-Minute Meal", TagCategory.MealPrep, 100),
        // Preservation
        new("Canning & Pickling", TagCategory.MealPrep, 110),
        new("Dehydrating", TagCategory.MealPrep, 120),
        new("Fermented", TagCategory.MealPrep, 130),
    ];

    private static readonly Lazy<IReadOnlyList<string>> LazyTagNames = new(() =>
        All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    );

    public static IReadOnlyList<string> TagNames => LazyTagNames.Value;

    private static readonly Lazy<ILookup<TagCategory, string>> LazyByCategory = new(() =>
        All.ToLookup(t => t.Category, t => t.Name)
    );

    public static IEnumerable<string> GetTagsForCategory(TagCategory category) =>
        LazyByCategory.Value[category];

    /// <summary>
    /// Grocery section / category names that should NEVER leak into recipe tags.
    /// </summary>
    public static readonly HashSet<string> DisallowedGrocerySectionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Produce",
        "MeatAndSeafood",
        "Meat And Seafood",
        "DairyAndEggs",
        "Dairy And Eggs",
        "Refrigerated",
        "Bakery",
        "PantryDryGoods",
        "Pantry Dry Goods",
        "CannedAndJarred",
        "Canned And Jarred",
        "CondimentsAndSauces",
        "Condiments And Sauces",
        "SpicesAndSeasonings",
        "Spices And Seasonings",
        "Baking",
        "Snacks",
        "Beverages",
        "Frozen",
        "Other",
    };

    /// <summary>
    /// Basic pantry staples or generic seasoning ingredients that should not be converted to tags.
    /// </summary>
    public static readonly HashSet<string> DisallowedGenericStaples = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Salt",
        "Kosher Salt",
        "Table Salt",
        "Sea Salt",
        "Black Pepper",
        "Pepper",
        "Ground Black Pepper",
        "Olive Oil",
        "Extra-Virgin Olive Oil",
        "Vegetable Oil",
        "Canola Oil",
        "Cooking Oil",
        "Water",
        "Flour",
        "All-Purpose Flour",
        "Sugar",
        "Granulated Sugar",
        "Brown Sugar",
        "Garlic",
        "Garlic Clove",
        "Garlic Cloves",
        "Cloves Garlic",
        "Clove Garlic",
        "Butter",
        "Unsalted Butter",
        "Salted Butter",
        "Onion",
        "Yellow Onion",
        "White Onion",
        "Red Onion",
    };

    /// <summary>
    /// Known LLM-generated aliases, concatenated strings, or synonyms mapped to canonical seeded tags.
    /// </summary>
    public static readonly Dictionary<string, string> CanonicalAliases = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Concatenations / Grocery remappings
        ["Condimentsandsauces"] = "Sauce",
        ["CondimentsAndSauces"] = "Sauce",
        ["Condiments and sauces"] = "Sauce",
        ["Condiment and sauce"] = "Sauce",
        ["Condiments"] = "Condiment",
        ["Sauces"] = "Sauce",
        ["Dips"] = "Dip",
        ["Dressings"] = "Dressing",
        ["Spreads"] = "Spread",
        ["Marinades"] = "Marinade",

        // Course aliases
        ["Main"] = "Main Course",
        ["Mains"] = "Main Course",
        ["Entree"] = "Main Course",
        ["Entrée"] = "Main Course",
        ["Main Dish"] = "Main Course",
        ["Side"] = "Side Dish",
        ["Sides"] = "Side Dish",
        ["Appetizers"] = "Appetizer",
        ["Starters"] = "Starter",
        ["Desserts"] = "Dessert",
        ["Soups"] = "Soup",
        ["Stews"] = "Stew",
        ["Salads"] = "Salad",
        ["Breads"] = "Bread",
        ["Drinks"] = "Drink",
        ["Cocktails"] = "Cocktail",
        ["Mocktails"] = "Mocktail",
        ["Sandwiches"] = "Sandwich",

        // Equipment aliases
        ["Onepot"] = "One-Pot",
        ["One Pot"] = "One-Pot",
        ["One-pan"] = "One-Pan",
        ["Onepan"] = "One-Pan",
        ["One Pan"] = "One-Pan",
        ["Sheetpan"] = "Sheet Pan",
        ["Sheet-Pan"] = "Sheet Pan",
        ["Castiron"] = "Cast Iron",
        ["Cast-Iron"] = "Cast Iron",
        ["Dutchoven"] = "Dutch Oven",
        ["Dutch-Oven"] = "Dutch Oven",
        ["Slowcooker"] = "Slow Cooker",
        ["Slow-Cooker"] = "Slow Cooker",
        ["Crockpot"] = "Slow Cooker",
        ["Crock Pot"] = "Slow Cooker",
        ["Instantpot"] = "Instant Pot",
        ["Instant-Pot"] = "Instant Pot",
        ["Pressurecooker"] = "Pressure Cooker",
        ["Pressure Cooker"] = "Pressure Cooker",
        ["Airfryer"] = "Air Fryer",
        ["Air Fryer"] = "Air Fryer",
        ["Air-Fryer"] = "Air Fryer",
        ["Sousvide"] = "Sous Vide",
        ["Sous-Vide"] = "Sous Vide",
        ["Foodprocessor"] = "Food Processor",
        ["Food-Processor"] = "Food Processor",
        ["Standmixer"] = "Stand Mixer",
        ["Stand-Mixer"] = "Stand Mixer",
        ["Toasteroven"] = "Toaster Oven",
        ["Toaster Oven"] = "Toaster Oven",
        ["Grilling"] = "Grill",
        ["Grilled"] = "Grill",
        ["Smoking"] = "Smoker",
        ["Smoked"] = "Smoker",
        ["Bake"] = "Baking",
        ["Baked"] = "Baking",
        ["Roast"] = "Roasting",
        ["Roasted"] = "Roasting",
        ["Broil"] = "Broiling",
        ["Broiled"] = "Broiling",
        ["Sear"] = "Pan-Sear",
        ["Pan-Seared"] = "Pan-Sear",
        ["Pan Sear"] = "Pan-Sear",
        ["Deep Fry"] = "Deep-Fry",
        ["Deep Fried"] = "Deep-Fry",
        ["Shallow Fry"] = "Shallow-Fry",
        ["Stir Fry"] = "Stir-Fry",
        ["Stir Fried"] = "Stir-Fry",
        ["Braised"] = "Braise",
        ["Simmer"] = "Boil & Simmer",
        ["Boil"] = "Boil & Simmer",
        ["Steam"] = "Steamed",

        // Dietary aliases
        ["Glutenfree"] = "Gluten-Free",
        ["Gluten Free"] = "Gluten-Free",
        ["Dairyfree"] = "Dairy-Free",
        ["Dairy Free"] = "Dairy-Free",
        ["Nutfree"] = "Nut-Free",
        ["Nut Free"] = "Nut-Free",
        ["Peanut Free"] = "Peanut-Free",
        ["Eggfree"] = "Egg-Free",
        ["Egg Free"] = "Egg-Free",
        ["Soyfree"] = "Soy-Free",
        ["Soy Free"] = "Soy-Free",
        ["Shellfish Free"] = "Shellfish-Free",
        ["Lowcarb"] = "Low-Carb",
        ["Low Carb"] = "Low-Carb",
        ["Highprotein"] = "High-Protein",
        ["High Protein"] = "High-Protein",
        ["Lowsodium"] = "Low-Sodium",
        ["Low Sodium"] = "Low-Sodium",
        ["Lowfat"] = "Low-Fat",
        ["Low Fat"] = "Low-Fat",
        ["Lowsugar"] = "Low-Sugar",
        ["Low Sugar"] = "Low-Sugar",
        ["Sugar-Free"] = "Low-Sugar",
        ["Sugar Free"] = "Low-Sugar",
        ["Diabetic Friendly"] = "Diabetic-Friendly",
        ["Gut Friendly"] = "Gut-Friendly",
        ["Heart Healthy"] = "Heart-Healthy",
        ["High Fiber"] = "High-Fiber",
        ["Alocohol"] = "Alcohol",
        ["Alcoholic"] = "Alcohol",
        ["Contains Alcohol"] = "Alcohol",
        ["With Alcohol"] = "Alcohol",
        ["Booze"] = "Alcohol",
        ["Liquor"] = "Alcohol",

        // Protein / Base aliases
        ["Chicken Breasts"] = "Chicken Breast",
        ["Chicken Thigh"] = "Chicken Thighs",
        ["Whole Chicken"] = "Chicken",
        ["Pork Chop"] = "Pork Chops",
        ["Ground Pork"] = "Pork",
        ["Pulled Pork"] = "Pork Shoulder",
        ["Scallop"] = "Scallops",
        ["Prawns"] = "Shrimp",
        ["Prawn"] = "Shrimp",
        ["Noodle"] = "Noodles",
        ["Potato"] = "Potatoes",
        ["Sweet Potato"] = "Sweet Potatoes",
        ["Grain"] = "Grains",
        ["Bean"] = "Beans",
        ["Chickpea"] = "Chickpeas",
        ["Lentil"] = "Lentils",

        // Occasion aliases
        ["Comfortfood"] = "Comfort Food",
        ["Comfort Food"] = "Comfort Food",
        ["Datenight"] = "Date Night",
        ["Date Night"] = "Date Night",
        ["Dinnerparty"] = "Dinner Party",
        ["Dinner Party"] = "Dinner Party",
        ["Gameday"] = "Game Day",
        ["Game Day"] = "Game Day",
        ["Fingerfood"] = "Finger Food",
        ["Finger Food"] = "Finger Food",
        ["Packedlunch"] = "Packed Lunch",
        ["Packed Lunch"] = "Packed Lunch",
        ["Roadtrip"] = "Road Trip",
        ["Road Trip"] = "Road Trip",
        ["Crowd Pleaser"] = "Crowd-Pleaser",
        ["Feeding A Crowd"] = "Crowd-Pleaser",
        ["Fall"] = "Autumn",

        // Meal Prep aliases
        ["Freezerfriendly"] = "Freezer-Friendly",
        ["Freezer Friendly"] = "Freezer-Friendly",
        ["Mealprep"] = "Meal Prep",
        ["Meal Prep"] = "Meal Prep",
        ["Batch Cook"] = "Batch Cooking",
        ["Batch Cooking"] = "Batch Cooking",
        ["Batchcook"] = "Batch Cooking",
        ["Leftoverfriendly"] = "Leftover-Friendly",
        ["Leftover Friendly"] = "Leftover-Friendly",
        ["Reheats"] = "Reheats Well",
        ["Makeahead"] = "Make-Ahead",
        ["Make Ahead"] = "Make-Ahead",
        ["30 Minute Meal"] = "30-Minute Meal",
        ["30 Minutes"] = "30-Minute Meal",
        ["Quick And Easy"] = "Quick Prep",
        ["Quick Prep"] = "Quick Prep",
        ["Slow Cook"] = "Slow-Cooked",
        ["Slow Cooked"] = "Slow-Cooked",
        ["Pickling"] = "Canning & Pickling",
        ["Canning"] = "Canning & Pickling",
    };
}
