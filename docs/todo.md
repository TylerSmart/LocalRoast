```json
{
  "mcpServers": {
    "localroast": {
      "url": "http://localhost:4200/api/mcp",
      "headers": {
        "Authorization": "Bearer lr_pat_MzbPOjF4TCsm3N0_mtlqwZGcX3hZ7JycSh3JVyRCYYg"
      }
    }
  }
}
```

# Ingestion

- [ ] Alright, this seems to be working better than our last system. I've been testing it locally. I've got some issues I am seeing and improvements I would like to make:
  - [ ] In general, it seems like a 50/50 chance the recipe goes straight to published or needs review, which is not as good as I would like. We need to figure out how to improve this metric.
    - [ ] I assume most of these recipes are getting flagged for review due to review issues. These review issues might be something we send back to the AI to fix and we can then also ask if the issue was either resolved, needs human intervention, or is not really an issue and can be ignored. I believe this automatic iterative feature got removed for some reason, and I think there is value to it. (The "Antipasto Pasta Salad" recipe's "salt: step amounts (3 Tsp) exceed the ingredient list (0.5 Tsp)." validation issue is a good example of this because the recipe does literally specify "1/2 teaspoon table salt, plus salt for cooking pasta" in the ingredient list, but then later has the following "Stir 1 tablespoon salt and pasta into boiling water and cook" in a step. This is obviously just a poorly written recipe and the AI could probably resolve this on its own by adjusting the ingredient list to resolve the validation issue. Another case of this in the same recipe is the "Step 5: Ingredient 11 "mushroom" is mentioned but not linked; verify whether this step uses it." quality warning which is caused by "Add meat, provolone, peppers, basil, and pasta to mushrooms and toss to combine." in step 5. This seems like another case where the AI could probably just disregard that quality warning because the rule was too strict.)
    - [ ] I am constantly seeing issues like "garlic: step amounts use units that cannot be compared with the ingredient list." and I honestly cannot determine where the issue is in the recipe. I don't know if this is a UI/UX problem with not knowing where the issue is or if this is just a false positive that we need to reassess.
    - [ ] I've seen this "Pasta alla Trapanese (Pasta with Tomato and Almond Pesto)" issue on the "Pasta alla Trapanese (Pasta with Tomato and Almond Pesto)" recipe as well as others. It seems common enough we may want to try and resolve it on the first round, unless we think addressing it with a repair round would be fine.
  - [ ] I am seeing plenty of cases where a step will reference ingredients in the step, but they are not actually included. (i.e. "Ultracreamy Hummus" has chickpeas, garlic, lemon juice, and salt referenced in step 2, yet the instruction card only says lemon juice is used, and somehow the ingredients list says it is divided and used in step 2. This is just a bit inconsistent and defeats the purpose of those features.)
  - [ ] I know that we are highlighting the ingredient amount in steps now (which is working perfectly), but I was hoping to actually highlight the ingredient as well associated with that amount. This is a case where I could see us editing a lot of text in the steps so the measurement amount is explicitly stated. (i.e. the first step for the "Ultracreamy Hummus" would go from "Combine chickpeas, baking soda, and 6 cups water in medium saucepan and..." to "Combine 2 cans chickpeas, 1/2 tsp baking soda, and 6 cups water in medium saucepan and...", with each ingredient and amount highlighted separately. That being said, the second step starts "While chickpeas cook...", in this case we wouldn't care to highlight it or add the ingredients since we aren't actually interacting with or measuring it.)
  - [ ] For the review issues, I think it might be handy to actually put the issue in a place nearby where the issue is, rather than just placing it at the top. I find it annoying to have to constantly try and find where the issue is and scroll between it and the top where the issues are listed.
- [ ] There is not a way for me to immediately run the "Review issues" AI actions ("Recheck issues", "Re0extract with AI", and "Refine with ...") without the LLM ingestion queue being turned on. This should not be the case, those should be queued run despite the ingestion queue being paused.
- [ ] I am not sure this is worth resolving, but it seem like all of the ingested recipes in review are either a 59% or 70% conf rating.

# Various Fixes and Improvements

- [ ] The "Retry All" button should apply to all in the category and filter, not just those on the page. I am on the "All" section with no filter and it shows 1562 results, but the "Retry All" button only applies to the 25 currently visible on the page.
- [ ] A nice feature would be to simplify the ingredient measurements (especially when multiplying recipes). For example, say a recipe calls for 1 tablespoon of oil. If I quadruple the recipe, it should say to  use a 1/4 cup of oil intead of 4 tablespoons, since that simplifies the measuring. The same principle could apply when halving a recipe and it calls for 1/4 cup of something, it would go to two tablespoons (instead of a 1/8 cup since nobody really has anything below 1/4 for measuring cups).
- [ ] I have found a small bug on my phone when I looking at the cookbook collections, the `.collection-pill-group` is not wide enough to fit everything, so the count and action buttons overflow. I am pretty sure this is just because the collection name is really long (the name of the collection is "The Complete America's Test Kitchen TV Show Cookbook") because not all collections do this (I made another collection called "Short Name" and it fits everything just fine). In fact, it looks like the width maxes out around the same width as the viewport (minus the margin/padding).
- [ ] The action buttons on the collection filters in the cookbook are not circles when they are hovered over, they are tall ovals.
- [ ] The LLM Settings modal is a bit complex now. Perhaps we could divide it up into tabs or something?
- [ ] Can we allow the user to manage model profiles (add, remove, edit)?
- [ ] I think it would be nice to be able to still open the ingestion review page for recipes that have been published, since it allows us to do things like refine the recipe after the fact.
- [ ] There are some elements (i.e. the `[aria-label="User profile and account options"]` button) that is too wide due to the icon margin still appearing after the text has gone away due to the viewport being too narrow. Can we resolve this with that button and look for other places this might be happening?
- [ ] The styling for sub-recipe ingredients is severely messed up (check the "Blueberry Pie" as an example at `http://localhost:4200/recipes/d70c27f6-dfd2-4452-84c2-277823121962`). Can we fix this?
