namespace GoblinTweaks.Core;

/// <summary>Which recipes count for the crafting log (the game's total of "crafted" recipes).</summary>
internal static class CraftingLogRecipes
{
    // NotebookDivisionCategory of the "Housing" and "Others" groups of the Special Recipes.
    private const uint HousingCategory = 2;
    private const uint OthersCategory  = 3;

    // NotebookDivision "Dyes", under Others: the log shows no [crafted/total] count for it, unlike Ornaments (1) and (2).
    private const uint DyesDivision = 1011;

    private static HashSet<uint>? _ids;

    /// <summary>
    /// The recipes that count for the crafting log: the level-based recipes of every class and, of the Special Recipes,
    /// the Housing ones and the Ornaments of Others (the groups the log shows a [crafted/total] count for). Master
    /// recipes, Dyes and the other special groups (sidequests, deliveries, collectables...) don't. Read from the game
    /// data once.
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
            if (!Counts(list.RowId, divisions)) continue;

            foreach (var r in list.Recipe)
                if (r.RowId != 0 && r.IsValid) set.Add(r.RowId);
        }

        return set;
    }

    private static bool Counts(uint listRow, Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.NotebookDivision> divisions)
    {
        if (listRow < 1000) return true; // level-based

        var divisionId = 1000 + (listRow - 1000) / 8;
        if (!divisions.TryGetRow(divisionId, out var division)) return false;

        return division.NotebookDivisionCategory.RowId switch
        {
            HousingCategory => true,
            OthersCategory  => divisionId != DyesDivision, // Ornaments (1), Ornaments (2)
            _               => false,
        };
    }
}
