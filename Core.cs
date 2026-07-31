using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Elements.Village;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Microsoft.VisualBasic.Devices;
using SharpDX;
using NumVector2 = System.Numerics.Vector2;

namespace SellMyShit
{
    public class Core : BaseSettingsPlugin<Settings>
    {
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

        private Point storedMousePosition;
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
                    var currencyPicker = currencyExchangePanel.CurrencyPicker;
                    var searchInputChildIndex =
                                Settings
                                    .CurrencyPickerSearchInputChildIndex
                                    .Value;
                    var searchInput = currencyPicker.GetChildAtIndex(searchInputChildIndex);
                    var searchInputRect = searchInput.GetClientRect();
                    Graphics.DrawFrame(searchInputRect.BottomLeft, searchInput.GetClientRect().TopRight, Color.Red, 2);
                    Graphics.DrawCircleFilled(searchInputRect.Center.ToVector2Num(), 5, Color.Red, 5);

                    if (storedMousePosition.X > 0 && storedMousePosition.Y > 0) Graphics.DrawCircleFilled(storedMousePosition.ToVector2(), 5, Color.Red, 5);

                    var iHaveButtonChildIndex = Settings.IHaveButtonChildIndex.Value;
                    var iHaveButton = currencyExchangePanel.GetChildAtIndex(iHaveButtonChildIndex);
                    var iHaveButtonRect = iHaveButton.GetClientRect();
                    
                    Graphics.DrawFrame(iHaveButtonRect.BottomLeft, searchInput.GetClientRect().TopRight, Color.Red, 2);
                    Graphics.DrawCircleFilled(iHaveButtonRect.Center.ToVector2Num(), 5, Color.Red, 5);
                }


                if (currencyExchangePanel?.IsVisible != true)
                    return;

                ProcessSellSequence(currencyExchangePanel);

                var ownedItems =
                    GetCurrencyExchangeItems(currencyExchangePanel);

                DrawOwnedItemsUi(ownedItems);
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
                .CurrencyExchangePanel as CurrencyExchangePanel;
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

            DrawFilterAndSortControls();

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

        private void DrawFilterAndSortControls()
        {
            ImGui.InputText(
                "Filter",
                ref _filterText,
                256);

            ImGui.NewLine();
            ImGui.SameLine();

            DrawSortButton(
                SortOptions.Name,
                SortByName);

            ImGui.SameLine();

            DrawSortButton(
                SortOptions.Value,
                SortByValue);

            ImGui.SameLine();

            DrawSortButton(
                SortOptions.Owned,
                SortByOwned);
        }

        private void DrawSortButton(
            string label,
            int column)
        {
            var configuredColumn =
                GetConfiguredSortColumn();

            var isCurrentColumn =
                configuredColumn == column;

            var buttonLabel = isCurrentColumn
                ? $"Sort {label} " +
                  $"{(Settings.SortAscending.Value ? "↑" : "↓")}"
                : $"Sort {label}";

            if (!ImGui.Button(buttonLabel))
                return;

            if (isCurrentColumn)
            {
                Settings.SortAscending.Value =
                    !Settings.SortAscending.Value;

                return;
            }

            Settings.SortBy.Value =
                GetSortOption(column);

            Settings.SortAscending.Value = true;
        }

        private unsafe void DrawOwnedItemsTable(
    IReadOnlyList<
        CurrencyExchangeCurrencyPickerCurrencyOption> items)
        {
            const ImGuiTableFlags tableFlags =
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable(
                    "CurrencyOwnedItemsTable",
                    4,
                    tableFlags))
            {
                return;
            }

            ImGui.TableSetupColumn(
                "Name",
                ImGuiTableColumnFlags.NoHide);

            ImGui.TableSetupColumn(
                "Value",
                ImGuiTableColumnFlags.WidthFixed);

            ImGui.TableSetupColumn(
                "Owned",
                ImGuiTableColumnFlags.WidthFixed);

            ImGui.TableSetupColumn(
                "Action",
                ImGuiTableColumnFlags.WidthFixed);

            ImGui.TableHeadersRow();

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

            var itemName =
                GetItemName(_pendingSellItem);

            var ownedAmount =
                _pendingSellItem.Owned;

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
                    MyLogMessage($"Stored Mouse Position {storedMousePosition}");
                    {
                        SetSellSequenceStep(
                            isCurrencyPickerVisible
                                ? SellSequenceStep
                                    .ClickCurrencyPickerSearchInput
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
                                SellSequenceStep.WaitForCurrencyPicker);

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
                            (Vector2)currencyExchangePanel
                                .Children[iHaveButtonChildIndex]
                                .GetClientRect()
                                .Center;

                        // Graphics.DrawCircleFilled(iHaveButtonPosition.ToVector2Num(), 20, Color.Red, 5);

                        if (QueueGameClick(iHaveButtonPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.WaitForCurrencyPicker);
                        }

                        break;
                    }

                case SellSequenceStep.WaitForCurrencyPicker:
                    {
                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .ClickCurrencyPickerSearchInput);
                        }

                        break;
                    }

                case SellSequenceStep
                    .ClickCurrencyPickerSearchInput:
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
                            (Vector2)currencyPicker
                                .Children[searchInputChildIndex]
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(searchInputPosition))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .TypeCurrencyPickerSearchQuery);
                        }

                        break;
                    }

                case SellSequenceStep
                    .TypeCurrencyPickerSearchQuery:
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
                                    .ValidateSearchQuery);
                        }

                        break;
                    }

                case SellSequenceStep.ValidateSearchQuery:
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
                            SetSellSequenceStep(SellSequenceStep.WaitForCurrencyPickerSearchResults);
                        }

                        break;
                    }

                case SellSequenceStep
                    .WaitForCurrencyPickerSearchResults:
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
                                (Vector2)ownedItemRect.Center))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep
                                    .WaitForCurrencyPickerToClose);
                        }

                        break;
                    }

                case SellSequenceStep
                    .WaitForCurrencyPickerToClose:
                    {
                        if (IsGameInputBusy() ||
                            isCurrencyPickerVisible)
                        {
                            break;
                        }


                        SetSellSequenceStep(SellSequenceStep.WaitForMarketRatio);


                        SetSellSequenceStep(
                        SellSequenceStep.WaitForMarketRatio);

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

                        var marketRateGet =
                            currencyExchangePanel.MarketRateGet;

                        var marketRateGive =
                            currencyExchangePanel.MarketRateGive;

                        if (marketRateGet <= 0 ||
                            marketRateGive <= 0)
                        {
                            break;
                        }

                        SetSellSequenceStep(
                            SellSequenceStep.ClickOfferedItemInput);

                        break;
                    }

                case SellSequenceStep.ClickOfferedItemInput:
                    {
                        if (IsGameInputBusy())
                            break;

                        if (isCurrencyPickerVisible)
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.Start);

                            break;
                        }

                        if (offeredItemCountInput == null)
                        {
                            LogError(
                                "Offered item count input was not found.");

                            StopSellSequence();
                            break;
                        }

                        var offeredInputPosition =
                            (Vector2)offeredItemCountInput
                                .GetClientRect()
                                .Center;

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
                            (Vector2)wantedItemCountInput
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

                        var marketRateGet =
                            currencyExchangePanel.MarketRateGet;

                        var marketRateGive =
                            currencyExchangePanel.MarketRateGive;

                        var wantedValue =
                            CalculateWantedAmount(
                                ownedAmount,
                                marketRateGet,
                                marketRateGive,
                                Settings.ListingPricePercent.Value);

                        if (wantedValue <= 0)
                        {
                            LogError(
                                $"Invalid market ratio: " +
                                $"{marketRateGet}:{marketRateGive}");

                            StopSellSequence();
                            break;
                        }

                        MyLogMessage(
                            $"Pricing {ownedAmount} {itemName}: " +
                            $"market={marketRateGet}:{marketRateGive}, " +
                            $"wanted={wantedValue}");

                        if (QueueGameTextReplacement(
                                wantedValue.ToString(
                                    CultureInfo.InvariantCulture)))
                        {
                            SetSellSequenceStep(
                                SellSequenceStep.ClickSellButton);
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
                            (Vector2)currencyExchangePanel
                                .Children[sellButtonChildIndex]
                                .GetClientRect()
                                .Center;

                        if (QueueGameClick(sellButtonPosition))
                        {
                            MyLogMessage(
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
                                SellSequenceStep.WaitForCurrencyPicker);

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
                            (Vector2)currencyExchangePanel
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
                MyLogMessage(
                    "A sell sequence is already running.");

                return;
            }

            _pendingSellItem = item;

            SetSellSequenceStep(
                SellSequenceStep.Start);

            MyLogMessage(
                $"Started sell sequence for " +
                $"{GetItemName(item)}.");
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

            MyLogMessage(
                $"Sell sequence step: {step}");
        }

        private void StopSellSequence()
        {
            _sellSequenceStep = SellSequenceStep.Idle;
            _pendingSellItem = null;
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

                    var focused =
                        WinApi.SetForegroundWindow(
                            gameWindowHandle);

                    MyLogMessage(
                        $"Focused PoE before typing: {focused}");

                    Thread.Sleep(
                        postFocusDelayMilliseconds);

                    var typed =
                        KeyboardInput.Keyboard
                            .ReplaceText(text);

                    MyLogMessage(
                        $"Replaced input text with " +
                        $"\"{text}\": {typed}");
                });
        }

        private bool QueueGameClick(
            Vector2 screenPosition)
        {

            screenPosition = screenPosition + GameController.Window.GetWindowRectangle().Location;

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

                    var focused =
                        WinApi.SetForegroundWindow(
                            gameWindowHandle);

                    MyLogMessage(
                        $"Focused PoE: {focused}");

                    Thread.Sleep(
                        postFocusDelayMilliseconds);

                    var moved =
                        MouseInput.Mouse.MoveMouse(
                            screenPosition);

                    MyLogMessage(
                        $"Moved mouse: {moved}, " +
                        $"position: {screenPosition}");

                    Thread.Sleep(
                        mouseSettleDelayMilliseconds);

                    var clicked =
                        MouseInput.Mouse.LeftClick(
                            mouseButtonHoldMilliseconds);

                    MyLogMessage(
                        $"Clicked PoE: {clicked}");
                });
        }

        private bool QueueGameMove(
    Vector2 screenPosition)
        {

            screenPosition = screenPosition + GameController.Window.GetWindowRectangle().Location;

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

                    var focused =
                        WinApi.SetForegroundWindow(
                            gameWindowHandle);

                    MyLogMessage(
                        $"Focused PoE: {focused}");

                    Thread.Sleep(
                        postFocusDelayMilliseconds);

                    var moved =
                        MouseInput.Mouse.MoveMouse(
                            screenPosition);

                    MyLogMessage(
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



        private void MyLogMessage(string message)
        {
            if (Settings.Debug)
            {
                LogMessage(message);
            }
        }

    }
}