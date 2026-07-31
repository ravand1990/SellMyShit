using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml;
using ExileCore;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;

namespace SellMyShit
{
    public class Settings : ISettings
    {
        private const int InterfaceGroupId = 100;
        private const int PricingGroupId = 200;
        private const int SequenceGroupId = 300;
        private const int InputTimingGroupId = 400;
        private const int CompatibilityGroupId = 500;
        private const int ExcludedCurrenciesGroupId = 600;

        private static readonly List<string> DefaultExcludedCurrencies =
        [
            "Mirror of Kalandra",
    "Hinekora's Lock",
    "Divine Orb",
    "Chaos Orb"
        ];

        private static readonly string DefaultExcludedCurrenciesJson =
            JsonConvert.SerializeObject(DefaultExcludedCurrencies);

        private string _newExcludedCurrency = string.Empty;

        private List<string> _excludedCurrencies =
            new(DefaultExcludedCurrencies);

        private string _loadedExcludedCurrenciesJson;


        public Settings()
        {
            SortBy.SetListValues(
                new List<string>
                {
                    SortOptions.Name,
                    SortOptions.Value,
                    SortOptions.Owned
                });


            ExcludedCurrenciesEditor = new CustomNode
            {
                DrawDelegate = DrawExcludedCurrenciesEditor
            };
        }

        public ToggleNode Enable { get; set; } = new(true);

        // ─────────────────────────────────────────────
        // Interface
        // ─────────────────────────────────────────────

        [Menu(
            "Interface",
            "Settings for the owned-currency window.",
            InterfaceGroupId)]
        public EmptyNode InterfaceGroup { get; set; } = new();

        [Menu(
            "Window width",
            "Width of the scrollable owned-currency list.",
            101,
            InterfaceGroupId)]
        public RangeNode<int> WindowWidth { get; set; } =
            new(520, 300, 1200);

        [Menu(
            "Window height",
            "Height of the scrollable owned-currency list.",
            102,
            InterfaceGroupId)]
        public RangeNode<int> WindowHeight { get; set; } =
            new(340, 150, 1000);


        [Menu(
            "Default Sort by",
            "The currently selected sort column.",
            103,
            InterfaceGroupId)]
        public ListNode SortBy { get; set; } = new()
        {
            Value = SortOptions.Value
        };

        [Menu(
               "Restore mouse position?",
               "If the sell sequence ends, the mouse jumps back to where you initiated the sequence.",
               104,
               InterfaceGroupId)]
        public ToggleNode RestoreMousePosition { get; set; } = new(true);

        [Menu(
            "Default Sort ascending?",
            "Sort low-to-high instead of high-to-low.",
            105,
            InterfaceGroupId)]
        public ToggleNode SortAscending { get; set; } = new(false);

        [Menu(
            "Show Debug Messages?",
            null,
            106,
            InterfaceGroupId)]
        public ToggleNode Debug { get; set; } = new(false);

        // ─────────────────────────────────────────────
        // Pricing
        // ─────────────────────────────────────────────

        [Menu(
            "Pricing",
            "Controls how the requested currency amount is calculated.",
            PricingGroupId)]
        public EmptyNode PricingGroup { get; set; } = new();

        [Menu(
            "Listing price percent",
            "Percentage of the detected market ratio to request. " +
            "95 means the listing is priced at 95% of the market ratio.",
            201,
            PricingGroupId)]
        public RangeNode<int> ListingPricePercent { get; set; } =
            new(95, 1, 100);

        // ─────────────────────────────────────────────
        // Sell sequence
        // ─────────────────────────────────────────────

        [Menu(
            "Sell sequence",
            "Timeouts and delays used by the automated sell process.",
            SequenceGroupId)]
        public EmptyNode SequenceGroup { get; set; } = new();

        [Menu(
            "Step timeout (seconds)",
            "Stops the sequence when one state remains active for too long.",
            301,
            SequenceGroupId)]
        public RangeNode<int> SequenceTimeoutSeconds { get; set; } =
            new(5, 1, 30);

        [Menu(
            "Search result delay (ms)",
            "Time to wait after entering a currency name before clicking its result.",
            302,
            SequenceGroupId)]
        public RangeNode<int> CurrencySearchDelayMilliseconds { get; set; } =
            new(100, 0, 5000);

        [Menu(
            "Market ratio delay (ms)",
            "Time to wait after entering amounts before checking the updated ratio.",
            303,
            SequenceGroupId)]
        public RangeNode<int> MarketRatioDelayMilliseconds { get; set; } =
            new(100, 0, 5000);

        // ─────────────────────────────────────────────
        // Input timing
        // ─────────────────────────────────────────────

        [Menu(
            "Input timing",
            "Mouse, keyboard, focus, and overlay timing settings.",
            InputTimingGroupId)]
        public EmptyNode InputTimingGroup { get; set; } = new();

        [Menu(
            "Text overlay hide time (ms)",
            "How long the plugin window remains hidden while typing.",
            401,
            InputTimingGroupId)]
        public RangeNode<int> TextOverlayHideMilliseconds { get; set; } =
            new(300, 0, 3000);

        [Menu(
            "Click overlay hide time (ms)",
            "How long the plugin window remains hidden while clicking.",
            402,
            InputTimingGroupId)]
        public RangeNode<int> ClickOverlayHideMilliseconds { get; set; } =
            new(300, 0, 3000);

        [Menu(
            "Text pre-focus delay (ms)",
            "Delay before focusing Path of Exile for keyboard input.",
            403,
            InputTimingGroupId)]
        public RangeNode<int> TextPreFocusDelayMilliseconds { get; set; } =
            new(150, 0, 1000);

        [Menu(
            "Text post-focus delay (ms)",
            "Delay after focusing Path of Exile before typing.",
            404,
            InputTimingGroupId)]
        public RangeNode<int> TextPostFocusDelayMilliseconds { get; set; } =
            new(150, 0, 1000);

        [Menu(
            "Text release delay (ms)",
            "Delay before releasing the shared input lock after typing.",
            405,
            InputTimingGroupId)]
        public RangeNode<int> TextReleaseDelayMilliseconds { get; set; } =
            new(150, 0, 1000);

        [Menu(
            "Click pre-focus delay (ms)",
            "Delay before focusing Path of Exile for a mouse click.",
            406,
            InputTimingGroupId)]
        public RangeNode<int> ClickPreFocusDelayMilliseconds { get; set; } =
            new(50, 0, 1000);

        [Menu(
            "Click post-focus delay (ms)",
            "Delay after focusing Path of Exile before moving the cursor.",
            407,
            InputTimingGroupId)]
        public RangeNode<int> ClickPostFocusDelayMilliseconds { get; set; } =
            new(50, 0, 1000);

        [Menu(
            "Mouse settle delay (ms)",
            "Delay after moving the cursor before pressing the mouse button.",
            408,
            InputTimingGroupId)]
        public RangeNode<int> MouseSettleDelayMilliseconds { get; set; } =
            new(50, 0, 1000);

        [Menu(
            "Mouse button hold time (ms)",
            "Time between the left mouse button down and up events.",
            409,
            InputTimingGroupId)]
        public RangeNode<int> MouseButtonHoldMilliseconds { get; set; } =
            new(50, 1, 500);

        [Menu(
            "Click release delay (ms)",
            "Delay before releasing the shared input lock after clicking.",
            410,
            InputTimingGroupId)]
        public RangeNode<int> ClickReleaseDelayMilliseconds { get; set; } =
            new(50, 0, 1000);

        // ─────────────────────────────────────────────
        // Compatibility
        // ─────────────────────────────────────────────

        [Menu(
            "Advanced compatibility",
            "Internal UI indexes. Change these only when a game update changes the exchange UI.",
            CompatibilityGroupId)]
        public EmptyNode CompatibilityGroup { get; set; } = new();

        [Menu(
            "I Have button child index",
            "Child index of the I Have currency-selector button.",
            501,
            CompatibilityGroupId)]
        public RangeNode<int> IHaveButtonChildIndex { get; set; } =
            new(10, 0, 50);

        [Menu(
            "Currency search input child index",
            "Child index of the currency-picker search input.",
            502,
            CompatibilityGroupId)]
        public RangeNode<int> CurrencyPickerSearchInputChildIndex { get; set; } =
            new(4, 0, 50);

        [Menu(
            "Sell button child index",
            "Child index of the final sell button.",
            503,
            CompatibilityGroupId)]
        public RangeNode<int> SellButtonChildIndex { get; set; } =
            new(16, 0, 50);



        // ─────────────────────────────────────────────
        // Exclude Currency
        // ─────────────────────────────────────────────


        [Menu(
            "Excluded currencies",
            "Currencies that should not be displayed or sold.",
            ExcludedCurrenciesGroupId)]
        public EmptyNode ExcludedCurrenciesGroup { get; set; } = new();

        [Newtonsoft.Json.JsonIgnore]
        [Menu(
            "Currency list",
            "Add, edit, or remove excluded currencies.",
            601,
            ExcludedCurrenciesGroupId)]
        public CustomNode ExcludedCurrenciesEditor { get; }

        [HideInReflection]
        public TextNode ExcludedCurrenciesJson { get; set; } =
            new(DefaultExcludedCurrenciesJson);

        public bool IsCurrencyExcluded(string currencyName)
        {
            if (string.IsNullOrWhiteSpace(currencyName))
                return false;

            return GetExcludedCurrencies().Any(excluded =>
                string.Equals(
                    excluded,
                    currencyName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void DrawExcludedCurrenciesEditor()
        {
            var excludedCurrencies = GetExcludedCurrencies();

            ImGui.PushID("ExcludedCurrenciesEditor");

            ImGui.TextDisabled(
                "Enter the exact name of a currency to exclude.");

            ImGui.SetNextItemWidth(260);

            var submitted = ImGui.InputTextWithHint(
                "##NewExcludedCurrency",
                "Currency name",
                ref _newExcludedCurrency,
                256,
                ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.SameLine();

            if (ImGui.Button("Add") || submitted)
                AddExcludedCurrency();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (excludedCurrencies.Count == 0)
            {
                ImGui.TextDisabled("No currencies excluded.");
                ImGui.PopID();
                return;
            }

            for (var index = 0;
                 index < excludedCurrencies.Count;
                 index++)
            {
                ImGui.PushID(index);

                var value = excludedCurrencies[index];

                ImGui.SetNextItemWidth(260);

                if (ImGui.InputText(
                        "##ExcludedCurrencyName",
                        ref value,
                        256))
                {
                    excludedCurrencies[index] = value;
                    SaveExcludedCurrencies();
                }

                ImGui.SameLine();

                if (ImGui.SmallButton("Delete"))
                {
                    excludedCurrencies.RemoveAt(index);
                    SaveExcludedCurrencies();

                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }

            ImGui.PopID();
        }

        private void AddExcludedCurrency()
        {
            var value = _newExcludedCurrency?.Trim();

            if (string.IsNullOrWhiteSpace(value))
                return;

            var excludedCurrencies =
                GetExcludedCurrencies();

            var alreadyExists =
                excludedCurrencies.Any(existing =>
                    string.Equals(
                        existing?.Trim(),
                        value,
                        StringComparison.OrdinalIgnoreCase));

            if (!alreadyExists)
            {
                excludedCurrencies.Add(value);
                SaveExcludedCurrencies();
            }

            _newExcludedCurrency = string.Empty;
        }

        private List<string> GetExcludedCurrencies()
        {
            ExcludedCurrenciesJson ??=
                new TextNode(DefaultExcludedCurrenciesJson);

            var serialized = ExcludedCurrenciesJson.Value;

            if (string.IsNullOrWhiteSpace(serialized))
                serialized = DefaultExcludedCurrenciesJson;

            /*
             * Reload the cache when ExileAPI restores a different
             * JSON value from the settings file.
             */
            if (_excludedCurrencies != null &&
                string.Equals(
                    _loadedExcludedCurrenciesJson,
                    serialized,
                    StringComparison.Ordinal))
            {
                return _excludedCurrencies;
            }

            try
            {
                _excludedCurrencies =
                    JsonConvert.DeserializeObject<List<string>>(serialized)
                    ?? new List<string>(DefaultExcludedCurrencies);
            }
            catch
            {
                _excludedCurrencies =
                    new List<string>(DefaultExcludedCurrencies);
            }

            _excludedCurrencies = _excludedCurrencies
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _loadedExcludedCurrenciesJson = serialized;

            return _excludedCurrencies;
        }




        private void SaveExcludedCurrencies()
        {
            _excludedCurrencies ??= [];

            var serialized =
                JsonConvert.SerializeObject(
                    _excludedCurrencies);

            _loadedExcludedCurrenciesJson =
                serialized;

            /*
             * Assigning TextNode.Value fires OnValueChanged,
             * causing ExileAPI to persist the settings.
             */
            ExcludedCurrenciesJson.Value =
                serialized;
        }

    }

    public static class SortOptions
    {
        public const string Name = "Name";
        public const string Value = "Value";
        public const string Owned = "Owned";
    }

}