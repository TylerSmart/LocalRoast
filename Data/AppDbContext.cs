using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<StoreAisle> StoreAisles => Set<StoreAisle>();

    public DbSet<FoodItem> FoodItems => Set<FoodItem>();

    public DbSet<FoodItemAlias> FoodItemAliases => Set<FoodItemAlias>();

    public DbSet<Recipe> Recipes => Set<Recipe>();

    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();

    public DbSet<RecipeStep> RecipeSteps => Set<RecipeStep>();

    public DbSet<RecipeStepIngredient> RecipeStepIngredients => Set<RecipeStepIngredient>();

    public DbSet<RecipeStepTimer> RecipeStepTimers => Set<RecipeStepTimer>();

    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<RecipeCollection> RecipeCollections => Set<RecipeCollection>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<RecipeTag> RecipeTags => Set<RecipeTag>();

    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();

    public DbSet<HouseholdLlmSettings> HouseholdLlmSettings => Set<HouseholdLlmSettings>();

    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
