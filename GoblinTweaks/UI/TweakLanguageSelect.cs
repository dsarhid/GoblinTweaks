using System.Numerics;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// The "Language" choice of a tweak's own window: a caption and a drop-down. The window is written in the
/// language chosen here, whatever the GoblinTweaks language is; the first choice follows GoblinTweaks.
/// </summary>
internal static class TweakLanguageSelect
{
    public const float CaptionW = 64f;
    public const float SelectW  = 170f;

    /// <summary>Width the caption and the drop-down take together.</summary>
    public const float Width = CaptionW + SelectW + 8f;

    /// <summary>Adds the caption and the drop-down with the top-left corner at <paramref name="position"/>. Add it last so its list opens on top.</summary>
    public static void Add(NativeAddon owner, Tweak tweak, Vector2 position)
    {
        var language = tweak.UiLanguage;

        new TextNode
        {
            String    = Loc.GetIn(language, "Settings.Language"),
            Position  = position + new Vector2(0f, 5f),
            Size      = new Vector2(CaptionW, 20f),
            TextColor = new Vector4(0.62f, 0.62f, 0.62f, 1f),
            FontSize  = 13,
        }.AttachNode(owner);

        // An empty code ("same as GoblinTweaks") would look like "nothing selected" to the drop-down, so it gets a name.
        const string Same = "same";
        string ToKey(string code) => code.Length == 0 ? Same : code;
        string FromKey(string key) => key == Same ? string.Empty : key;

        var texts = new Dictionary<string, string> { [Same] = Loc.GetIn(language, "Settings.LanguageSame") };
        foreach (var (code, name) in Loc.Languages) texts[code] = name;
        var keys = texts.Keys.ToList();

        var select = new StringDropDownNode
        {
            Position         = position + new Vector2(CaptionW + 8f, 0f),
            Size             = new Vector2(SelectW, 26f),
            MaxListOptions   = keys.Count,
            GetLabelFunction = key => texts.GetValueOrDefault(key, key),
        };
        select.OnOptionSelected = key => tweak.SetLanguage(FromKey(key));
        select.AttachNode(owner);

        // Set after attaching so the box shows the current choice's label instead of staying empty.
        select.Options        = keys;
        select.SelectedOption = keys.Contains(ToKey(tweak.Language)) ? ToKey(tweak.Language) : Same;
    }
}
