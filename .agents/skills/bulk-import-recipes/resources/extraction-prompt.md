# Unified Single-Pass Recipe Extraction & Step Tokenization Guide

You are transcribing raw recipe text into a single structured JSON object for a culinary application that dynamically **multiplies recipe quantities**, **converts measuring systems (US <-> Metric)**, **tracks canonical grocery SKUs**, and **launches clickable timers**.

> **IMPORTANT EXECUTION RULE:** Do NOT call `read_file`, `run_command`, `search_web`, or any workspace tools. Every recipe's full source text and every rule you need is provided inline in your prompt. Parse the text directly and return the final structured JSON via `finish` immediately.

---

## 1. General Metadata Rules

- **`title`**: Copy the recipe title faithfully from the source in clean Title Case (or exact original capitalization if already normal; never ALL-CAPS shouting).
- **`description`**:
  - **Clean Book / Source Headnotes (Preserve Verbatim)**: When the source prints `Description & Kitchen Notes:` or genuine headnotes, `"Why This Recipe Works"` explanations, technique/ingredient notes, serving suggestions, optional variations, or make-ahead/storage notes, copy the full text faithfully verbatim while stripping standalone editorial banners like `"WHY THIS RECIPE WORKS"`, `"WHY THIS RECIPES WORKS"`, or `"BEFORE YOU BEGIN"` (condense only if `> 3,800` characters).
  - **Missing Descriptions, Bare `"Variation of ..."` Stubs, or Web Blog/SEO Filler (Generate Grounded Summary)**: When the source has no headnote/description, only prints a bare `"Variation of ..."` cross-reference, or only contains clickbait/SEO/blog storytelling unrelated to cooking the dish, write a crisp, grounded **1–2 sentence culinary description** capturing the dish's character, flavor/texture, main cooking technique, or key ingredients. Always emit a non-empty `description`, never list raw ingredient quantities, and never invent unstated claims.
- **`servings`**: Numeric base serving count as a number (for ranges like `"Serves 4 to 6"`, use the lower number `4`), or `null` if not stated.
- **`yield`**: Concise printed yield label when given (e.g., `"Serves 4"`, `"Makes 2 cups"`, `"Makes 12 muffins"`), or `null` if not stated.
- **`timing`**: Integer minutes `{ "prep": int|null, "cook": int|null, "total": int|null }`. Convert hours/days to integer minutes (`1 hr 30 min` -> `90`). Use `0` for `cook` only when the recipe is clearly no-cook. Set `prep`/`cook`/`total` **ONLY from explicit timing lines** in the source — **use `null` whenever no explicit timing line is stated (NEVER guess or sum step durations)**.
- **`tags`**: Select 2–6 tags **strictly from the Curated Tags list in Section 5** below. NEVER invent custom tags or copy group headers. Do NOT tag sauces/dips/condiments/toppings as `"Dinner"` or `"Main Course"`. Only use `"Vegetarian"`/`"Vegan"` if every ingredient qualifies, and `"30-Minute Meal"`/`"Quick Prep"` only when explicit `total <= 30`.

---

## 2. Sections (`sections`)

Each object in `sections` has:
- `"name"`: Section heading in clean Title Case (e.g., `"For the Sauce"`, `"For the Dough"`, `"For the Topping"`), or `null` if the recipe is unsectioned. Never ALL-CAPS like `"PICADA"`. NEVER invent generic names like `"Ingredients"`, `"Instructions"`, `"Directions"`, `"Method"`, or `"Main"`.
- `"ingredients"`: Array of ingredients in this section, in exact printed order.
- `"steps"`: Array of `{ "instruction": string, "tokens": [...] }` for this section, in exact chronological order.
- When a section has a heading (`name` is non-null), **strip redundant inline section prefixes** like `"FOR THE BEEF: "` or `"FOR THE CABBAGE-CARROT SLAW: "` from BOTH `instruction` and the leading `"text"` token! And when a recipe uses named sections for its components, keep trailing finishing/assembly steps in a clean Title Case section (such as keeping them under the main dish section or `"Assembly and Serving"`) rather than mixing named and `null` sections across steps. If ingredient sections do not match step blocks 1-to-1, preserve every ingredient and step cleanly without dropping any — **NEVER leave `"steps": []` across all sections when the source contains instructions!**
- When extracting a **Variation** recipe (`Variation of ...` / `Base Ingredients` + `Variation Instructions`):
  1. Include ALL active base + variation ingredients, deduplicated cleanly (never duplicate shared spices).
  2. Act on `"Omit ..."` / `"Do not add ..."` / `"Substitute Y for X"` directives — exclude omitted items from BOTH `ingredients` and `steps`, and NEVER copy meta-editing sentences like `"Omit the parsley."` into `instruction` or `tokens`!
  3. Replace superseded master branches cleanly from the branch point onward and exclude base-only ingredients belonging only to replaced steps.

---

## 3. Ingredients (`sections[].ingredients`)

### Strict Source-List Boundary
- When the source prints an `Ingredients:` block, extract **ONLY items from that ingredient list**. NEVER pull incidental cooking/boiling/steaming/ice-bath water (`"1 inch water"`, `"6 cups water for boiling"`, `"2 cups ice cubes"`) or pan spray from `Instructions:` into `ingredients`.

### Fields per Ingredient Object
- **`name`**: Ingredient name without leading quantity/unit or leading/trailing prep verbs (e.g., `"2 tablespoons finely chopped onion"` -> `name: "onion"`, `prepNote: "finely chopped"`; `"1 teaspoon grated lemon zest"` -> `name: "lemon zest"`, `prepNote: "grated"`). Preserve meaningful grocery qualifiers (`"unsalted butter"`, `"kosher salt"`, `"extra-virgin olive oil"`, `"heavy cream"`, `"lime zest"`, `"lime juice"`, `"frozen spinach"`). When an ingredient offers an `"A or B"` or `"A and/or B"` choice (`"1 cup pecans or walnuts"`, `"chicken breasts and/or thighs"`), put **ONLY `A`** in `name` and put **`B`** in `replacements`!
- **`quantity`**: Decimal (`1/2` -> `0.5`, `1/4` -> `0.25`, `1/8` -> `0.125`, `1/3` -> `0.3333`, `2/3` -> `0.6667`). Set `null` ONLY when no number is printed (`"Table salt"`, `"Freshly ground black pepper"`, `"Table salt and pepper"`, `"Cooking spray"` -> `quantity: null, unit: null`, NEVER `quantity: 1`). Set `1` for `"Pinch ..."` / `"A dash of ..."`.
- **`maxQuantity`**: Decimal ONLY when the primary measurement is an explicit numeric range in the **same unit** (`"1 1/2 to 2 tablespoons"` -> `quantity: 1.5, maxQuantity: 2, unit: "tablespoon"`, with `maxQuantity >= quantity`), otherwise `null`. NEVER put parenthetical weight conversions (`"1 1/4 cups (5 ounces)"`), stick equivalents, pack weights (`"1 (14-ounce) can"`), item counts (`"(3 to 4 medium)"`), or `"A plus B"` second terms into `maxQuantity` — those belong in `alternateMeasurements` or added together!
- **`unit`**: Standardized lowercase singular string from Section 4, lowercase singular custom unit (`can`, `jar`, `clove`, `stick`, `bunch`, `head`, `stalk`, `sprig`, `slice`, `sheet`, `piece`, `recipe`, `batch`, `serving`, etc.), or `null` when count-only (`"3 ripe avocados"`, `"2 large eggs"`). NEVER use the ingredient's own noun as `unit` (`"4 large eggs"` -> `unit: null`, NEVER `"egg"`), and NEVER use `unit: "recipe"` for raw meat roasts (`"1 (2-pound) pork butt roast"` -> `quantity: 1, unit: null, alternateMeasurements: [{"quantity": 2, "unit": "pound"}]`).
- **`prepNote`**: Prep actions, size/state modifiers, divided/optional/serving notes, OR physical dimensions (`"8-inch square"`, `"divided"`, `"toasted and finely chopped"`, `"at room temperature"`), or `null`. **NEVER wrap `prepNote` in outer parentheses** (`"8-inch square"`, NOT `"(8-inch square)"`).
- **`isOptional`**: `true` if explicitly optional (`"(optional)"`), else `false`.
- **`canonicalFoodName`**: Singular lowercase normalized grocery item name for our food catalog (e.g., `"onion"`, `"unsalted butter"`, `"kosher salt"`, `"heavy cream"`, `"extra-virgin olive oil"`, `"black pepper"`, `"garlic"`, `"egg"`). Always preserve grocery-distinctive qualifiers (`"unsalted butter"`, `"kosher salt"`, `"heavy cream"`, `"sour cream"`, `"dark brown sugar"`, `"all-purpose flour"`, `"chicken broth"`). Set to `null` for `isRecipeReference: true` items.
- **`category`**: Choose ONE exact `FoodCategory` enum string:
  `Produce`, `MeatAndSeafood`, `DairyAndEggs`, `Refrigerated`, `Bakery`, `PantryDryGoods`, `CannedAndJarred`, `CondimentsAndSauces`, `SpicesAndSeasonings`, `Baking`, `Snacks`, `Beverages`, `Frozen`, `Other`.
- **`isPantryStaple`**: Always set `false` (pantry staples are curated manually in the Food Catalog).
- **`isRecipeReference`**: `true` when the ingredient is another prepared recipe in the cookbook (`"1 recipe Easy-Peel Hard-Cooked Eggs"`, `"4 servings Carne Deshebrada (recipe follows)"`, `"Garlic Mayonnaise (page 142)"` -> when NOT a raw grocery cross-reference like `"see page 12"`). Set `false` for ordinary groceries.
- **`referencedRecipeTitle`**: Clean title of the referenced sub-recipe when `isRecipeReference` is `true` (e.g., `"Easy-Peel Hard-Cooked Eggs"`, `"Carne Deshebrada"`, without trailing `"(recipe follows)"` or `"1 recipe"`), else `null`. Keep sub-recipe quantity and unit faithfully (`quantity: 1, unit: "recipe"`, or `quantity: 4, unit: "serving"`).
- **`replacements`**: Array of `{ "name", "quantity", "maxQuantity", "unit", "prepNote", "canonicalFoodName", "category", "isPantryStaple" }` ONLY when THAT SPECIFIC line has `"or"` / `"and/or"` / `"in place of"`. Use `quantity: null, unit: null` when reusing the primary ingredient's measurement.
- **`alternateMeasurements`**: Array of `{ "quantity", "unit" }` ONLY for equivalent printed weights/volumes/counts on that line (`"1 1/4 cups (6 1/4 ounces)"` -> `alternateMeasurements: [{"quantity": 6.25, "unit": "ounce"}]`; `"2 (15-ounce) cans chickpeas"` -> `alternateMeasurements: [{"quantity": 30, "unit": "ounce"}]`, multiplying count * per-container size; `"1/4 cup lemon juice (from 2 lemons)"` -> `[{"quantity": 2, "unit": null}]`). NEVER put dimensions (`1-inch`) or temperatures here.

### Splitting & Combining `"plus"` / Salt-and-Pepper Lines
- Split `"plus"` ONLY when it joins two **distinct ingredient forms with separate measurements** (e.g., `"1/4 tsp lime zest plus 2 tbsp juice"` -> `lime zest` + `lime juice`, `"2 eggs plus 1 white"` -> `eggs` + `egg white` — always include `"egg"` or the fruit noun on both rows!).
- Combine compatible measurements of the **same ingredient** onto ONE row (`"6 tbsp plus 1 tsp olive oil"` -> `quantity: 6.3333, unit: "tablespoon"`; `"1/4 cup plus 1 tsp oil"` -> `quantity: 0.2708, unit: "cup"`).
- NEVER split ONE line whose `prepNote` handles parts (`"2 scallions, white parts minced, green parts sliced thin"` stays ONE row).
- Split `"Table salt and pepper"` when steps measure them separately (`2 teaspoons salt, and 1 teaspoon pepper`); otherwise either keep unsplit or split cleanly with `quantity: null, unit: null`.

---

## 4. Standardized Units List

Whenever using a unit below (including abbreviations or plurals), write `unit` as the **exact lowercase singular string**:
- Tiny volume: `drop`, `smidgen`, `pinch`, `dash`
- US volume: `teaspoon`, `tablespoon`, `fluid ounce`, `cup`, `pint`, `quart`, `gallon`
- Metric volume: `milliliter`, `centiliter`, `deciliter`, `liter`
- Weight: `ounce`, `pound`, `milligram`, `gram`, `kilogram`
- Dimension (tokens only): `inch`, `foot`, `yard`, `millimeter`, `centimeter`, `meter`
- Timer (tokens only): `second`, `minute`, `hour`, `day`
- Temperature (tokens only): `fahrenheit`, `celsius`

---

## 5. Paired Step Tokenization (`sections[].steps`)

Each element in `sections[].steps` has:
- **`instruction`**: Complete verbatim plain-text step (1-to-1 with source numbered steps, leading `"1."` stripped, all sentences of that step kept together, parenthetical storage/make-ahead sentences kept intact unless leading storage step 1).
- **`tokens`**: Array of uniform token objects that concatenates seamlessly into `instruction` while turning scalable measurements and clickable timers into structured tokens.

### Indexing Rule for `ingredientIndex`
- Compute **one flat 0-based ingredient index** sequentially across ALL `sections[].ingredients` in order (Section 0 Ingredients `[0..k-1]`, Section 1 Ingredients `[k..m-1]`, etc.).
- Link step ingredient tokens (`"type": "ingredient"`) to that flat 0-based `ingredientIndex`. Use `null` ONLY for incidental step-only ingredients (boiling water, steaming water, ice bath) or pan oil not in the ingredient list.

### Uniform Token Object Shapes
Every element in `tokens` MUST be an object with `"type"` equal to one of:
1. **`"text"`**: `{ "type": "text", "text": "..." }` — prose, spaces, punctuation, unmeasured ingredient mentions (`"Add egg yolks"`), relative fractions (`"one-third of whipped whites"`, `"remaining batter"`), plain item/pulse counts (`"2 baking sheets"`, `"10 pulses"`, `"3 or 4 times"`), week/month durations (`"up to 6 weeks"`, `"1 month"`), rotation angles (`"rotate 180 degrees"`), and physical measuring utensils (`"back of 1 teaspoon dipped in flour"`).
2. **`"ingredient"`**: `{ "type": "ingredient", "ingredientIndex": int|null, "name": string, "quantity": number, "maxQuantity": number|null, "unit": string|null, "fixed": boolean }`
   - Consume `"of"` / `"of the"` inside `"text"` BEFORE or omit it cleanly so `"Heat 2 teaspoons of the oil"` becomes `"Heat "` + `ingredient(2 tsp "oil")` (NEVER leave `"Heat of the "` or trailing `" more oil"`!).
   - Set **`"fixed": true`** ONLY when the amount is a fixed per-item portion that does NOT multiply linearly when per-item serving count or fixed application applies (e.g., `"Spread 1 tablespoon butter on each roll"`, `"1 teaspoon dipping sauce per dumpling"`). Otherwise `"fixed": false`.
3. **`"mixture"`**: `{ "type": "mixture", "name": string, "quantity": number, "maxQuantity": number|null, "unit": string|null, "fixed": boolean }` — combined mixtures (`"1/4 cup glaze"`, `"Reserve 3 tablespoons pan drippings"`, `"all but 1 teaspoon fat"`), and set `"fixed": true` for fixed per-item mixture portions (`"1 tablespoon filling"` repeated per dumpling/meatball).
4. **`"timer"`**: `{ "type": "timer", "amount": number, "maxAmount": number|null, "unit": "second"|"minute"|"hour"|"day", "label": string }` — concise descriptive action label (`"Simmer sauce"`, `"Bake rolls"`, NEVER `"Cook"` or `"Timer"`). Consume the ENTIRE `"6 to 8 minutes"` / `"one hour"` span (leaving NO trailing `" minutes"` or `" to 8 minutes"` in adjacent `"text"`!).
5. **`"temperature"`**: `{ "type": "temperature", "amount": number, "maxAmount": number|null, "unit": "fahrenheit"|"celsius" }` — consume `" degrees"` / `"°F"` inside the token!
6. **`"dimension"`**: `{ "type": "dimension", "amount": number, "maxAmount": number|null, "unit": "inch"|"foot"|"yard"|"millimeter"|"centimeter"|"meter", "hyphenated": boolean }` — set `"hyphenated": true` ONLY when hyphenated (`"12-inch skillet"`, `"1/4-inch-wide strips"` -> `dim(0.25,"inch",true)` + `"-wide strips"`). For `"9 by 13-inch"`, use TWO separate `dimension` tokens (`dim(9,"inch",false)` + `" by "` + `dim(13,"inch",true)`).
7. **`"fixed"`**: `{ "type": "fixed", "name": string|null, "amount": number, "maxAmount": number|null, "unit": string|null, "hyphenated": boolean }` — **ONLY** for:
   - Vessel/pan capacities: `"12-cup Bundt pan"` (`name: "Bundt pan", amount: 12, unit: "cup", hyphenated: true`), `"4-cup liquid measuring cup"`.
   - Unlisted incremental `"at a time"` / `"increments"` rates: `"adding 1 teaspoon at a time"` (`name: null, amount: 1, unit: "teaspoon", hyphenated: false`).
   - Parenthetical/informational check-yields: `"(you should have "` + `fixed(name: null, amount: 5, maxAmount: 6, unit: "cup", hyphenated: false)` + `")"`, keeping `"(you should have "` BEFORE the `fixed` token!
   - **NEVER** use `type: "fixed"` for brining/dissolving/boiling water (`"2 quarts cold water"`, `"Bring 6 cups water to boil"` — use `type: "ingredient"` with `ingredientIndex: null`!).

### Critical Single-Bound Rule (`"up to X"`)
- `"up to 24 hours"`, `"up to 3 days"`, `"up to 2 inches"`, `"up to 1 tablespoon water"` MUST emit `"up to "` as `"text"` followed by a single-bound token (`amount/quantity = X`, `maxAmount/maxQuantity = null`). NEVER invent `0..X` or `1..X` ranges, and NEVER leave `"10 to "` before `{10..11}`!

---

## 6. Curated Tags List

Choose strictly from these exact tag strings:
- **Meal & Course**: `Breakfast`, `Brunch`, `Lunch`, `Dinner`, `Snack`, `Midnight Snack`, `Dessert`, `Appetizer`, `Starter`, `Side Dish`, `Main Course`, `Salad`, `Soup`, `Stew`, `Bread`, `Baked Goods`, `Sandwich`, `Beverage`, `Drink`, `Cocktail`, `Mocktail`, `Condiment`, `Sauce`, `Dip`, `Dressing`, `Spread`, `Marinade`
- **Cuisine**: `American`, `Southern`, `BBQ`, `Tex-Mex`, `Mexican`, `Cajun`, `Creole`, `Caribbean`, `South American`, `Cuban`, `Brazilian`, `Hawaiian`, `Italian`, `French`, `Spanish`, `Tapas`, `Greek`, `German`, `Eastern European`, `British`, `Irish`, `Scandinavian`, `East Asian`, `Chinese`, `Japanese`, `Korean`, `Southeast Asian`, `Thai`, `Vietnamese`, `Indonesian`, `Filipino`, `South Asian`, `Indian`, `Pakistani`, `Mediterranean`, `Middle Eastern`, `Lebanese`, `Turkish`, `Persian`, `North African`, `Moroccan`
- **Dietary**: `Gluten-Free`, `Dairy-Free`, `Nut-Free`, `Peanut-Free`, `Tree-Nut-Free`, `Egg-Free`, `Soy-Free`, `Shellfish-Free`, `Fish-Free`, `Sesame-Free`, `Vegetarian`, `Vegan`, `Pescatarian`, `Keto`, `Low-Carb`, `Paleo`, `Whole30`, `Halal`, `Kosher`, `Alcohol`, `High-Protein`, `Low-Sodium`, `Low-Fat`, `Low-Sugar`, `Diabetic-Friendly`, `Gut-Friendly`, `Heart-Healthy`, `High-Fiber`
- **Method & Equipment**: `Cast Iron`, `Carbon Steel`, `Stainless Steel`, `Wok`, `Dutch Oven`, `Sauté`, `Braise`, `Deep-Fry`, `Shallow-Fry`, `Pan-Sear`, `Stir-Fry`, `Boil & Simmer`, `Steamed`, `Baking`, `Roasting`, `Broiling`, `Sheet Pan`, `Instant Pot`, `Pressure Cooker`, `Slow Cooker`, `Air Fryer`, `Blender`, `Food Processor`, `Stand Mixer`, `Sous Vide`, `Toaster Oven`, `Microwave`, `Waffle Maker`, `Charcoal Grill`, `Gas Grill`, `Grill`, `Smoker`, `Campfire`, `One-Pot`, `One-Pan`, `No-Cook`, `Minimal Dishes`
- **Protein & Base**: `Chicken Breast`, `Chicken Thighs`, `Chicken`, `Turkey`, `Duck`, `Beef`, `Steak`, `Ground Beef`, `Pork`, `Pork Chops`, `Pork Tenderloin`, `Pork Shoulder`, `Lamb`, `Sausage`, `Bacon`, `Ham`, `Eggs`, `Salmon`, `White Fish`, `Shrimp`, `Scallops`, `Tuna`, `Crab`, `Lobster`, `Mussels`, `Tofu`, `Tempeh`, `Lentils`, `Beans`, `Chickpeas`, `Edamame`, `Pasta`, `Noodles`, `Rice`, `Potatoes`, `Sweet Potatoes`, `Grains`, `Quinoa`, `Farro`, `Couscous`, `Pizza`, `Polenta`
- **Occasion & Preservation**: `Spring`, `Summer`, `Autumn`, `Winter`, `Comfort Food`, `Light & Refreshing`, `Cozy & Warming`, `Rainy Day`, `Crowd-Pleaser`, `Potluck`, `Date Night`, `Dinner Party`, `Holiday`, `Thanksgiving`, `Christmas`, `Game Day`, `Finger Food`, `Weeknight`, `Camping`, `Picnic`, `Road Trip`, `Packed Lunch`, `Freezer-Friendly`, `Meal Prep`, `Batch Cooking`, `Leftover-Friendly`, `Reheats Well`, `Make-Ahead`, `Overnight Marinate`, `Quick Prep`, `Slow-Cooked`, `30-Minute Meal`, `Canning & Pickling`, `Dehydrating`, `Fermented`
