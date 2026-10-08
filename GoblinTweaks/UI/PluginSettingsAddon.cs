using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>General plugin settings, native: the language of the GoblinTweaks windows and the font of their titles.</summary>
internal sealed class PluginSettingsAddon : SettingsPanelAddon
{
    private const float BlockH = 86f;

    public required ConfigStore Config { get; init; }

    protected override float HeaderHeight => BlockH * 2f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        LanguageSelect(origin, width, Loc.Get("Settings.Language"), Loc.Get("Settings.Language.Help"),
            [.. Loc.Languages.Select(language => (language.Code, language.Name))],
            () => Loc.CurrentLanguage,
            code =>
            {
                if (code == Loc.CurrentLanguage || code.Length == 0) return;
                Config.Data.Language = code;
                Config.Save();
                Loc.SetLanguage(code);
            });

        LanguageSelect(origin + new Vector2(0f, BlockH), width, Loc.Get("Settings.Font"), Loc.Get("Settings.Font.Help"),
            [.. UiFont.Choices.Select(font => (font.Key, font.Key))],
            () => UiFont.Key,
            key =>
            {
                if (key == UiFont.Key || key.Length == 0) return;
                Config.Data.FontFamily = key;
                Config.Save();
                UiFont.Set(key);
            });
    }

    /// <summary>A sample of every font the game offers, so the choice can be made by looking.</summary>
    protected override void Build()
    {
        Section(Loc.Get("Settings.Font.Preview"));

        foreach (var (key, type) in UiFont.Choices)
        {
            Note(key, Pad, ContentW, key == UiFont.Key ? Gold : Muted, 12);

            var size = UiFont.Size(type, 22);
            new TextNode
            {
                String      = "Ajustes · Settings · 0123456789",
                Position    = new Vector2(Pad, Y),
                Size        = new Vector2(ContentW, size + 8f),
                TextColor   = White,
                FontType    = type,
                FontSize    = size,
                CharSpacing = type == FontType.TrumpGothic ? 1u : 0u,
            }.AttachNode(Host);
            Y += size + 14f;
        }
    }
}
