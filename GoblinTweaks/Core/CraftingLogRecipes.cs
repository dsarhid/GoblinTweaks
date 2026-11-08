namespace GoblinTweaks.Core;

/// <summary>Which recipes count for the crafting log (the game's total of "crafted" recipes).</summary>
internal static class CraftingLogRecipes
{
    // NotebookDivisionCategory of the "Housing" group of the Special Recipes.
    private const uint HousingCategory = 2;

    private static HashSet<uint>? _ids;

    /// <summary>
    /// The recipes that count for the crafting log: the level-based recipes of every class and, of the Special Recipes,
    /// the Housing ones. Master recipes and the other special groups (sidequests, deliveries, collectables...) don't.
    /// Read from the game data once.
    /// </summary>
    /// <remarks>
    /// RecipeNotebookList has one row per class and log division: <c>craftType * 40 + division</c> for the level-based
    /// divisions (0-39), <c>1000 + (division - 1000) * 8 + craftType</c> for the rest, whose NotebookDivision row says
    /// which group of the log it is under.
    /// </remarks>
    public static HashSet<uint> Ids => _ids ??= Build();

    private static HashSet<uint> Build()
    {
        var lists     = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.RecipeNotebookList>();
        var divisions = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.NotebookDivision>();

        var set = new HashSet<uint>();
        foreach (var list in lists)
        {
            var counts = list.RowId < 1000
                || (divisions.TryGetRow(1000 + (list.RowId - 1000) / 8, out var division)
                    && division.NotebookDivisionCategory.RowId == HousingCategory);
            if (!counts) continue;

            foreach (var r in list.Recipe)
                if (r.RowId != 0 && r.IsValid) set.Add(r.RowId);
        }

        return set;
    }
}
