import unittest

from evaluate_atk_ingestion import converted_amount, ingredient_amounts_in_prose, score_result


class EvaluationTests(unittest.TestCase):
    def test_equivalent_volume_units_and_count_without_unit(self):
        self.assertEqual(2, converted_amount({"quantity": 6, "unit": "Tsp"}, "quantity", "Tbsp"))
        self.assertEqual(3, converted_amount({"quantity": 3, "unit": None}, "quantity", "Whole"))
        with self.assertRaises(ValueError):
            converted_amount({"quantity": 1, "unit": "Lb"}, "quantity", "Cup")

    def test_process_measurements_are_not_ingredient_amounts(self):
        self.assertEqual([], ingredient_amounts_in_prose(
            "Simmer 5 minutes until reduced to 1/3 cup. Use a 12-inch pan at 350 degrees."
        ))
        self.assertEqual(["2 tablespoons"], ingredient_amounts_in_prose(
            "Add 2 tablespoons oil, then reduce sauce to 1 cup."
        ))

    def test_salt_does_not_match_unsalted_butter(self):
        source, result = self.fixture()
        result["draft"]["ingredients"].append({
            "displayName": "unsalted butter", "quantity": 12, "unit": "Tbsp",
            "originalRawText": "12 tablespoons unsalted butter",
        })
        source["ingredients"].append({"text": "12 tablespoons unsalted butter"})
        result["draft"]["steps"][0]["ingredientIndices"].append(1)
        issues = score_result(
            {"ingredients": [{"name": "salt", "quantity": 0.5, "unit": "Tsp"}]}, source, result
        )
        self.assertEqual([], issues)

    def test_optional_extra_conserves_range_and_raw_bad_index_is_not_hidden(self):
        source, result = self.fixture()
        ingredient = result["draft"]["ingredients"][0]
        ingredient.update(displayName="oil", quantity=1.5, unit="Tbsp", originalRawText="1 1/2-2 tbsp oil")
        result["draft"]["ingredients"].append({
            **ingredient, "quantity": 0.5, "isOptional": True,
        })
        result["draft"]["steps"][0]["ingredientIndices"] = [0, 1]
        source["ingredients"] = [{"text": "1 1/2-2 tbsp oil"}]
        case = {"ingredients": [{"name": "oil", "range": [1.5, 2], "parts": [1.5, 0.5], "unit": "Tbsp"}]}
        self.assertEqual([], score_result(case, source, result))
        result["raw"] = {"steps": [{"ingredientIndices": [2]}]}
        self.assertIn("invalid index", score_result(case, source, result)[0])

    @staticmethod
    def fixture():
        return (
            {"title": "Sauce", "ingredients": [{"text": "1/2 teaspoon salt"}]},
            {"draft": {
                "title": "Sauce",
                "ingredients": [{
                    "displayName": "salt", "quantity": 0.5, "unit": "Tsp",
                    "originalRawText": "1/2 teaspoon salt",
                }],
                "steps": [{"instruction": "Mix the ingredients.", "ingredientIndices": [0], "timers": []}],
            }},
        )

    def test_explicit_step_portions_and_alternative_measurements(self):
        source, result = self.fixture()
        line = "4 tablespoons oil (2 ounces)"
        source["ingredients"] = [{"text": line}]
        ingredient = result["draft"]["ingredients"][0]
        ingredient.update(displayName="oil", quantity=4, unit="Tbsp", originalRawText=line,
                          alternativeMeasurement={"quantity": 2, "unit": "Oz"})
        step = result["draft"]["steps"][0]
        step["ingredientAmounts"] = [{"ingredientIndex": 0,
                                     "measurement": {"quantity": 2, "unit": "Tbsp"}}]
        result["draft"]["steps"].append({**step, "instruction": "Add the remaining oil."})
        case = {"ingredients": [{"name": "oil", "quantity": 4, "unit": "Tbsp", "parts": [2, 2],
                                 "alternativeMeasurement": {"quantity": 2, "unit": "Oz"}}]}
        self.assertEqual([], score_result(case, source, result))
        ingredient["alternativeMeasurement"]["quantity"] = 3
        self.assertTrue(any("incorrect alternative" in i for i in score_result(case, source, result)))
        step["ingredientAmounts"][0]["measurement"]["quantity"] = 5
        self.assertTrue(any("Wrong portions" in i for i in score_result(case, source, result)))


if __name__ == "__main__":
    unittest.main()
