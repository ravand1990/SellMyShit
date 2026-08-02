using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements.Village;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Microsoft.VisualBasic.Devices;
using Newtonsoft.Json;
using SharpDX;
using NumVector2 = System.Numerics.Vector2;

namespace SellMyShit
{
    public class Core : BaseSettingsPlugin<Settings>
    {
        private const string WantedCurrencyName = "Chaos Orb";
        private const int SortByName = 0;
        private const int SortByValue = 1;
        private const int SortByOwned = 2;

        private Func<BaseItemType, double> _getNinjaBaseItemTypeValue;

        private string _filterText = string.Empty;

        private int _inputInProgress;
        private DateTime _hideOverlayUntilUtc = DateTime.MinValue;

        private SellSequenceStep _sellSequenceStep = SellSequenceStep.Idle;
        private DateTime _sellSequenceStepStartedUtc;
        private CurrencyExchangeCurrencyPickerCurrencyOption _pendingSellItem;
        private CurrencyExchangeCurrencyPickerCurrencyOption _pendingWantedItem;
        private MarketRatio _pendingMarketRatio;

        private string _pendingSellItemName = string.Empty;
        private int _pendingSellOwnedAmount;

        private static readonly TimeSpan CurrencyItemsRefreshInterval =
            TimeSpan.FromMilliseconds(500);

        private readonly Dictionary<string, double> _ninjaUnitValueCache =
            new(StringComparer.OrdinalIgnoreCase);

        private List<CurrencyExchangeCurrencyPickerCurrencyOption>
            _currencyExchangeItemsCache = [];

        private bool _currencyExchangeItemsCacheInitialized;
        private DateTime _nextCurrencyItemsRefreshUtc = DateTime.MinValue;
        private int _currencyItemsVersion;

        private readonly List<CurrencyExchangeCurrencyPickerCurrencyOption>
            _displayItemsCache = [];

        private int _displayItemsSourceVersion = -1;
        private string _displayItemsFilter = string.Empty;
        private int _displayItemsSortColumn = -1;
        private bool _displayItemsSortAscending;
        private bool _isAltKeyDown = false;
        private Point storedMousePosition;


        private List<string> debugMessages = new List<string>();


        public override bool Initialise() => true;

        public override void Render()
        {
            if (!Settings.Enable.Value)
                return;

            try
            {
                var currencyExchangePanel = GetCurrencyExchangePanel();
                if (Settings.Debug)
                {

                    DebugImGuiWindow();
                    if (currencyExchangePanel.IsVisible)
                    {
                        var currencyPicker = currencyExchangePanel.CurrencyPicker;

                        if (currencyPicker.IsVisible)
                        {
                            var searchInputChildIndex =
                                        Settings
                                            .CurrencyPickerSearchInputChildIndex
                                            .Value;
                            var searchInput = currencyPicker.GetChildAtIndex(searchInputChildIndex);
                            var searchInputRect = searchInput.GetClientRect();
                            Graphics.DrawFrame(searchInputRect.BottomLeft, searchInput.GetClientRect().TopRight, Color.Red, 2);
                            Graphics.DrawCircleFilled(searchInputRect.Center.ToVector2Num(), 5, Color.Red, 5);
                        }

                        if (!currencyPicker.IsVisible)
                        {
                            if (storedMousePosition.X > 0 && storedMousePosition.Y > 0) Graphics.DrawCircleFilled(storedMousePosition.ToVector2(), 5, Color.Red, 5);

                            var iHaveButtonChildIndex = Settings.IHaveButtonChildIndex.Value;
                            var iHaveButton = currencyExchangePanel.GetChildAtIndex(iHaveButtonChildIndex);
                            var iHaveButtonRect = iHaveButton.GetClientRect();

                            Graphics.DrawFrame(iHaveButtonRect.BottomLeft, iHaveButtonRect.TopRight, Color.Red, 2);
                            Graphics.DrawCircleFilled(iHaveButtonRect.Center.ToVector2Num(), 5, Color.Red, 5);

                            var iWantButtonChildIndex = Settings.IWantButtonChildIndex.Value;
                            var iWantButton = currencyExchangePanel.GetChildAtIndex(iWantButtonChildIndex);
                            var iWantButtonRect = iWantButton.GetClientRect();

                            Graphics.DrawFrame(iWantButtonRect.BottomLeft, iWantButtonRect.TopRight, Color.Red, 2);
                            Graphics.DrawCircleFilled(iWantButtonRect.Center.ToVector2Num(), 5, Color.Red, 5);

                            var offeredItemCountInput = currencyExchangePanel.OfferedItemCountInput;
                            var offeredItemCountInputRect = offeredItemCountInput.GetClientRect();

                            Graphics.DrawFrame(offeredItemCountInputRect.BottomLeft, offeredItemCountInputRect.TopRight, Color.Red, 2);
                            Graphics.DrawCircleFilled(offeredItemCountInputRect.Center.ToVector2Num(), 5, Color.Red, 5);

                            var wantedItemCountInput = currencyExchangePanel.WantedItemCountInput;
                            var wantedItemCountInputRect = wantedItemCountInput.GetClientRect();

                            Graphics.DrawFrame(wantedItemCountInputRect.BottomLeft, wantedItemCountInputRect.TopRight, Color.Red, 2);
                            Graphics.DrawCircleFilled(wantedItemCountInputRect.Center.ToVector2Num(), 5, Color.Red, 5);

                            var sellButtonChildIndex = Settings.SellButtonChildIndex.Value;
                            var sellButton = currencyExchangePanel.GetChildAtIndex(sellButtonChildIndex);
                            var sellButtonRect = sellButton.GetClientRect();

                            Graphics.DrawFrame(sellButtonRect.BottomLeft, sellButtonRect.TopRight, Color.Red, 2);
                            Graphics.DrawCircleFilled(sellButtonRect.Center.ToVector2Num(), 5, Color.Red, 5);


                        }
                    }
                }

                if (currencyExchangePanel?.IsVisible != true)
                    return;

                ProcessSellSequence(currencyExchangePanel);

                var ownedItems =
                    GetCurrencyExchangeItems(currencyExchangePanel);

                if (SellSequenceStep.Idle == _sellSequenceStep) DrawOwnedItemsUi(ownedItems);
            }
            catch (Exception ex)
            {
                LogError($"SellMyShit error: {ex}");
                StopSellSequence();
            }
        }

        private CurrencyExchangePanel GetCurrencyExchangePanel()
        {

            return GameController?
                .IngameState?
                .IngameUi?
                .CurrencyExchangePanel;
        }

        private List<CurrencyExchangeCurrencyPickerCurrencyOption>
            GetCurrencyExchangeItems(
                CurrencyExchangePanel currencyExchangePanel)
        {
            if (currencyExchangePanel == null)
                return [];

            var now = DateTime.UtcNow;

            if (_currencyExchangeItemsCacheInitialized &&
                now < _nextCurrencyItemsRefreshUtc)
            {
                return _currencyExchangeItemsCache;
            }

            _nextCurrencyItemsRefreshUtc =
                now.Add(CurrencyItemsRefreshInterval);

            try
            {
                var options =
                    currencyExchangePanel.CurrencyPicker?.Options;

                if (options == null)
                    return _currencyExchangeItemsCache;

                _currencyExchangeItemsCache = options
                    .Where(item =>
                        item?.Children != null &&
                        item.Children.Count > 0 &&
                        item.Owned > 0)
                    .Where(item =>
                        !Settings.IsCurrencyExcluded(
                            GetItemName(item)))
                    .Distinct()
                    .ToList();

                _currencyExchangeItemsCacheInitialized = true;
                _currencyItemsVersion++;

                return _currencyExchangeItemsCache;
            }
            catch (Exception ex)
            {
                LogError(
                    $"SellMyShit error while extracting items: {ex}");

                return _currencyExchangeItemsCache;
            }
        }

        private IReadOnlyList<CurrencyExchangeCurrencyPickerCurrencyOption>
            GetDisplayItems(
                IReadOnlyList<
                    CurrencyExchangeCurrencyPickerCurrencyOption> ownedItems)
        {
            var sortColumn =
                GetConfiguredSortColumn();

            var sortAscending =
                Settings.SortAscending.Value;

            var filter =
                _filterText?.Trim() ?? string.Empty;

            var cacheIsCurrent =
                _displayItemsSourceVersion == _currencyItemsVersion &&
                _displayItemsSortColumn == sortColumn &&
                _displayItemsSortAscending == sortAscending &&
                string.Equals(
                    _displayItemsFilter,
                    filter,
                    StringComparison.Ordinal);

            if (cacheIsCurrent)
                return _displayItemsCache;

            _displayItemsCache.Clear();

            foreach (var item in ownedItems)
            {
                if (item == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(filter) &&
                    GetItemSearchText(item).IndexOf(
                        filter,
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                _displayItemsCache.Add(item);
            }

            _displayItemsCache.Sort(
                (left, right) =>
                    CompareItems(
                        left,
                        right,
                        sortColumn,
                        sortAscending));

            _displayItemsSourceVersion =
                _currencyItemsVersion;

            _displayItemsFilter =
                filter;

            _displayItemsSortColumn =
                sortColumn;

            _displayItemsSortAscending =
                sortAscending;

            return _displayItemsCache;
        }

        private void DrawOwnedItemsUi(
            List<CurrencyExchangeCurrencyPickerCurrencyOption> ownedItems)
        {
            ownedItems ??= [];

            if (DateTime.UtcNow < _hideOverlayUntilUtc)
                return;

            if (!ImGui.Begin(
                    "Owned Currency Items",
                    ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                return;
            }

            DrawFilterControls();

            var filteredItems =
                GetDisplayItems(ownedItems);

            ImGui.Text(
                $"Owned Items: {filteredItems.Count} / {ownedItems.Count}");

            ImGui.Separator();

            var childSize = new NumVector2(
                Settings.WindowWidth.Value,
                Settings.WindowHeight.Value);

            if (ImGui.BeginChild(
                    "CurrencyOwnedItemsChild",
                    childSize,
                    ImGuiChildFlags.None,
                    ImGuiWindowFlags.None))
            {
                DrawOwnedItemsTable(filteredItems);
            }

            ImGui.EndChild();
            ImGui.End();
        }

        private void DrawFilterControls()
        {
            ImGui.InputText(
                "Filter",
                ref _filterText,
                256);
        }

        private void HandleSortHeaderClick(int column)
        {
            var configuredColumn =
                GetConfiguredSortColumn();

            if (configuredColumn == column)
            {
                Settings.SortAscending.Value =
                    !Settings.SortAscending.Value;

                return;
            }

            Settings.SortBy.Value =
                GetSortOption(column);

            Settings.SortAscending.Value = true;
        }

        private void DrawSortHeader(
            string label,
            int column)
        {
            var isCurrentColumn =
                GetConfiguredSortColumn() == column;

            var indicator = isCurrentColumn
                ? Settings.SortAscending.Value
                    ? " ^"
                    : " v"
                : string.Empty;

            var buttonLabel =
                $"{label}{indicator}##SortHeader{column}";

            if (ImGui.Button(
                    buttonLabel,
                    new NumVector2(
                        ImGui.GetContentRegionAvail().X,
                        0)))
            {
                HandleSortHeaderClick(column);
            }
        }

        private unsafe void DrawOwnedItemsTable(
    IReadOnlyList<CurrencyExchangeCurrencyPickerCurrencyOption> items)
        {
            const ImGuiTableFlags tableFlags =
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.Sortable |
                ImGuiTableFlags.SizingFixedFit |
                ImGuiTableFlags.NoSavedSettings;

            if (!ImGui.BeginTable(
                    "CurrencyOwnedItemsTable",
                    4,
                    tableFlags))
            {
                return;
            }


            var defaultSortByColum = Settings.SortBy.Value;


            var nameTableFlags = ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.PreferSortDescending;
            if (defaultSortByColum == "Name") nameTableFlags |= ImGuiTableColumnFlags.DefaultSort;
            ImGui.TableSetupColumn(
                "Name",
                ImGuiTableColumnFlags.WidthStretch |
                ImGuiTableColumnFlags.PreferSortAscending);

            var valueTableFlags = ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.PreferSortDescending;
            if (defaultSortByColum == "Value") valueTableFlags |= ImGuiTableColumnFlags.DefaultSort;
            ImGui.TableSetupColumn(
                "Value",
                ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.DefaultSort |
                ImGuiTableColumnFlags.PreferSortDescending);

            var ownedTableFlags = ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.PreferSortDescending;
            if (defaultSortByColum == "Owned") ownedTableFlags |= ImGuiTableColumnFlags.DefaultSort;
            ImGui.TableSetupColumn(
                "Owned", ownedTableFlags
);

            ImGui.TableSetupColumn(
                "Action",
                ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.NoSort);

            // ImGui erzeugt die klickbaren Header und Sortierpfeile.
            ImGui.TableHeadersRow();

            ApplyImGuiTableSorting();

            var clipper = new ImGuiListClipperPtr(
                ImGuiNative.ImGuiListClipper_ImGuiListClipper());

            try
            {
                clipper.Begin(items.Count);

                while (clipper.Step())
                {
                    for (var index = clipper.DisplayStart;
                         index < clipper.DisplayEnd;
                         index++)
                    {
                        DrawOwnedItemRow(
                            items[index],
                            index);
                    }
                }
            }
            finally
            {
                clipper.Destroy();
            }

            ImGui.EndTable();
        }

        private unsafe void ApplyImGuiTableSorting()
        {
            var sortSpecs =
                ImGui.TableGetSortSpecs();

            if (sortSpecs.NativePtr == null ||
                !sortSpecs.SpecsDirty ||
                sortSpecs.SpecsCount <= 0)
            {
                return;
            }

            var columnSortSpec =
                sortSpecs.Specs;

            Settings.SortBy.Value =
                GetSortOption(columnSortSpec.ColumnIndex);

            Settings.SortAscending.Value =
                columnSortSpec.SortDirection ==
                ImGuiSortDirection.Ascending;

            _displayItemsSourceVersion = -1;

            sortSpecs.SpecsDirty = false;
        }
        private static float GetSortHeaderWidth(string label)
        {
            var textWidth =
                ImGui.CalcTextSize($"{label} v").X;

            var style =
                ImGui.GetStyle();

            return textWidth +
                   style.FramePadding.X * 2 +
                   style.CellPadding.X * 2;
        }

        private void DrawOwnedItemRow(
            CurrencyExchangeCurrencyPickerCurrencyOption item,
            int index)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            ImGui.TextUnformatted(
                GetItemName(item));

            ImGui.TableNextColumn();

            var totalValue =
                GetTotalNinjaValue(item);

            ImGui.TextUnformatted(
                totalValue.ToString(
                    CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();

            ImGui.TextUnformatted(
                item.Owned.ToString(
                    CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();

            if (ImGui.Button($"Sell##{index}"))
                StartSellSequence(item);
        }

        private int CompareItems(
            CurrencyExchangeCurrencyPickerCurrencyOption left,
            CurrencyExchangeCurrencyPickerCurrencyOption right,
            int column,
            bool ascending)
        {
            if (left == null && right == null)
                return 0;

            if (left == null)
                return ascending ? -1 : 1;

            if (right == null)
                return ascending ? 1 : -1;

            switch (column)
            {
                case SortByValue:
                    {
                        var leftValue =
                            GetTotalNinjaValue(left);

                        var rightValue =
                            GetTotalNinjaValue(right);

                        return ascending
                            ? leftValue.CompareTo(rightValue)
                            : rightValue.CompareTo(leftValue);
                    }

                case SortByOwned:
                    return ascending
                        ? left.Owned.CompareTo(right.Owned)
                        : right.Owned.CompareTo(left.Owned);

                case SortByName:
                default:
                    {
                        var leftName =
                            GetItemName(left);

                        var rightName =
                            GetItemName(right);

                        return ascending
                            ? string.Compare(
                                leftName,
                                rightName,
                                StringComparison.OrdinalIgnoreCase)
                            : string.Compare(
                                rightName,
                                leftName,
                                StringComparison.OrdinalIgnoreCase);
                    }
            }
        }

        private int GetConfiguredSortColumn()
        {
            return Settings.SortBy.Value switch
            {
                SortOptions.Name => SortByName,
                SortOptions.Owned => SortByOwned,
                _ => SortByValue
            };
        }

        private static string GetSortOption(int column)
        {
            return column switch
            {
                SortByName => SortOptions.Name,
                SortByOwned => SortOptions.Owned,
                _ => SortOptions.Value
            };
        }

        private void ProcessSellSequence(
            CurrencyExchangePanel currencyExchangePanel)
        {
            if (_sellSequenceStep == SellSequenceStep.Idle)
                return;

            if (currencyExchangePanel == null ||
                !currencyExchangePanel.IsVisible)
            {
                LogError(
                    "Currency exchange panel is no longer visible.");

                StopSellSequence();
                return;
            }

            if (_pendingSellItem == null)
            {
                LogError("No pending sell item.");
                StopSellSequence();
                return;
            }

            if (TimeInCurrentStep() >
                TimeSpan.FromSeconds(
                    Settings.SequenceTimeoutSeconds.Value))
            {
                LogError(
                    $"Sell sequence timed out at step " +
                    $"{_sellSequenceStep}.");

                StopSellSequence();
                return;
            }

            var itemName = _pendingSellItemName;
            var ownedAmount = _pendingSellOwnedAmount;

            var currencyPicker =
                currencyExchangePanel.CurrencyPicker;

            var isCurrencyPickerVisible =
                currencyPicker?.IsVisible == true;

            var offeredItemCountInput =
                currencyExchangePanel.OfferedItemCountInput;

            var wantedItemCountInput =
                currencyExchangePanel.WantedItemCountInput;

            switch (_sellSequenceStep)
            {
                case SellSequenceStep.Start:
                    storedMousePosition = MouseInput.Mouse.GetCursorPosition();
                    debugMessages.Add($"Stored Mouse Position {storedMousePosition}");
                    {
                        SetSellSequenceStep(
                            isCurrencyPickerVisible
                                ? SellSequenceStep
                                    .ClickCurrencyPickerOfferedSearchInput
                                : SellSequenceStep.ClickIHave);

                        break;
                    }

                case SellSequenceStep.ClickIHave:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForOfferedCurrencyPicker);

                            break;
                        }

                        var iHaveButtonChildIndex =
                            Settings.IHaveButtonChildIndex.Value;

                        if (currencyExchangePanel.Children == null ||
                            currencyExchangePanel.Children.Count <=
                            iHaveButtonChildIndex)
                        {
                            LogError("I Have button was not found.");
                            StopSellSequence();
                            break;
                        }

                        var iHaveButtonPosition =
                            currencyExchangePanel
                                .Children[iHaveButtonChildIndex]
                                .GetClientRect()
                                .Center;

                        // Graphics.DrawCircleFilled(iHaveButtonPosition.ToVector2Num(), 20, Color.Red, 5);

                        if (QueueGameClick(iHaveButtonPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForOfferedCurrencyPicker);
                        }

                        break;
                    }

                case SellSequenceStep.WaitForOfferedCurrencyPicker:
                    {
                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .ClickCurrencyPickerOfferedSearchInput);
                        }

                        break;
                    }

                case SellSequenceStep
                    .ClickCurrencyPickerOfferedSearchInput:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIHave);

                            break;
                        }

                        var searchInputChildIndex =
                            Settings
                                .CurrencyPickerSearchInputChildIndex
                                .Value;

                        if (currencyPicker.Children == null ||
                            currencyPicker.Children.Count <=
                            searchInputChildIndex)
                        {
                            LogError(
                                "Currency picker search input " +
                                "was not found.");

                            StopSellSequence();
                            break;
                        }

                        var searchInputPosition =
                            currencyPicker
                                .Children[searchInputChildIndex]
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(searchInputPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .TypeCurrencyPickerOfferedSearchQuery);
                        }

                        break;
                    }

                case SellSequenceStep
                    .TypeCurrencyPickerOfferedSearchQuery:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIHave);

                            break;
                        }

                        if (QueueGameTextReplacement(itemName))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .ValidateOfferedSearchQuery);
                        }

                        break;
                    }

                case SellSequenceStep.ValidateOfferedSearchQuery:
                    {

                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIHave);

                            break;
                        }

                        var searchInputChildIndex =
                               Settings
                                   .CurrencyPickerSearchInputChildIndex
                                   .Value;

                        if (currencyPicker.Children == null ||
                            currencyPicker.Children.Count <=
                            searchInputChildIndex)
                        {
                            LogError(
                                "Currency picker search input " +
                                "was not found.");

                            StopSellSequence();
                            break;
                        }

                        var searchText =
                            currencyPicker
                                .Children[searchInputChildIndex]
                                .Children[0].Text;
                        if (searchText == itemName)
                        {
                            SetSellSequenceStep(SellSequenceStep.WaitForCurrencyPickerOfferedSearchResults);
                        }

                        break;
                    }

                case SellSequenceStep
                    .WaitForCurrencyPickerOfferedSearchResults:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIHave);

                            break;
                        }

                        if (TimeInCurrentStep() <
                            TimeSpan.FromMilliseconds(
                                Settings
                                    .CurrencySearchDelayMilliseconds
                                    .Value))
                        {
                            break;
                        }

                        var itemPositionY = _pendingSellItem.Position.Y;
                        var currencyPickerOptionContainer = GameController.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker.OptionContainer;

                        if (!_pendingSellItem.IsVisible && !_pendingSellItem.IsVisibleLocal)
                        {
                            break;
                        }

                        if (itemPositionY > currencyPickerOptionContainer.Height)
                        {
                            break;
                        }

                        SetSellSequenceStep(
                            SellSequenceStep.ClickOwnedItem);

                        break;
                    }

                case SellSequenceStep.ClickOwnedItem:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickOfferedItemInput);

                            break;
                        }

                        var ownedItemRect =
                            _pendingSellItem.GetClientRect();

                        if (ownedItemRect.Width <= 0 ||
                            ownedItemRect.Height <= 0)
                        {
                            break;
                        }

                        if (QueueGameClick(
                                ownedItemRect.Center))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .WaitForOfferedCurrencyPickerToClose);
                        }

                        break;
                    }

                case SellSequenceStep
                    .WaitForOfferedCurrencyPickerToClose:
                    {
                        if (IsGameInputBusy() ||
                            isCurrencyPickerVisible)
                        {
                            break;
                        }


                        SetSellSequenceStep(SellSequenceStep.CheckIfChaosIsWanted);

                        break;
                    }


                case SellSequenceStep.CheckIfChaosIsWanted:
                    {
                        if (IsGameInputBusy())
                            break;


                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickCurrencyPickerWantedSearchInput);

                            break;
                        }


                        var iWantButtonChildIndex = Settings.IWantButtonChildIndex.Value;

                        if (currencyExchangePanel?.Children == null ||
                            currencyExchangePanel.Children.Count <=
                            iWantButtonChildIndex)
                        {
                            LogError("I Want button not found, aborting...");
                            StopSellSequence();
                            break;
                        }

                        var iWantButton = currencyExchangePanel.GetChildAtIndex(iWantButtonChildIndex);
                        var selectedWantedItemName = iWantButton.GetChildAtIndex(0).Text;

                        if (selectedWantedItemName == WantedCurrencyName)
                        {
                            debugMessages.Add(
                                $"{WantedCurrencyName} is already selected as wanted currency.");


                            if (Settings.ListPriceBasedOnHighestCompetingTrade)
                            {
                                SetSellSequenceStep(SellSequenceStep.ShowMarketRatioTooltip);
                            }
                            else
                            {
                                SetSellSequenceStep(
                                    SellSequenceStep.WaitForMarketRatio);
                            }

                            break;
                        }

                        SetSellSequenceStep(
                            SellSequenceStep.ClickIWant);

                        break;
                    }

                case SellSequenceStep.ClickIWant:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForWantedCurrencyPicker);

                            break;
                        }

                        var iWantButtonChildIndex = Settings.IWantButtonChildIndex.Value;

                        if (currencyExchangePanel.Children == null ||
                            currencyExchangePanel.Children.Count <=
                            iWantButtonChildIndex)
                        {
                            LogError("I Want button was not found.");
                            StopSellSequence();
                            break;
                        }

                        var iWantButtonPosition =
                            currencyExchangePanel
                                .GetChildAtIndex(iWantButtonChildIndex)
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(iWantButtonPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForWantedCurrencyPicker);
                        }

                        break;
                    }

                case SellSequenceStep.WaitForWantedCurrencyPicker:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickCurrencyPickerWantedSearchInput);
                        }

                        break;
                    }

                case SellSequenceStep.ClickCurrencyPickerWantedSearchInput:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIWant);

                            break;
                        }

                        var searchInputChildIndex = Settings.CurrencyPickerSearchInputChildIndex.Value;

                        if (currencyPicker.Children == null ||
                            currencyPicker.Children.Count <=
                            searchInputChildIndex)
                        {
                            LogError(
                                "Wanted currency picker search input was not found.");

                            StopSellSequence();
                            break;
                        }

                        var searchInputPosition =
                            currencyPicker
                                .GetChildAtIndex(searchInputChildIndex)
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(searchInputPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.TypeCurrencyPickerWantedSearchQuery);
                        }

                        break;
                    }

                case SellSequenceStep.TypeCurrencyPickerWantedSearchQuery:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIWant);

                            break;
                        }

                        if (QueueGameTextReplacement(WantedCurrencyName))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ValidatedWantedSearchQuery);
                        }

                        break;
                    }

                case SellSequenceStep.ValidatedWantedSearchQuery:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIWant);

                            break;
                        }

                        var searchInputChildIndex = Settings.CurrencyPickerSearchInputChildIndex.Value;

                        if (currencyPicker.Children == null ||
                            currencyPicker.Children.Count <= searchInputChildIndex)
                        {
                            LogError(
                                "Wanted currency picker search input was not found.");

                            StopSellSequence();
                            break;
                        }

                        var searchInput =
                            currencyPicker.GetChildAtIndex(searchInputChildIndex);

                        if (searchInput.Children == null ||
                            searchInput.Children.Count == 0)
                        {
                            break;
                        }

                        var searchText = searchInput.GetChildAtIndex(0).Text?.Trim();

                        if (string.Equals(
                                searchText,
                                WantedCurrencyName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .WaitForCurrencyPickerWantedSearchResults);
                        }

                        break;
                    }

                case SellSequenceStep.WaitForCurrencyPickerWantedSearchResults:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (!isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickIWant);

                            break;
                        }

                        if (TimeInCurrentStep() <
                            TimeSpan.FromMilliseconds(
                                Settings.CurrencySearchDelayMilliseconds.Value))
                        {
                            break;
                        }


                        _pendingWantedItem = currencyPicker.Options.Select(option => option).Where(option => option.Children.Any(child => child.Text == WantedCurrencyName)).FirstOrDefault();


                        var wantedItemRect = _pendingWantedItem.GetClientRect();

                        if (wantedItemRect.Width <= 0 ||
                            wantedItemRect.Height <= 0)
                        {
                            break;
                        }

                        var optionContainer =
                            currencyPicker.OptionContainer;

                        if (optionContainer != null)
                        {
                            var optionContainerRect =
                                optionContainer.GetClientRect();

                            if (!optionContainerRect.Intersects(wantedItemRect))
                                break;
                        }

                        SetSellSequenceStep(
                            SellSequenceStep.ClickWantedItem);

                        break;
                    }

                case SellSequenceStep.ClickWantedItem:
                    {
                        if (IsGameInputBusy())
                            break;


                        if (_pendingWantedItem == null)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .WaitForCurrencyPickerWantedSearchResults);

                            break;
                        }

                        var wantedItemRect = _pendingWantedItem.GetClientRect();

                        if (wantedItemRect.Width <= 0 ||
                            wantedItemRect.Height <= 0)
                        {
                            break;
                        }

                        if (QueueGameClick(
                                wantedItemRect.Center))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .WaitForWantedCurrencyPickerToClose);
                        }

                        break;
                    }

                case SellSequenceStep.WaitForWantedCurrencyPickerToClose:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                            break;

                        _pendingWantedItem = null;


                        if (Settings.ListPriceBasedOnHighestCompetingTrade)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ShowMarketRatioTooltip);
                        }
                        else
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForMarketRatio);
                        }

                        break;
                    }


                case SellSequenceStep.ShowMarketRatioTooltip:
                    {
                        if (IsGameInputBusy())
                            break;

                        debugMessages.Add(
                            $"Showing market ratio tooltip for " +
                            $"{ownedAmount} of {itemName}...");

                        var marketRatioPanelChildIndex = Settings.MarketRatioPanelIndex.Value;
                        var marketRatioPanel = currencyExchangePanel.GetChildAtIndex(marketRatioPanelChildIndex);



                        if (QueueGameMove(marketRatioPanel.GetClientRect().Center))
                        {
                            {
                                Thread.Sleep(
                                    Settings.MouseSettleDelayMilliseconds.Value);

                                SetSellSequenceStep(
                                    SellSequenceStep.ShowDetailedMarketRatioTooltip);
                                break;
                            }
                        }

                        break;
                    }

                case SellSequenceStep.ShowDetailedMarketRatioTooltip:
                    {
                        if (IsGameInputBusy())
                            break;

                        debugMessages.Add(
                            $"Showing detailed market ratio tooltip for " +
                            $"{ownedAmount} of {itemName}...");

                        var marketRatioPanelChildIndex = Settings.MarketRatioPanelIndex.Value;
                        var marketRatioPanel = currencyExchangePanel.GetChildAtIndex(marketRatioPanelChildIndex);
                        var marketRatioPanelTooltip = marketRatioPanel?.Tooltip;

                        if (marketRatioPanelTooltip == null)
                        {
                            break;
                        }

                        if (!_isAltKeyDown)
                        {
                            KeyboardInput.Keyboard.AltKeyDown();
                            debugMessages.Add($"Pressing Alt to show detailed market ratio info");

                            _isAltKeyDown = true;
                        }

                        SetSellSequenceStep(
                            SellSequenceStep.WaitForDetailedMarketRatioInfo);
                        break;
                    }

                case SellSequenceStep.WaitForDetailedMarketRatioInfo:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (TimeInCurrentStep() <
                            TimeSpan.FromMilliseconds(
                                Settings.MarketRatioDelayMilliseconds.Value))
                        {
                            break;
                        }

                        debugMessages.Add(
                            $"Waiting for detailed market ratio info for " +
                            $"{ownedAmount} of {itemName}...");

                        var marketRatioPanelChildIndex = Settings.MarketRatioPanelIndex.Value;
                        var marketRatioPanel = currencyExchangePanel.GetChildAtIndex(marketRatioPanelChildIndex);
                        var marketRatioPanelTooltip = marketRatioPanel?.Tooltip;


                        if (!marketRatioPanelTooltip.Children.Any(child => child.Text == "Competing Trades"))
                        {
                            break;
                        }

                        _pendingMarketRatio = GetMarketRatioForPendingExchange(currencyExchangePanel);

                        if (_isAltKeyDown)
                        {
                            KeyboardInput.Keyboard.AltKeyUp();
                            _isAltKeyDown = false;
                        }

                        if (_pendingMarketRatio == null || _pendingMarketRatio.MarketGetRate <= 0 || _pendingMarketRatio.MarketGiveRate <= 0)
                        {
                            break;
                        }

                        debugMessages.Add(
                            $"Market ratio for {ownedAmount} of {itemName}: " +
                            $"{_pendingMarketRatio.MarketGetRate}:{_pendingMarketRatio.MarketGiveRate}");

                        SetSellSequenceStep(
                            SellSequenceStep.ClickOfferedItemInput);
                        break;
                    }

                case SellSequenceStep.WaitForMarketRatio:
                    {

                        if (IsGameInputBusy())
                            break;

                        if (TimeInCurrentStep() <
                            TimeSpan.FromMilliseconds(
                                Settings
                                    .MarketRatioDelayMilliseconds
                                    .Value))
                        {
                            break;
                        }

                        var marketRatio = GetMarketRatioForPendingExchange(currencyExchangePanel);

                        var marketRateGive = currencyExchangePanel.MarketRateGive;

                        if (marketRatio.MarketGetRate <= 0 ||
                            marketRatio.MarketGiveRate <= 0)
                        {
                            break;
                        }

                        debugMessages.Add(
                            $"Market ratio for {ownedAmount} of {itemName}: " +
                            $"{marketRatio.MarketGetRate}:{marketRatio.MarketGiveRate}");

                        _pendingMarketRatio = marketRatio;


                        SetSellSequenceStep(
                            SellSequenceStep.ClickOfferedItemInput);

                        break;
                    }

                case SellSequenceStep.ClickOfferedItemInput:
                    {
                        if (IsGameInputBusy())
                            break;


                        if (offeredItemCountInput == null)
                        {
                            LogError(
                                "Offered item count input was not found.");

                            StopSellSequence();
                            break;
                        }

                        var offeredInputPosition = offeredItemCountInput.GetClientRect().Center;

                        if (QueueGameClick(offeredInputPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .TypeOfferedItemValue);
                        }

                        break;
                    }

                case SellSequenceStep.TypeOfferedItemValue:
                    {
                        if (IsGameInputBusy())
                            break;

                        var offeredValue =
                            ownedAmount.ToString(
                                CultureInfo.InvariantCulture);

                        if (QueueGameTextReplacement(offeredValue))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .ClickWantedItemInput);
                        }

                        break;
                    }



                case SellSequenceStep.ClickWantedItemInput:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.Start);

                            break;
                        }

                        if (wantedItemCountInput == null)
                        {
                            LogError(
                                "Wanted item count input was not found.");

                            StopSellSequence();
                            break;
                        }

                        var wantedInputPosition =
                            wantedItemCountInput
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(wantedInputPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .TypeWantedItemValue);
                        }

                        break;
                    }

                case SellSequenceStep.TypeWantedItemValue:
                    {
                        if (IsGameInputBusy())
                            break;


                        debugMessages.Add(
                            $"Calculating wanted value for " +
                            $"{ownedAmount} of {itemName}...");

                        debugMessages.Add(
                            $"Market ratio: " +
                            $"{_pendingMarketRatio.MarketGetRate}:{_pendingMarketRatio.MarketGiveRate}");

                        var wantedValue =
                            CalculateWantedAmount(
                                ownedAmount,
                                _pendingMarketRatio.MarketGetRate,
                                _pendingMarketRatio.MarketGiveRate,
                                Settings.ListingPricePercent.Value);

                        if (wantedValue <= 0)
                        {
                            LogError(
                                $"Invalid market ratio: " +
                                $"{_pendingMarketRatio.MarketGetRate}:{_pendingMarketRatio.MarketGiveRate}");

                            StopSellSequence();
                            break;
                        }

                        debugMessages.Add(
                            $"Pricing {ownedAmount} {itemName}: " +
                            $"market={_pendingMarketRatio.MarketGetRate}:{_pendingMarketRatio.MarketGiveRate}, " +
                            $"wanted={wantedValue}");

                        if (QueueGameTextReplacement(
                                wantedValue.ToString(
                                    CultureInfo.InvariantCulture)))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.BlurInput);
                        }

                        break;
                    }

                case SellSequenceStep.BlurInput:
                    {
                        if (IsGameInputBusy())
                            break;

                        var villageGold = currencyExchangePanel.GetChildAtIndex(15);
                        var villageGoldPosition = villageGold.GetClientRect().Center;

                        if (QueueGameClick(villageGoldPosition))
                        {
                            debugMessages.Add($"Blurring input to lock in ratio... ");

                            SetSellSequenceStep(
                                SellSequenceStep.CheckIfSellButtonIsActive);
                        }

                        break;
                    }

                case SellSequenceStep.CheckIfSellButtonIsActive:
                    {
                        if (IsGameInputBusy())
                            break;


                        var sellButtonChildIndex =
                            Settings.SellButtonChildIndex.Value;

                        if (currencyExchangePanel.Children == null ||
                            currencyExchangePanel.Children.Count <=
                            sellButtonChildIndex)
                        {
                            LogError("Sell button was not found.");
                            StopSellSequence();
                            break;
                        }

                        var sellButton = currencyExchangePanel.GetChildAtIndex(sellButtonChildIndex);

                        if (sellButton.IsActive)
                        {
                            SetSellSequenceStep(SellSequenceStep.ClickSellButton);
                        }

                        break;
                    }

                case SellSequenceStep.ClickSellButton:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.Start);

                            break;
                        }

                        var sellButtonChildIndex =
                            Settings.SellButtonChildIndex.Value;

                        if (currencyExchangePanel.Children == null ||
                            currencyExchangePanel.Children.Count <=
                            sellButtonChildIndex)
                        {
                            LogError("Sell button was not found.");
                            StopSellSequence();
                            break;
                        }

                        var sellButtonPosition =
                            currencyExchangePanel
                                .Children[sellButtonChildIndex]
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(sellButtonPosition))
                        {
                            debugMessages.Add(
                                $"Sell sequence completed for " +
                                $"{ownedAmount} of {itemName}.");

                            SetSellSequenceStep(
                                SellSequenceStep.CheckUnfavorableTrade);
                        }

                        break;
                    }

                case SellSequenceStep.CheckUnfavorableTrade:
                    {
                        //Check unfavorable trade window here...

                        SetSellSequenceStep(
                            SellSequenceStep.ReopenIHave);

                        break;
                    }

                case SellSequenceStep.ReopenIHave:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForOfferedCurrencyPicker);

                            break;
                        }

                        var iHaveButtonChildIndex =
                            Settings.IHaveButtonChildIndex.Value;

                        if (currencyExchangePanel.Children == null ||
                            currencyExchangePanel.Children.Count <=
                            iHaveButtonChildIndex)
                        {
                            LogError("I Have button was not found.");
                            StopSellSequence();
                            break;
                        }

                        var iHaveButtonPosition =
                            currencyExchangePanel
                                .Children[iHaveButtonChildIndex]
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(iHaveButtonPosition))
                            SetSellSequenceStep(SellSequenceStep.End);

                        break;
                    }
                case SellSequenceStep.End:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (Settings.RestoreMousePosition) { QueueGameMove(storedMousePosition); storedMousePosition = new Point(); }
                        StopSellSequence();
                        break;
                    }
            }
        }

        private void StartSellSequence(
            CurrencyExchangeCurrencyPickerCurrencyOption item)
        {
            if (item == null)
                return;



            if (_sellSequenceStep != SellSequenceStep.Idle)
            {
                debugMessages.Add("A sell sequence is already running.");
                return;
            }

            if (!GameController.Window.IsForeground())
            {
                var focused =
                    WinApi.SetForegroundWindow(
                        GameController.Window.Process.MainWindowHandle);
                debugMessages.Add($"Focused PoE: {focused}");
            }

            _pendingSellItem = item;
            _pendingSellItemName = GetItemName(item);
            _pendingSellOwnedAmount = item.Owned;

            debugMessages.Add(
                $"Started sell sequence for {_pendingSellItemName}, " +
                $"owned amount: {_pendingSellOwnedAmount}.");

            SetSellSequenceStep(SellSequenceStep.Start);
        }

        private static string GetItemName(
            CurrencyExchangeCurrencyPickerCurrencyOption item)
        {
            return item?
                       .Children?
                       .FirstOrDefault()?
                       .Text
                   ?? item?.ToString()
                   ?? "Unknown";
        }

        private static string GetItemSearchText(
            CurrencyExchangeCurrencyPickerCurrencyOption item)
        {
            return item?
                       .Children?
                       .FirstOrDefault()?
                       .Text
                   ?? item?.ToString()
                   ?? string.Empty;
        }

        private void SetSellSequenceStep(
            SellSequenceStep step)
        {
            _sellSequenceStep = step;
            _sellSequenceStepStartedUtc = DateTime.UtcNow;

            debugMessages.Add(
                $"Sell sequence step: {step}");
        }

        private void StopSellSequence()
        {
            _sellSequenceStep = SellSequenceStep.Idle;

            _pendingSellItem = null;
            _pendingWantedItem = null;

            _pendingSellItemName = string.Empty;
            _pendingSellOwnedAmount = 0;
            _pendingMarketRatio = null;
            _isAltKeyDown = false;

        }

        private TimeSpan TimeInCurrentStep()
        {
            return DateTime.UtcNow -
                   _sellSequenceStepStartedUtc;
        }

        private bool IsGameInputBusy()
        {
            return Volatile.Read(
                ref _inputInProgress) != 0;
        }

        private bool QueueGameTextReplacement(
            string text)
        {
            if (text == null)
                return false;

            var overlayHideMilliseconds =
                Settings.TextOverlayHideMilliseconds.Value;

            var releaseDelayMilliseconds =
                Settings.TextReleaseDelayMilliseconds.Value;

            var preFocusDelayMilliseconds =
                Settings.TextPreFocusDelayMilliseconds.Value;

            var postFocusDelayMilliseconds =
                Settings.TextPostFocusDelayMilliseconds.Value;

            return QueueGameInput(
                overlayHideMilliseconds,
                releaseDelayMilliseconds,
                "Game keyboard input failed",
                gameWindowHandle =>
                {
                    Thread.Sleep(
                        preFocusDelayMilliseconds);




                    Thread.Sleep(
                        postFocusDelayMilliseconds);

                    var typed =
                        KeyboardInput.Keyboard
                            .ReplaceText(text);

                    debugMessages.Add(
                        $"Replaced input text with " +
                        $"\"{text}\": {typed}");
                });
        }

        private bool QueueGameClick(
            Vector2 screenPosition)
        {

            screenPosition = screenPosition + GameController.Window.GetWindowRectangleReal().Location;

            var overlayHideMilliseconds =
                Settings.ClickOverlayHideMilliseconds.Value;

            var releaseDelayMilliseconds =
                Settings.ClickReleaseDelayMilliseconds.Value;

            var preFocusDelayMilliseconds =
                Settings.ClickPreFocusDelayMilliseconds.Value;

            var postFocusDelayMilliseconds =
                Settings.ClickPostFocusDelayMilliseconds.Value;

            var mouseSettleDelayMilliseconds =
                Settings.MouseSettleDelayMilliseconds.Value;

            var mouseButtonHoldMilliseconds =
                Settings.MouseButtonHoldMilliseconds.Value;



            return QueueGameInput(
                overlayHideMilliseconds,
                releaseDelayMilliseconds,
                "Game click failed",
                gameWindowHandle =>
                {
                    Thread.Sleep(
                        preFocusDelayMilliseconds);





                    Thread.Sleep(
                        postFocusDelayMilliseconds);

                    var moved =
                        MouseInput.Mouse.MoveMouse(
                            screenPosition);

                    debugMessages.Add(
                        $"Moved mouse: {moved}, " +
                        $"position: {screenPosition}");

                    Thread.Sleep(
                        mouseSettleDelayMilliseconds);

                    var clicked =
                        MouseInput.Mouse.LeftClick(
                            mouseButtonHoldMilliseconds);

                    debugMessages.Add(
                        $"Clicked PoE: {clicked}");
                });
        }

        private bool QueueGameMove(
    Vector2 screenPosition)
        {

            screenPosition = screenPosition + GameController.Window.GetWindowRectangleReal().Location;

            var overlayHideMilliseconds =
                Settings.ClickOverlayHideMilliseconds.Value;

            var releaseDelayMilliseconds =
                Settings.ClickReleaseDelayMilliseconds.Value;

            var preFocusDelayMilliseconds =
                Settings.ClickPreFocusDelayMilliseconds.Value;

            var postFocusDelayMilliseconds =
                Settings.ClickPostFocusDelayMilliseconds.Value;

            var mouseSettleDelayMilliseconds =
                Settings.MouseSettleDelayMilliseconds.Value;

            var mouseButtonHoldMilliseconds =
                Settings.MouseButtonHoldMilliseconds.Value;



            return QueueGameInput(
                overlayHideMilliseconds,
                releaseDelayMilliseconds,
                "Game click failed",
                gameWindowHandle =>
                {
                    Thread.Sleep(
                        preFocusDelayMilliseconds);






                    Thread.Sleep(
                        postFocusDelayMilliseconds);

                    var moved =
                        MouseInput.Mouse.MoveMouse(
                            screenPosition);

                    debugMessages.Add(
                        $"Moved mouse: {moved}, " +
                        $"position: {screenPosition}");

                    Thread.Sleep(
                        mouseSettleDelayMilliseconds);

                });
        }

        private bool QueueGameInput(
            int overlayHideMilliseconds,
            int releaseDelayMilliseconds,
            string errorMessage,
            Action<IntPtr> inputAction)
        {
            if (Interlocked.CompareExchange(
                    ref _inputInProgress,
                    1,
                    0) != 0)
            {
                return false;
            }

            var gameWindowHandle =
                GameController?
                    .Window?
                    .Process?
                    .MainWindowHandle
                ?? IntPtr.Zero;

            if (gameWindowHandle == IntPtr.Zero)
            {
                Interlocked.Exchange(
                    ref _inputInProgress,
                    0);

                LogError(
                    "Path of Exile window handle is unavailable.");

                return false;
            }

            _hideOverlayUntilUtc =
                DateTime.UtcNow.AddMilliseconds(
                    overlayHideMilliseconds);

            _ = Task.Run(() =>
            {
                try
                {
                    inputAction(gameWindowHandle);
                }
                catch (Exception ex)
                {
                    LogError(
                        $"{errorMessage}: {ex}");
                }
                finally
                {
                    Thread.Sleep(
                        releaseDelayMilliseconds);

                    Interlocked.Exchange(
                        ref _inputInProgress,
                        0);
                }
            });

            return true;
        }

        private static int CalculateWantedAmount(
            int offeredAmount,
            int marketRateGet,
            int marketRateGive,
            int listingPricePercent)
        {
            if (offeredAmount <= 0 ||
                marketRateGet <= 0 ||
                marketRateGive <= 0 ||
                listingPricePercent <= 0)
            {
                return 0;
            }

            var priceMultiplier =
                listingPricePercent / 100d;

            var exactWantedAmount =
                offeredAmount *
                (marketRateGet * priceMultiplier) /
                marketRateGive;

            return Math.Max(
                1,
                (int)Math.Floor(exactWantedAmount));
        }

        private bool TryGetNinjaValue(
            CurrencyExchangeCurrencyPickerCurrencyOption option,
            out double chaosValue)
        {
            chaosValue = 0;

            var itemType =
                option?.ItemType;

            if (itemType == null)
            {
                // LogError(
                //     $"No BaseItemType found for " +
                //     $"{GetItemName(option)}.");

                return false;
            }

            _getNinjaBaseItemTypeValue ??=
                GameController.PluginBridge
                    .GetMethod<Func<BaseItemType, double>>(
                        "NinjaPrice.GetBaseItemTypeValue");

            if (_getNinjaBaseItemTypeValue == null)
            {
                LogError(
                    "NinjaPrice.GetBaseItemTypeValue " +
                    "is unavailable.");

                return false;
            }

            try
            {
                chaosValue =
                    _getNinjaBaseItemTypeValue(itemType);

                return true;
            }
            catch (Exception ex)
            {
                LogError(
                    $"Failed to retrieve Ninja Price for " +
                    $"{GetItemName(option)}: {ex}");

                return false;
            }
        }

        private double GetTotalNinjaValue(
            CurrencyExchangeCurrencyPickerCurrencyOption item)
        {
            if (item == null)
                return 0;

            return TryGetNinjaValue(
                item,
                out var unitValue)
                ? Math.Floor(unitValue * item.Owned)
                : 0;
        }

        private MarketRatio GetMarketRatioForPendingExchange(CurrencyExchangePanel currencyExchangePanel, int neededAmount = 100)
        {
            if (currencyExchangePanel == null)
            {
                return null;
            }

            if (Settings.ListPriceBasedOnHighestCompetingTrade)
            {
                debugMessages.Add($"ListPriceBasedOnHighestCompetingTrade is enabled. Attempting to retrieve all available market ratios...");
                var marketRatioPanelChildIndex = Settings.MarketRatioPanelIndex.Value;
                var marketRatioPanel = currencyExchangePanel.GetChildAtIndex(marketRatioPanelChildIndex);
                var marketRatioPanelTooltip = marketRatioPanel?.Tooltip;

                var allMarketRatioLinesGroupedByHeight = marketRatioPanelTooltip?.Children?
                    .GroupBy(child => child.Y)
                    .Select(group => group
                        .Select(child => child.TextNoTags)
                        .ToList())
                    .ToList();

                var availableMarketRatios = new List<MarketRatio>();




                foreach (var marketRatioLine in allMarketRatioLinesGroupedByHeight)
                {
                    var marketRatio = new MarketRatio
                    {
                        MarketGetRate = int.TryParse(marketRatioLine[0].Split(" : ").FirstOrDefault().Replace(">", "").Replace("<", ""), out int giveRate) ? giveRate : 0,
                        MarketGiveRate = int.TryParse(marketRatioLine[0].Split(" : ").LastOrDefault().Replace(">", "").Replace("<", ""), out int getRate) ? getRate : 0,
                    };


                    if (marketRatio.MarketGetRate <= 0 || marketRatio.MarketGiveRate <= 0)
                    {
                        CustomDebugImGuiWindow($"Skipping market ratio line due to invalid rates: {marketRatioLine[0]}");
                        continue;
                    }

                    marketRatio.AvailableTrades = int.TryParse(marketRatioLine[1].Replace(".", ""), out int trades) ? trades : 0;

                    CustomDebugImGuiWindow($"Found market ratio: {marketRatio.MarketGetRate}:{marketRatio.MarketGiveRate} with {marketRatio.AvailableTrades} available trades.");


                    if (marketRatio.MarketGetRate > 0 && marketRatio.MarketGiveRate > 0 && marketRatio.AvailableTrades > 0)
                    {
                        availableMarketRatios.Add(marketRatio);
                    }
                }

                availableMarketRatios = availableMarketRatios.OrderByDescending(ratio => ratio.MarketGetRate).ToList();

                if (Settings.OnlyPickRatiosWithSufficientStock)
                {
                    availableMarketRatios = availableMarketRatios.Where(ratio => ratio.AvailableTrades >= neededAmount).ToList();
                }

                CustomDebugImGuiWindow($"Json Formated Ratios: {JsonConvert.SerializeObject(availableMarketRatios, Formatting.Indented)}");

                return availableMarketRatios.FirstOrDefault();
            }
            else
            {
                return new MarketRatio
                {
                    MarketGetRate = currencyExchangePanel.MarketRateGet,
                    MarketGiveRate = currencyExchangePanel.MarketRateGive,
                    AvailableTrades = 0 // Not available in this mode
                };
            }
        }

        private void MyLogMessage(string message)
        {
            if (Settings.Debug)
            {
                LogMessage(message);
            }
        }

        private void DebugImGuiWindow()
        {
            try
            {
                if (!Settings.Debug)
                    return;

                ImGui.Separator();

                ImGui.Text($"Sell sequence step: {_sellSequenceStep}");
                ImGui.Text($"Pending sell item: {_pendingSellItemName}");
                ImGui.Text($"Pending sell owned amount: {_pendingSellOwnedAmount}");
                ImGui.Text($"Pending wanted item: {_pendingWantedItem?.Text}");
                ImGui.Text($"Input in progress: {(_inputInProgress != 0)}");

                ImGui.Separator();

                foreach (var message in debugMessages)
                {
                    ImGui.NewLine();
                    ImGui.TextWrapped(message);
                }
            }
            catch (Exception ex)
            {
            }
        }

        private void CustomDebugImGuiWindow(string message)
        {
            if (!Settings.Debug)
                return;

            ImGui.TextWrapped(message);
        }
    }
}