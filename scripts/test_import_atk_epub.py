import os
import sys
import unittest
from xml.etree import ElementTree as ET

sys.path.insert(0, os.path.dirname(__file__))
import import_atk_epub as atk

NS_X = {"x": "http://www.w3.org/1999/xhtml"}


class ImportAtkEpubTests(unittest.TestCase):
    def test_extract_ingredient_links_rewrites_single_dual_section_and_ignores_sidebar(self):
        id_map = {
            "037_c015.xhtml#c015-s1225": {
                "title": "Foolproof All-Butter Dough for Single-Crust Pie",
                "is_sidebar": False,
            },
            "037_c015.xhtml#c015-ss335": {
                "title": "Foolproof Dough for Single-Crust Pie",
                "is_sidebar": False,
            },
            "037_c015.xhtml#c015-s1259": {
                "title": "Free-Form Summer Fruit Tart",
                "is_sidebar": False,
            },
            "023_c008.xhtml#new3": {
                "title": "Pan-Seared Shrimp",
                "is_sidebar": True,
            },
        }

        p_single = ET.fromstring(
            '<p xmlns="http://www.w3.org/1999/xhtml">1 recipe single-crust pie dough '
            '(<a href="037_c015.xhtml#c015-s1225">this page</a>), fitted into a 9-inch pie plate</p>'
        )
        text, links = atk.extract_ingredient_links_and_text(
            p_single, "009_c002.xhtml", "Quiche Lorraine", id_map, NS_X
        )
        self.assertEqual(["Foolproof All-Butter Dough for Single-Crust Pie"], links)
        self.assertEqual(
            "1 recipe Foolproof All-Butter Dough for Single-Crust Pie (this page), fitted into a 9-inch pie plate",
            text,
        )

        p_dual = ET.fromstring(
            '<p xmlns="http://www.w3.org/1999/xhtml">1 recipe single-crust pie dough '
            '(<a href="037_c015.xhtml#c015-s1225">this page</a> and '
            '<a href="037_c015.xhtml#c015-ss335">this page</a>)</p>'
        )
        text, links = atk.extract_ingredient_links_and_text(
            p_dual, "015_c006.xhtml", "Cast Iron Chicken Pot Pie", id_map, NS_X
        )
        self.assertEqual(
            [
                "Foolproof All-Butter Dough for Single-Crust Pie",
                "Foolproof Dough for Single-Crust Pie",
            ],
            links,
        )
        self.assertEqual(
            "1 recipe Foolproof All-Butter Dough for Single-Crust Pie (this page; or see Foolproof Dough for Single-Crust Pie)",
            text,
        )

        p_section = ET.fromstring(
            '<p xmlns="http://www.w3.org/1999/xhtml">1 recipe Rustic Tart Dough '
            '(<a href="037_c015.xhtml#c015-s1259">this page</a>), rolled into a 12-inch circle</p>'
        )
        text, links = atk.extract_ingredient_links_and_text(
            p_section, "038_c015.xhtml", "Rustic Free-Form Apple Tart", id_map, NS_X
        )
        self.assertEqual(["Free-Form Summer Fruit Tart"], links)
        self.assertEqual(
            "1 recipe Free-Form Summer Fruit Tart (Rustic Tart Dough; this page), rolled into a 12-inch circle",
            text,
        )

        p_sidebar = ET.fromstring(
            '<p xmlns="http://www.w3.org/1999/xhtml">1 pound shrimp, peeled and deveined '
            '(<a href="023_c008.xhtml#new3">see this page</a>)</p>'
        )
        text, links = atk.extract_ingredient_links_and_text(
            p_sidebar, "023_c008.xhtml", "Paella", id_map, NS_X
        )
        self.assertEqual([], links)
        self.assertIn("(see this page)", text)

        p_sidebar_no_see = ET.fromstring(
            '<p xmlns="http://www.w3.org/1999/xhtml">1 pound shrimp, peeled and deveined '
            '(<a href="023_c008.xhtml#new3">this page</a>)</p>'
        )
        _, links = atk.extract_ingredient_links_and_text(
            p_sidebar_no_see, "023_c008.xhtml", "Paella", id_map, NS_X
        )
        self.assertEqual([], links)

    def test_sort_recipes_topologically_handles_cross_chapter_commas_siblings_and_duplicates(self):
        ch1_master = {"title": "Rosé Sangria", "chapter": "Ch1", "type": "master", "ingredients": [{"text": "4 ounces Simple Syrup", "linked_recipes": []}]}
        ch1_sub = {"title": "Simple Syrup", "chapter": "Ch1", "type": "sub", "ingredients": [{"text": "1 cup sugar", "linked_recipes": []}], "source_parent_recipe": ch1_master}

        ch2_quiche = {
            "title": "Quiche Lorraine",
            "chapter": "Ch2",
            "type": "master",
            "ingredients": [
                {
                    "text": "1 recipe Foolproof All-Butter Dough for Single-Crust Pie (this page)",
                    "linked_recipes": ["Foolproof All-Butter Dough for Single-Crust Pie"],
                }
            ],
        }
        ch3_pork = {
            "title": "Easy Grilled Boneless Pork Chops",
            "chapter": "Ch3",
            "type": "master",
            "ingredients": [{"text": "1 recipe Onion, Olive, and Caper Relish (optional) (this page)", "linked_recipes": []}],
        }
        ch3_relish = {
            "title": "Onion, Olive, and Caper Relish",
            "chapter": "Ch3",
            "type": "sub",
            "ingredients": [{"text": "1 onion", "linked_recipes": []}],
            "source_parent_recipe": ch3_pork,
        }
        ch4_rub_early = {"title": "Spice Rub", "chapter": "Ch4", "type": "sub", "ingredients": [{"text": "1 tbsp cumin", "linked_recipes": []}]}
        ch4_ribs = {
            "title": "Memphis Ribs",
            "chapter": "Ch4",
            "type": "master",
            "ingredients": [{"text": "1 recipe Spice Rub", "linked_recipes": []}],
        }
        ch12_bread = {
            "title": "Almost No-Knead Sourdough Bread",
            "chapter": "Ch12",
            "type": "master",
            "ingredients": [{"text": "1/3 cup mature sourdough starter", "linked_recipes": []}],
        }
        ch12_starter = {
            "title": "Sourdough Starter",
            "chapter": "Ch12",
            "type": "sub",
            "ingredients": [{"text": "5 cups flour", "linked_recipes": []}],
            "source_parent_recipe": ch12_bread,
        }
        ch14_pavlova = {
            "title": "Pavlova with Fruit and Whipped Cream",
            "chapter": "Ch14",
            "type": "master",
            "ingredients": [{"text": "1 recipe Orange, Cranberry, and Mint Topping", "linked_recipes": []}],
        }
        ch14_topping = {
            "title": "Orange, Cranberry, and Mint Topping",
            "chapter": "Ch14",
            "type": "sub",
            "ingredients": [{"text": "1 cup cranberries", "linked_recipes": []}],
            "source_parent_recipe": ch14_pavlova,
        }
        ch15_dough = {
            "title": "Foolproof All-Butter Dough for Single-Crust Pie",
            "chapter": "Ch15",
            "type": "master",
            "ingredients": [{"text": "2 1/2 cups flour", "linked_recipes": []}],
        }
        ch15_rub_late = {"title": "Spice Rub", "chapter": "Ch15", "type": "sub", "ingredients": [{"text": "1 tbsp sugar", "linked_recipes": []}]}

        recipes = [
            ch1_master,
            ch1_sub,
            ch2_quiche,
            ch3_pork,
            ch3_relish,
            ch4_rub_early,
            ch4_ribs,
            ch12_bread,
            ch12_starter,
            ch14_pavlova,
            ch14_topping,
            ch15_dough,
            ch15_rub_late,
        ]
        atk._resolve_sibling_subrecipe_references(recipes)
        sorted_r = atk.sort_recipes_topologically(recipes)
        pos = {id(r): idx for idx, r in enumerate(sorted_r)}

        self.assertLess(pos[id(ch15_dough)], pos[id(ch2_quiche)])
        self.assertLess(pos[id(ch1_sub)], pos[id(ch1_master)])
        self.assertLess(pos[id(ch3_relish)], pos[id(ch3_pork)])
        self.assertLess(pos[id(ch12_starter)], pos[id(ch12_bread)])
        self.assertLess(pos[id(ch14_topping)], pos[id(ch14_pavlova)])
        self.assertLess(pos[id(ch4_rub_early)], pos[id(ch4_ribs)])

    @unittest.skipUnless(os.path.exists(atk.DEFAULT_EPUB_PATH), "ATK EPUB not present locally")
    def test_full_epub_topological_order_has_zero_inverted_dependencies(self):
        parsed = atk.parse_atk_epub(atk.DEFAULT_EPUB_PATH)
        sorted_recipes = atk.sort_recipes_topologically(parsed)
        self.assertEqual(1644, len(sorted_recipes))

        pos = {}
        by_title = {}
        for idx, r in enumerate(sorted_recipes):
            pos.setdefault(r["title"], []).append(idx)
            by_title.setdefault(r["title"], r)

        inverted = []
        for idx, r in enumerate(sorted_recipes):
            for ing in r["ingredients"]:
                for lt in ing.get("linked_recipes", []):
                    t_indices = pos.get(lt, [])
                    if not any(ti < idx for ti in t_indices):
                        inverted.append((idx, r["title"], lt, t_indices))
        self.assertEqual([], inverted)

        self.assertLess(
            pos["Foolproof All-Butter Dough for Single-Crust Pie"][0],
            pos["Simple Cheese Quiche"][0],
        )
        self.assertLess(
            pos["Foolproof All-Butter Dough for Single-Crust Pie"][0],
            pos["Quiche Lorraine"][0],
        )
        self.assertLess(
            pos["Foolproof All-Butter Dough for Single-Crust Pie"][0],
            pos["Cast Iron Chicken Pot Pie"][0],
        )
        self.assertLess(
            pos["Foolproof Dough for Single-Crust Pie"][0],
            pos["Cast Iron Chicken Pot Pie"][0],
        )
        self.assertLess(
            pos["Sourdough Starter"][0],
            pos["Almost No-Knead Sourdough Bread"][0],
        )
        self.assertLess(
            pos["Simple Syrup"][0],
            pos["Rosé Sangria"][0],
        )
        self.assertLess(
            pos["Onion, Olive, and Caper Relish"][0],
            pos["Easy Grilled Boneless Pork Chops"][0],
        )
        self.assertLess(
            pos["Orange, Cranberry, and Mint Topping"][0],
            pos["Pavlova with Fruit and Whipped Cream"][0],
        )
        self.assertLess(
            pos["Soy-Mustard Glaze"][0],
            pos["Glazed Salmon"][0],
        )

        glazed_ings = [i["text"] for i in by_title["Glazed Salmon"]["ingredients"]]
        self.assertTrue(any("Soy-Mustard Glaze" in t for t in glazed_ings), glazed_ings)


if __name__ == "__main__":
    unittest.main()
