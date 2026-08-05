using System;
using System.Collections.Generic;
using System.Linq;
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
        private const int TimingGroupId = 300;
        private const int InputGroupId = 400;
        private const int AdvancedGroupId = 500;
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

        private readonly Random _delayRandom = new();

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

        [Menu(
            "Interface",
            "Settings for the owned-items window.",
            InterfaceGroupId)]
        public EmptyNode InterfaceGroup { get; set; } = new();

        [Menu(
            "Pin Window",
            "Docks the window to the top-right corner of the " +
            "currency exchange panel.",
            101,
            InterfaceGroupId)]
        public ToggleNode PinWindow { get; set; } = new(true);

        [Menu(
            "Default Sort Column",
            "The column the item table is sorted by initially.",
            102,
            InterfaceGroupId)]
        public ListNode SortBy { get; set; } = new()
        {
            Value = SortOptions.Value
        };

        [Menu(
            "Default Sort Ascending",
            "Sorts low-to-high instead of high-to-low.",
            103,
            InterfaceGroupId)]
        public ToggleNode SortAscending { get; set; } = new(false);

        [Menu(
            "Restore Mouse Position",
            "Moves the cursor back to where it was once a sequence " +
            "finishes.",
            104,
            InterfaceGroupId)]
        public ToggleNode RestoreMousePosition { get; set; } = new(true);

        [Menu(
            "Show Debug Messages",
            "Displays the debug window and verbose sequence logging.",
            105,
            InterfaceGroupId)]
        public ToggleNode Debug { get; set; } = new(false);

        [Menu(
            "Pricing",
            "Controls how the requested currency amount is calculated.",
            PricingGroupId)]
        public EmptyNode PricingGroup { get; set; } = new();

        [Menu(
            "Listing Price Percent",
            "The percentage of the detected market ratio to request. " +
            "100 lists at the full market ratio.",
            201,
            PricingGroupId)]
        public RangeNode<int> ListingPricePercent { get; set; } =
            new(100, 1, 100);

        [Menu(
            "Use Highest Competing Ratio",
            "Lists at the highest competing trade ratio instead of " +
            "the current market rate. Trades take longer to fill but " +
            "are more profitable.",
            202,
            PricingGroupId)]
        public ToggleNode UseHighestCompetingRatio { get; set; } =
            new(false);

        [Menu(
            "Require Sufficient Stock",
            "Only picks competing ratios whose stock covers the " +
            "amount being sold.",
            203,
            PricingGroupId)]
        public ToggleNode RequireSufficientStock { get; set; } =
            new(true);

        [Menu(
            "Timing",
            "Delays and timeouts for the automated sequences.",
            TimingGroupId)]
        public EmptyNode TimingGroup { get; set; } = new();

        [Menu(
            "Action Delay (ms)",
            "The base delay between automated inputs and state checks. " +
            "Every use is randomized by plus/minus 10 percent " +
            "(100 becomes 90-110).",
            301,
            TimingGroupId)]
        public RangeNode<int> ActionDelayMilliseconds { get; set; } =
            new(100, 0, 1000);

        [Menu(
            "Collect Delay (ms)",
            "The delay between individual item collections. Applies " +
            "with and without InputHumanizer because of trade rate " +
            "limits.",
            302,
            TimingGroupId)]
        public RangeNode<int> CollectDelayMilliseconds { get; set; } =
            new(1000, 250, 5000);

        [Menu(
            "Step Timeout (s)",
            "Skips the current item when one sequence step remains " +
            "active for this long.",
            303,
            TimingGroupId)]
        public RangeNode<int> SequenceTimeoutSeconds { get; set; } =
            new(5, 1, 30);

        [Menu(
            "Input",
            "How mouse and keyboard input is delivered.",
            InputGroupId)]
        public EmptyNode InputGroup { get; set; } = new();

        [Menu(
            "Use InputHumanizer",
            "Routes mouse input through the InputHumanizer plugin " +
            "(humanized delays and mouse paths) when it is loaded.",
            401,
            InputGroupId)]
        public ToggleNode UseInputHumanizer { get; set; } = new(false);

        [Menu(
            "Advanced",
            "Game constants. UI elements are discovered automatically, " +
            "so there are no child indexes to maintain.",
            AdvancedGroupId)]
        public EmptyNode AdvancedGroup { get; set; } = new();

        [Menu(
            "Maximum Concurrent Trades",
            "How many exchange orders the game allows at the same time.",
            501,
            AdvancedGroupId)]
        public RangeNode<int> MaxConcurrentTrades { get; set; } =
            new(10, 1, 20);

        /// <summary>
        /// Returns the base action delay randomized by plus/minus 10 percent,
        /// so a configured value of 100 yields 90-110 milliseconds.
        /// </summary>
        public int GetRandomizedActionDelay()
        {
            var baseDelay = ActionDelayMilliseconds.Value;

            if (baseDelay <= 0)
                return 0;

            return (int)Math.Round(
                baseDelay * (0.9 + _delayRandom.NextDouble() * 0.2));
        }

        [Menu(
            "Excluded Currencies",
            "Currencies that should not be displayed or sold.",
            ExcludedCurrenciesGroupId)]
        public EmptyNode ExcludedCurrenciesGroup { get; set; } = new();

        [JsonIgnore]
        [Menu(
            "Currency List",
            "Add, edit, or remove excluded currencies.",
            601,
            ExcludedCurrenciesGroupId)]
        public CustomNode ExcludedCurrenciesEditor { get; }

        /// <summary>
        /// JSON-serialized excluded-currency list; the value ExileAPI actually
        /// persists to the settings file. <see cref="GetExcludedCurrencies"/>
        /// maintains a deserialized cache on top of it.
        /// </summary>
        [HideInReflection]
        public TextNode ExcludedCurrenciesJson { get; set; } =
            new(DefaultExcludedCurrenciesJson);

        /// <summary>Returns whether the given currency is on the excluded list.</summary>
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
                ImGui.TextDisabled("No currencies are excluded.");
                ImGui.PopID();
                return;
            }

            for (var index = 0; index < excludedCurrencies.Count; index++)
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

            var excludedCurrencies = GetExcludedCurrencies();

            var alreadyExists = excludedCurrencies.Any(existing =>
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

        /// <summary>
        /// Returns the cached excluded-currency list, re-deserializing it
        /// whenever ExileAPI restores a different JSON value from the settings
        /// file than the one the cache was built from.
        /// </summary>
        private List<string> GetExcludedCurrencies()
        {
            ExcludedCurrenciesJson ??=
                new TextNode(DefaultExcludedCurrenciesJson);

            var serialized = ExcludedCurrenciesJson.Value;

            if (string.IsNullOrWhiteSpace(serialized))
                serialized = DefaultExcludedCurrenciesJson;

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

        /// <summary>
        /// Serializes the excluded-currency list into
        /// <see cref="ExcludedCurrenciesJson"/>. Assigning the node's value
        /// fires its change event, which makes ExileAPI persist the settings.
        /// </summary>
        private void SaveExcludedCurrencies()
        {
            _excludedCurrencies ??= [];

            var serialized =
                JsonConvert.SerializeObject(_excludedCurrencies);

            _loadedExcludedCurrenciesJson = serialized;

            ExcludedCurrenciesJson.Value = serialized;
        }
    }

    /// <summary>Column names selectable as the item table's default sort.</summary>
    public static class SortOptions
    {
        public const string Name = "Name";
        public const string Value = "Value";
        public const string Owned = "Owned";
    }
}
