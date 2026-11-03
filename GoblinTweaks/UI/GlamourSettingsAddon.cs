using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Glamour;
using GoblinTweaks.Tweaks;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Settings of Goblin Glamour: the language of its windows, and the optional AI — provider, model, the user's own
/// key (never shown back once saved) and, for the "custom" provider, its URL and model. The drop-downs live in the
/// fixed strip on top so their lists are never clipped by the scrolling area.
/// </summary>
/// <remarks>
/// Choosing a provider changes the list of models. That list is never changed inside the provider's click: the game is
/// still handling that click and may still point at the old option buttons (freeing them there crashed the game). The
/// model drop-down is made again on the next frame instead, inside a holder that keeps its place under the provider's
/// list. The same happens when the provider's own list of models arrives (asked for once a key is there): providers
/// retire models, so the list in the plugin is only a first suggestion.
/// </remarks>
internal sealed unsafe class GlamourSettingsAddon : SettingsPanelAddon
{
    private const float RowH    = 34f;
    private const float SelectW = 300f;

    private ResNode? _modelSlot;
    private StringDropDownNode? _model;
    private bool _remakeModels;

    public GoblinGlamour? Tweak { get; init; }

    protected override float HeaderHeight => 6f + RowH * 3f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;
        var options = Tweak.Current;

        // Model first, so the provider's list (and the language list, added last) open over it.
        Caption(Tweak.Text("Settings.Model"), origin + new Vector2(Pad, 6f + RowH * 2f + 5f));
        _modelSlot = new ResNode
        {
            Position = origin + new Vector2(width - SelectW - Pad, 6f + RowH * 2f),
            Size     = new Vector2(SelectW, 26f),
        };
        _modelSlot.AttachNode(this);
        MakeModels();

        Caption(Tweak.Text("Settings.Provider"), origin + new Vector2(Pad, 6f + RowH + 5f));
        var keys = AiProviders.All.Select(provider => provider.Key).ToList();
        var provider = new StringDropDownNode
        {
            Position         = origin + new Vector2(width - SelectW - Pad, 6f + RowH),
            Size             = new Vector2(SelectW, 26f),
            MaxListOptions   = keys.Count,
            GetLabelFunction = key => Tweak.Text($"Provider.{key}"),
        };
        provider.OnOptionSelected = key =>
        {
            if (options.Provider == key) return;
            options.Provider = key;
            options.Model = string.Empty;
            Tweak.SaveCurrent();
            _remakeModels = true;   // next frame, outside this click
            RequestRebuild();
        };
        provider.AttachNode(this);
        provider.Options        = keys;
        provider.SelectedOption = AiProviders.Get(options.Provider).Key;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    /// <summary>
    /// The model list of the chosen provider, made new; none for the custom provider, whose model is typed below.
    /// Options are set after attaching (see the note on KamiToolKit drop-downs).
    /// </summary>
    private void MakeModels()
    {
        if (_modelSlot is null || Tweak is null) return;
        var options = Tweak.Current;
        var provider = AiProviders.Get(options.Provider);

        _model?.Dispose();
        _model = null;
        Tweak.RefreshModels(() => _remakeModels = true);
        var models = Tweak.Models;
        if (provider.Custom || models.Count == 0) return;

        _model = new StringDropDownNode
        {
            Size           = new Vector2(SelectW, 26f),
            MaxListOptions = 6,
        };
        _model.OnOptionSelected = model => { options.Model = model; Tweak.SaveCurrent(); };
        _model.AttachNode(_modelSlot);
        _model.Options        = [.. models];
        _model.SelectedOption = Tweak.AiModel;
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        if (_remakeModels)
        {
            _remakeModels = false;
            NativeFocus.Release(addon);
            MakeModels();
            NativeFocus.RefreshCollisions(addon);
        }
        base.OnUpdate(addon);
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var options = Tweak.Current;
        var provider = AiProviders.Get(options.Provider);

        Section(Tweak.Text("Settings.TryOn"));
        Number(Tweak.Text("Settings.TryOnSize"), Tweak.Text("Settings.TryOnSize.Help"), 25, 300, 5, options.TryOnPercent,
            Tweak.SetTryOnSize);

        Section(Tweak.Text("Settings.Glamourer"));
        Note(Tweak.Text(Tweak.GlamourerAvailable ? "Settings.Glamourer.On" : "Settings.Glamourer.Off"), Pad, ContentW, Muted);

        Section(Tweak.Text("Settings.Ai"));
        Note(Tweak.Text("Settings.Ai.Help"), Pad, ContentW, Muted);
        Space(8f);

        if (provider.Custom)
        {
            Input(Tweak.Text("Settings.CustomUrl"), Tweak.Text("Settings.CustomUrl.Help"), options.CustomBaseUrl, 256,
                text => { options.CustomBaseUrl = text.Trim(); Tweak.SaveCurrent(); });
            Input(Tweak.Text("Settings.CustomModel"), null, options.Model, 128,
                text => { options.Model = text.Trim(); Tweak.SaveCurrent(); });
        }

        if (!provider.NeedsKey)
        {
            Note(Tweak.Text($"Settings.GetKey.{provider.Key}"), Pad, ContentW, Muted);
            Space(8f);
        }
        else
        {
            // The key is never written back into the box: once saved it only says so.
            var keyHelp = Tweak.Text($"Settings.GetKey.{provider.Key}");
            Input(Tweak.Text("Settings.ApiKey"), keyHelp, string.Empty, 256,
                text =>
                {
                    if (text.Trim().Length == 0) return;
                    Tweak.SetApiKey(text.Trim());
                    _remakeModels = true;   // this key may see other models
                    RequestRebuild();
                },
                Tweak.ApiKey.Length > 0 ? Tweak.Text("Settings.ApiKey.Saved") : Tweak.Text("Settings.ApiKey.Placeholder"));

            if (Tweak.ApiKey.Length > 0)
            {
                var clear = new TextButtonNode
                {
                    String   = Tweak.Text("Settings.ApiKey.Clear"),
                    Position = new Vector2(Pad, Y),
                    Size     = new Vector2(180f, 26f),
                };
                clear.OnClick = () => { Tweak.SetApiKey(string.Empty); _remakeModels = true; RequestRebuild(); };
                clear.AttachNode(Host);
                Y += 34f;
            }
        }

        Section(Tweak.Text("Settings.Hidden"));
        Note(string.Format(Tweak.Text("Settings.Hidden.Help"), options.Disliked.Count), Pad, ContentW, Muted);
        if (options.Disliked.Count > 0)
        {
            Space(4f);
            var showAgain = new TextButtonNode
            {
                String   = Tweak.Text("Settings.Hidden.Clear"),
                Position = new Vector2(Pad, Y),
                Size     = new Vector2(220f, 26f),
            };
            showAgain.OnClick = () => { Tweak.ClearDisliked(); RequestRebuild(); };
            showAgain.AttachNode(Host);
            Y += 34f;
        }

        Section(Tweak.Text("Settings.Privacy.Title"));
        Note(Tweak.Text("Settings.Privacy"), Pad, ContentW, Muted);
    }

    private void Caption(string text, Vector2 position)
        => new TextNode
        {
            String    = text,
            Position  = position,
            Size      = new Vector2(200f, 20f),
            TextColor = White,
            FontSize  = 14,
        }.AttachNode(this);

    /// <summary>A title, a one-line text box under it and an optional explanation; <paramref name="set"/> runs when typing ends.</summary>
    private TextInputNode Input(string label, string? help, string value, int max, Action<string> set, string? placeholder = null)
    {
        new TextNode
        {
            String    = label,
            Position  = new Vector2(Pad, Y),
            Size      = new Vector2(ContentW, 20f),
            TextColor = White,
            FontSize  = 14,
        }.AttachNode(Host);
        Y += 22f;

        var input = new TextInputNode
        {
            Position      = new Vector2(Pad, Y),
            Size          = new Vector2(ContentW, 28f),
            MaxCharacters = max,
        };
        if (placeholder is not null) input.PlaceholderString = placeholder;
        input.OnInputComplete = text => set(text.ToString());
        input.AttachNode(Host);
        if (value.Length > 0) input.String = value;
        Y += 32f;

        if (!string.IsNullOrEmpty(help))
            Note(help, Pad, ContentW, Muted);
        Y += 8f;
        return input;
    }

    protected override void OnClosed()
    {
        _model = null;
        _modelSlot = null;
        _remakeModels = false;
    }
}
