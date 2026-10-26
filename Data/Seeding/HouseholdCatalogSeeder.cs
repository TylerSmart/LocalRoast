using System.Text.RegularExpressions;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Seeding;

public static class HouseholdCatalogSeeder
{
    public static async Task SeedAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default
    )
    {
        var hasAisles = await db.StoreAisles.AnyAsync(cancellationToken);
        if (!hasAisles)
        {
            var defaultAisles = new (string Name, int SortOrder)[]
            {
                ("Produce", 10),
                ("Bakery", 20),
                ("Deli", 30),
                ("Meat & Seafood", 40),
                ("Dairy & Eggs", 50),
                ("Canned & Jarred", 60),
                ("Dry Goods & Pasta", 70),
                ("International", 80),
                ("Spices & Baking", 90),
                ("Frozen", 100),
                ("Beverages", 110),
                ("Household", 120),
                ("Other", 130),
            };

            foreach (var (name, sortOrder) in defaultAisles)
            {
                db.StoreAisles.Add(
                    new StoreAisle
                    {
                        Name = name,
                        NormalizedName = name.Trim().ToLowerInvariant(),
                        SortOrder = sortOrder,
                        IsSystemSeeded = true,
                    }
                );
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var hasFoodItems = await db.FoodItems.AnyAsync(cancellationToken);
        if (!hasFoodItems)
        {
            var aisles = await db.StoreAisles.ToDictionaryAsync(
                a => a.NormalizedName,
                a => a.Id,
                cancellationToken
            );

            Guid? GetAisleId(string aisleName) =>
                aisles.TryGetValue(aisleName.Trim().ToLowerInvariant(), out var id) ? id : null;

            var starterFoodItems = new (
                string Name,
                string AisleName,
                bool IsPantryStaple,
                string[] Aliases
            )[]
            {
                ("Water", "Beverages", true, []),
                ("Kosher Salt", "Spices & Baking", true, ["Salt", "Fine Sea Salt"]),
                (
                    "Black Pepper",
                    "Spices & Baking",
                    true,
                    ["Freshly Ground Black Pepper", "Peppercorns"]
                ),
                ("Extra-Virgin Olive Oil", "Spices & Baking", true, ["Olive Oil", "EVOO"]),
                ("Neutral Cooking Oil", "Spices & Baking", true, ["Vegetable Oil", "Canola Oil"]),
                ("All-Purpose Flour", "Spices & Baking", true, ["AP Flour", "Plain Flour"]),
                ("Granulated Sugar", "Spices & Baking", true, ["White Sugar"]),
                ("Baking Powder", "Spices & Baking", true, []),
                ("Baking Soda", "Spices & Baking", true, []),
                ("Unsalted Butter", "Dairy & Eggs", true, ["Butter"]),
                ("Garlic", "Produce", false, ["Garlic Cloves"]),
                ("Yellow Onion", "Produce", false, ["Onion", "Sweet Onion"]),
                ("Green Onion", "Produce", false, ["Scallion", "Spring Onion"]),
                ("Cilantro", "Produce", false, ["Fresh Coriander"]),
                ("Lemon", "Produce", false, []),
                ("Lime", "Produce", false, []),
                ("Large Eggs", "Dairy & Eggs", false, ["Egg", "Eggs"]),
                ("Chicken Thighs", "Meat & Seafood", false, ["Boneless Skinless Chicken Thighs"]),
            };

            foreach (var (name, aisleName, isPantryStaple, aliases) in starterFoodItems)
            {
                var foodItem = new FoodItem
                {
                    Name = name,
                    NormalizedName = name.Trim().ToLowerInvariant(),
                    DefaultStoreAisleId = GetAisleId(aisleName),
                    IsPantryStaple = isPantryStaple,
                };

                foreach (var alias in aliases)
                {
                    var cleanAlias = alias.Trim();
                    if (!string.IsNullOrWhiteSpace(cleanAlias))
                    {
                        foodItem.Aliases.Add(
                            new FoodItemAlias
                            {
                                Alias = cleanAlias,
                                NormalizedAlias = cleanAlias.ToLowerInvariant(),
                            }
                        );
                    }
                }

                db.FoodItems.Add(foodItem);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var hasCollections = await db.Collections.AnyAsync(cancellationToken);
        if (!hasCollections)
        {
            var starterCollections = new (string Name, string Description, int SortOrder)[]
            {
                ("Weeknight Staples", "Reliable 30–45 minute dinners for busy evenings.", 10),
                (
                    "Weekend Projects",
                    "Slow braises, homemade pasta, sourdough, and showstoppers.",
                    20
                ),
                (
                    "Holiday & Hosting",
                    "Crowd-pleasers and make-ahead dishes for family gatherings.",
                    30
                ),
                (
                    "Sauces & Components",
                    "Reusable dressings, marinades, pickles, and spice blends.",
                    40
                ),
            };

            foreach (var (name, description, sortOrder) in starterCollections)
            {
                db.Collections.Add(
                    new Collection
                    {
                        Name = name,
                        NormalizedName = name.Trim().ToLowerInvariant(),
                        Slug = ToKebabCase(name),
                        Description = description,
                        SortOrder = sortOrder,
                        IsSystemSeeded = true,
                    }
                );
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var hasTags = await db.Tags.AnyAsync(cancellationToken);
        if (!hasTags)
        {
            var starterTags = new (string Name, TagCategory Category, int SortOrder)[]
            {
                // Course
                ("Main", TagCategory.Course, 10),
                ("Side", TagCategory.Course, 20),
                ("Breakfast", TagCategory.Course, 30),
                ("Lunch", TagCategory.Course, 40),
                ("Dessert", TagCategory.Course, 50),
                ("Appetizer", TagCategory.Course, 60),
                ("Sauce / Component", TagCategory.Course, 70),
                // Cuisine
                ("American", TagCategory.Cuisine, 10),
                ("Italian", TagCategory.Cuisine, 20),
                ("Mexican", TagCategory.Cuisine, 30),
                ("Thai", TagCategory.Cuisine, 40),
                ("Japanese", TagCategory.Cuisine, 50),
                ("Indian", TagCategory.Cuisine, 60),
                ("Mediterranean", TagCategory.Cuisine, 70),
                // Dietary
                ("Vegetarian", TagCategory.Dietary, 10),
                ("Vegan", TagCategory.Dietary, 20),
                ("Gluten-Free", TagCategory.Dietary, 30),
                ("Dairy-Free", TagCategory.Dietary, 40),
                // Equipment
                ("Sheet Pan", TagCategory.Equipment, 10),
                ("One-Pot", TagCategory.Equipment, 20),
                ("Instant Pot", TagCategory.Equipment, 30),
                ("Air Fryer", TagCategory.Equipment, 40),
                ("Dutch Oven", TagCategory.Equipment, 50),
                ("Grill", TagCategory.Equipment, 60),
                ("Slow Cooker", TagCategory.Equipment, 70),
            };

            foreach (var (name, category, sortOrder) in starterTags)
            {
                var catSlug = category.ToString().ToLowerInvariant();
                var nameSlug = ToKebabCase(name);
                db.Tags.Add(
                    new Tag
                    {
                        Name = name,
                        NormalizedName = name.Trim().ToLowerInvariant(),
                        Slug = $"{catSlug}-{nameSlug}",
                        Category = category,
                        SortOrder = sortOrder,
                        IsSystemSeeded = true,
                    }
                );
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var hasLlmSettings = await db.HouseholdLlmSettings.AnyAsync(cancellationToken);
        if (!hasLlmSettings)
        {
            db.HouseholdLlmSettings.Add(
                new HouseholdLlmSettings
                {
                    SingletonKey = HouseholdLlmSettings.DefaultSingletonKey,
                    Provider = LlmProvider.Ollama,
                    BaseUrl = "http://localhost:11434",
                    ApiKey = null,
                    TextModel = null,
                    VisionModel = null,
                    OllamaKeepAlive = "5m",
                    RequestTimeoutSeconds = 120,
                    AutoPublishHighConfidenceText = false,
                }
            );

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lower = value.Trim().ToLowerInvariant();
        var clean = Regex.Replace(lower, @"[^a-z0-9\s-]", "");
        var hyphens = Regex.Replace(clean, @"[\s-]+", "-");
        return hyphens.Trim('-');
    }
}
