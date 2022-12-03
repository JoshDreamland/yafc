using System;
using System.Buffers.Text;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;
using SDL2;
using YAFC.Model;
using YAFC.Parser;
using YAFC.UI;

namespace YAFC
{

    public class ProjectPageSettingsPanel : PseudoScreen
    {
        private static readonly ProjectPageSettingsPanel Instance = new ProjectPageSettingsPanel();

        private ProjectPage editingPage;
        private string name;
        private FactorioObject icon;
        private Action<string, FactorioObject> callback;
        
        public static void Build(ImGui gui, ref string name, FactorioObject icon, Action<FactorioObject> setIcon)
        {
            gui.BuildTextInput(name, out name, "Input name");
            if (gui.BuildFactorioObjectButton(icon, 4f, MilestoneDisplay.None, SchemeColor.Grey))
            {
                SelectObjectPanel.Select(Database.objects.all, "Select icon", setIcon);
            }

            if (icon == null && gui.isBuilding)
                gui.DrawText(gui.lastRect, "And select icon", RectAlignment.Middle);
        }

        public static void Show(ProjectPage page, Action<string, FactorioObject> callback = null)
        {
            Instance.editingPage = page;
            Instance.name = page?.name;
            Instance.icon = page?.icon;
            Instance.callback = callback;
            MainScreen.Instance.ShowPseudoScreen(Instance);
        }
        
        private Dictionary<string, Recipe> DetermineBestRecipes() {
            var result = new Dictionary<string, Recipe>();
            foreach (var good in Database.goods.all) {
                foreach (var recipe in good.production) {
                    if (result.TryAdd(good.name, recipe)) {
                        foreach (var product in recipe.products)
                            result.TryAdd(product.goods.name, recipe);
                        continue;
                    }
                    Recipe old = result[good.name];
                    if (recipe.Cost() < old.Cost()) result[good.name] = recipe;
                }
            }
            return result;
        }

        // Adds a recipe to the given list and adds its rank to the given dictionary. Returns its rank.
        private int AddRecipeAndDependencies(Recipe recipe, List<Recipe> recipe_list, Dictionary<string, int> ranks, Dictionary<string, Recipe> best_recipes) {
            int rank;
            if (ranks.TryGetValue(recipe.name, out rank)) return rank;
            rank = 0;
            ranks.Add(recipe.name, 0);
            foreach (var ingredient in recipe.ingredients) {
                Recipe best_recipe;
                if (!best_recipes.TryGetValue(ingredient.goods.name, out best_recipe)) {
                    Console.Error.WriteLine(ingredient.goods.name + " has no recipe but is an ingredient...");
                    continue;
                }
                rank = Math.Max(rank, AddRecipeAndDependencies(best_recipe, recipe_list, ranks, best_recipes) + 1);
            }
            ranks[recipe.name] = rank;
            recipe_list.Add(recipe);
            return rank;
        }
        
        private List<Recipe> ComputeRecipeHierarchy(Dictionary<string, int> ranks = null) {
            if (ranks == null) ranks = new Dictionary<string, int>();
            var result = new List<Recipe>();
            var best_recipes = DetermineBestRecipes();
            foreach (var recipe in best_recipes) {
                AddRecipeAndDependencies(recipe.Value, result, ranks, best_recipes);
            }
            return result;
        }

        // Puts the input/output goods of the given recipes in a list, grouped
        // via Markov model. This places recipes with similar inputs nearby.
        private List<Goods> MarkovSortedGoods(List<Recipe> recipes) {
            var markov = new Dictionary<string, Dictionary<string, int>>();
            var seen = new Dictionary<string, int>();
            var mapped = new Dictionary<string, Goods>();
            foreach (var recipe in recipes) {
                foreach (var product in recipe.ingredients) {
                    mapped.TryAdd(product.goods.name, product.goods);
                    if (!seen.TryAdd(product.goods.name, 1))
                        seen[product.goods.name] += 1;
                    if (markov.TryAdd(product.goods.name, null))
                      markov[product.goods.name] = new Dictionary<string, int>();
                    foreach (var product2 in recipe.ingredients) {
                        if (product2.goods.name != product.goods.name) {
                            if (!markov[product.goods.name].TryAdd(product2.goods.name, 1))
                                markov[product.goods.name][product2.goods.name] += 1;
                        }
                    }
                    foreach (var product2 in recipe.products) {
                        if (product2.goods.name != product.goods.name) {
                            if (!markov[product.goods.name].TryAdd(product2.goods.name, 1))
                                markov[product.goods.name][product2.goods.name] += 1;
                        }
                    }
                }
            }
            var ordering = new System.Collections.Generic.PriorityQueue<string, int>();
            foreach (var item in seen) {
                // Use a very low priority so that we don't iterate elements by
                // total quantity until we're completely out of Markov suggestions.
                // Start with the most basic (lowest-value) items.
                double cost = mapped[item.Key].Cost();
                cost = double.IsInfinity(cost) || cost < 0 ? 1000 : Math.Sqrt(cost * 10);
                ordering.Enqueue(item.Key, 110000000 - item.Value * 100000 + (int) cost);
                Console.Write("Enqueue initializer " + item.Key + " @" + (110000000 - item.Value * 100000 + (int) cost).ToString()
                              + " (item was used in " + item.Value.ToString() + " recipe(s); cost heuristic " + cost.ToString() + ")\n");
            }
            var result = new List<Goods>();
            var written = new HashSet<string>();
            string current;
            int priority;
            while (ordering.TryDequeue(out current, out priority)) {
                if (written.Add(current)) {
                    Console.Write("Accepting " + current + " (" + priority.ToString() + "), which should be followed by:  [\n");
                    result.Add(mapped[current]);
                    if (priority > 100000000) priority = 0;
                    foreach (var edge in markov[current]) {
                        // Favor breadth-first unpacking of Markov pairs.
                        int newpriority = -edge.Value;
                        ordering.Enqueue(edge.Key, newpriority);
                        Console.Write("  " + edge.Key + " (" + newpriority + ")\n");
                    }
                    Console.Write("]\n");
                }
            }
            return result;
        }

        List<Recipe> ComputeAvailableRecipes() {
            var recipes = new List<Recipe>();
            foreach (var recipe in ComputeRecipeHierarchy()) {
                if (!Milestones.Instance.IsAccessibleWithCurrentMilesones(recipe)) continue;
                if (recipe.name.Contains("delivery-cannon")) continue;
                recipes.Add(recipe);
            }
            return recipes;
        }

        // An Excel-like spreadsheet representation of all game recipes.
        // Not to be confused with the production sheet YAFC users create.
        struct RecipeSpreadsheet {
            public struct MatrixRow {
                public Recipe recipe;
                public List<int> ordered_counts;
                public MatrixRow(Recipe r) {
                    recipe = r;
                    ordered_counts = new List<int>();
                }
            }
            public List<Goods> goods { get; }
            public List<MatrixRow> rows { get; }

            // Sort by first ingredient column. Recipes with more incredients
            // toward the first column are sorted to the top of the list.
            void Sort() {
                rows.Sort((l1, l2) => {
                    for (int i = 0; i < l1.ordered_counts.Count; ++i) {
                        if (l1.ordered_counts[i] != 0) {
                            if (l2.ordered_counts[i] == 0) return -1;
                        } else {
                            if (l2.ordered_counts[i] != 0) return 1;
                        }
                        if (i > l2.ordered_counts.Count) return 1;
                    }
                    if (l2.ordered_counts.Count > l1.ordered_counts.Count)
                        return -1;
                    return 0;
                });
            }

            public RecipeSpreadsheet(List<Goods> goods, List<MatrixRow> rows) {
                this.goods = goods;
                this.rows = rows;
            }
        }

        // Tabulates the given recipes as a spreadsheet of inputs/outputs.
        RecipeSpreadsheet ComputeRecipeSheet(List<Recipe> recipes) {
            var goods = MarkovSortedGoods(recipes);
            var rows = new List<RecipeSpreadsheet.MatrixRow>();
            foreach (var recipe in recipes) {
                var amounts = new Dictionary<string, float>();
                foreach (var ingredient in recipe.ingredients) {
                    amounts.Add(ingredient.goods.name, -ingredient.amount);
                }
                foreach (var product in recipe.products) {
                    if (!amounts.TryAdd(product.goods.name, product.amount))
                        amounts[product.goods.name] += product.amount;
                }
                var row = new RecipeSpreadsheet.MatrixRow(recipe);
                foreach (var good in goods) {
                    float amount = 0;
                    amounts.TryGetValue(good.name, out amount);
                    row.ordered_counts.Add((int) amount);
                }
                rows.Add(row);
            }
            return new RecipeSpreadsheet(goods, rows);
        }

        public override void Build(ImGui gui)
        {
            gui.spacing = 3f;
            BuildHeader(gui, editingPage == null ? "Create new page" : "Edit page icon and name");
            Build(gui, ref name, icon, s =>
            {
                icon = s;
                Rebuild();
            });

            using (gui.EnterRow(0.5f, RectAllocator.RightRow))
            {
                if (editingPage == null && gui.BuildButton("Create", active:!string.IsNullOrEmpty(name)))
                {
                    callback?.Invoke(name, icon);
                    Close();
                }

                if (editingPage != null && gui.BuildButton("OK", active:!string.IsNullOrEmpty(name)))
                {
                    if (editingPage.name != name || editingPage.icon != icon)
                    {
                        editingPage.RecordUndo(true).name = name;
                        editingPage.icon = icon;
                    }
                    Close();
                }

                if (gui.BuildButton("Cancel", SchemeColor.Grey))
                    Close();

                if (editingPage != null && gui.BuildButton("Other tools", SchemeColor.Grey, active:!string.IsNullOrEmpty(name)))
                {
                    gui.ShowDropDown(OtherToolsDropdown);
                }

                gui.allocator = RectAllocator.LeftRow;
                if (editingPage != null && gui.BuildRedButton("Delete page"))
                {
                    Project.current.RemovePage(editingPage);
                    Close();
                }
            }
        }

        private void OtherToolsDropdown(ImGui gui)
        {
            if (gui.BuildContextMenuButton("Duplicate page"))
            {
                gui.CloseDropdown();
                var project = editingPage.owner;
                var collector = new ErrorCollector();
                var serializedCopy = JsonUtils.Copy(editingPage, project, collector);
                if (collector.severity > ErrorSeverity.None)
                    ErrorListPanel.Show(collector);
                if (serializedCopy != null)
                {
                    serializedCopy.GenerateNewGuid();
                    serializedCopy.icon = icon;
                    serializedCopy.name = name;
                    project.RecordUndo().pages.Add(serializedCopy);
                    MainScreen.Instance.SetActivePage(serializedCopy);
                    Close();
                }
            }

            if (gui.BuildContextMenuButton("Share (export string to clipboard)"))
            {
                gui.CloseDropdown();
                var data = JsonUtils.SaveToJson(editingPage);
                using (var targetStream = new MemoryStream())
                {
                    using (var compress = new DeflateStream(targetStream, CompressionLevel.Optimal, true))
                    {
                        using (var writer = new BinaryWriter(compress, Encoding.UTF8, true))
                        {
                            // write some magic chars and version as a marker
                            writer.Write("YAFC\nProjectPage\n".AsSpan());
                            writer.Write(YafcLib.version.ToString().AsSpan());
                            writer.Write("\n\n\n".AsSpan());
                        }
                        data.CopyTo(compress);
                    }
                    var encoded = Convert.ToBase64String(targetStream.GetBuffer(), 0, (int)targetStream.Length);
                    SDL.SDL_SetClipboardText(encoded);
                }
            }

            if (gui.BuildContextMenuButton("Export uncompressed"))
            {
                gui.CloseDropdown();
                var data = JsonUtils.SaveToJson(editingPage);
                SDL.SDL_SetClipboardText(Encoding.UTF8.GetString(data.GetBuffer()));
            }

            if (gui.BuildContextMenuButton("Export recipe graph"))
            {
                string nodes = "", edges = "";
                foreach (var recipe in Database.recipes.all) {
                    if (!Milestones.Instance.IsAccessibleWithCurrentMilesones(recipe)) {
                        nodes += "    // " + recipe.name + " is not enabled\n";
                        continue;
                    }
                    nodes += "    \"" + recipe.name + "\" [shape=box]\n";

                    edges += "  // " + recipe.name + "\n";
                    foreach (var ingredient in recipe.ingredients)
                        edges += "  \"" + ingredient.goods.name + "\" -> \"" + recipe.name + "\"\n";
                    foreach (var product in recipe.products)
                        edges += "  \"" + recipe.name + "\" -> \"" + product.goods.name + "\"\n";
                    edges += "\n";
                }
                SDL.SDL_SetClipboardText("digraph recipes {\n  {\n" + nodes + "\n  }\n\n" + edges + "\n}");
                gui.CloseDropdown();
            }

            if (gui.BuildContextMenuButton("Export recipe sheet"))
            {
                var matrix = ComputeRecipeSheet(ComputeAvailableRecipes());
                matrix.rows.Sort();

                var sheet = "Recipe Name";
                foreach (var good in matrix.goods) {
                    sheet += "\t" + good.name;
                }
                sheet += "\n";
                foreach (var row in matrix.rows) {
                    var line = row.recipe.name;
                    int min_ind = row.ordered_counts.Count, max_ind = 0;
                    for (int i = 0; i < row.ordered_counts.Count; ++i) {
                        if (row.ordered_counts[i] != 0) {
                            min_ind = Math.Min(min_ind, i);
                            max_ind = i;
                        }
                    }
                    for (int i = 0; i < row.ordered_counts.Count; ++i) {
                        line += "\t" + ((i >= min_ind && i <= max_ind) ? row.ordered_counts[i].ToString() : "");
                    }
                    sheet += line + "\n";
                }
                SDL.SDL_SetClipboardText(sheet);
                gui.CloseDropdown();
            }

            if (editingPage == MainScreen.Instance.activePage && gui.BuildContextMenuButton("Make full page screenshot"))
            {
                var screenshot = MainScreen.Instance.activePageView.GenerateFullPageScreenshot();
                ImageSharePanel.Show(screenshot, editingPage.name);
                gui.CloseDropdown();
            }
        }

        public static void LoadProjectPageFromClipboard()
        {
            var collector = new ErrorCollector();
            var project = Project.current;
            ProjectPage page = null;
            try
            {
                var text = SDL.SDL_GetClipboardText();
                var compressedBytes = Convert.FromBase64String(text.Trim());
                using (var deflateStream = new DeflateStream(new MemoryStream(compressedBytes), CompressionMode.Decompress))
                {
                    using (var ms = new MemoryStream())
                    {
                        deflateStream.CopyTo(ms);
                        var bytes = ms.GetBuffer();
                        var index = 0;
                        if (DataUtils.ReadLine(bytes, ref index) != "YAFC" || DataUtils.ReadLine(bytes, ref index) != "ProjectPage")
                            throw new InvalidDataException();
                        var version = new Version(DataUtils.ReadLine(bytes, ref index) ?? "");
                        if (version > YafcLib.version)
                            collector.Error("String was created with the newer version of YAFC (" + version + "). Data may be lost.", ErrorSeverity.Important);
                        DataUtils.ReadLine(bytes, ref index); // reserved 1
                        if (DataUtils.ReadLine(bytes, ref index) != "") // reserved 2 but this time it is requried to be empty
                            throw new NotSupportedException("Share string was created with future version of YAFC (" + version + ") and is incompatible");
                        page = JsonUtils.LoadFromJson<ProjectPage>(new ReadOnlySpan<byte>(bytes, index, (int) ms.Length - index), project, collector);
                    }
                }
            }
            catch (Exception ex)
            {
                collector.Exception(ex, "Clipboard text does not contain valid YAFC share string", ErrorSeverity.Critical);
            }

            if (page != null)
            {
                var existing = project.FindPage(page.guid); 
                if (existing != null)
                {
                    MessageBox.Show((haveChoice, choice) =>
                    {
                        if (!haveChoice)
                            return;
                        if (choice)
                            project.RemovePage(existing);
                        else
                            page.GenerateNewGuid();
                        project.RecordUndo().pages.Add(page);
                        MainScreen.Instance.SetActivePage(page);
                    }, "Page already exists",
                    "Looks like this page already exists with name '" + existing.name + "'. Would you like to replace it or import as copy?", "Replace", "Import as copy");
                }
                else
                {
                    project.RecordUndo().pages.Add(page);
                    MainScreen.Instance.SetActivePage(page);
                }
            }

            if (collector.severity > ErrorSeverity.None)
            {
                ErrorListPanel.Show(collector);
            }
        }
    }
}
