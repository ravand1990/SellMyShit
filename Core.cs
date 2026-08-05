using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.Village;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using NumVector2 = System.Numerics.Vector2;

namespace SellMyShit
{
    /// <summary>
    /// Automates listing owned currency on the in-game currency exchange.
    /// Renders an owned-items window next to the exchange panel, runs a
    /// step-based sell sequence per item (single or queued batch), and
    /// collects items from filled or canceled orders.
    /// </summary>
    public class Core : BaseSettingsPlugin<Settings>
    {
        public static Core Instance;

        private const string WantedCurrencyName = "Chaos Orb";

        /// <summary>
        /// Conservative fallback when a currency's maximum stack size cannot
        /// be read; most currencies stack to at least 10.
        /// </summary>
        private const int DefaultCurrencyMaxStackSize = 10;

        private const int SortColumnName = 0;
        private const int SortColumnValue = 1;
        private const int SortColumnOwned = 2;

        private const string SellButtonText = "place order";

        /// <summary>Gap between the exchange panel's right edge and the pinned window.</summary>
        private const float PinnedWindowPadding = 16f;

        private const int MaxDebugMessages = 200;

        private static readonly TimeSpan CurrencyItemsRefreshInterval =
            TimeSpan.FromMilliseconds(500);

        private Func<BaseItemType, double> _getNinjaBaseItemTypeValue;

        private string _filterText = string.Empty;

        private SyncTask<bool> _inputTask;
        private bool _releaseInputControlPending;

        private SellSequenceStep _sellSequenceStep = SellSequenceStep.Idle;
        private DateTime _sellSequenceStepStartedUtc;
        private CurrencyExchangeCurrencyPickerCurrencyOption _pendingSellItem;
        private CurrencyExchangeCurrencyPickerCurrencyOption _pendingWantedItem;
        private MarketRatio _pendingMarketRatio;

        private string _pendingSellItemName = string.Empty;
        private int _pendingSellOwnedAmount;

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

        /// <summary>
        /// Cursor position captured when an input session starts, stored
        /// window-relative; the default value means "not stored".
        /// </summary>
        private NumVector2 _storedMousePosition;

        private readonly List<string> _markedItemNames = [];
        private readonly Queue<string> _sellQueue = new();

        /// <summary>
        /// True from the first sell or collect start until the batch fully
        /// ends; drives the mouse-position restore and the InputHumanizer
        /// release in <see cref="EndInputSessionIfNeeded"/>.
        /// </summary>
        private bool _inputSessionActive;

        private bool _collectSequenceActive;
        private DateTime _nextCollectActionUtc = DateTime.MinValue;
        private int _lastCollectOrderId = -1;
        private int _lastCollectRemaining = -1;
        private int _collectRetryCount;

        private readonly List<string> _debugMessages = [];

        public Core()
        {
            Instance = this;
        }

        public override bool Initialise() => true;

        public override Job Tick()
        {
            if (_inputTask != null)
            {
                TaskUtils.RunOrRestart(ref _inputTask, () => null);
            }
            else if (_releaseInputControlPending)
            {
                _releaseInputControlPending = false;
                _inputTask = ReleaseInputControl();
            }

            return null;
        }

        private async SyncTask<bool> ReleaseInputControl()
        {
            await Input.ReleaseControl();
            Input.ReleaseResources();
            return true;
        }

        public override void Render()
        {
            if (!Settings.Enable.Value)
                return;

            try
            {
                var currencyExchangePanel = GetCurrencyExchangePanel();

                if (Settings.Debug)
                {
                    DrawDebugWindow();
                    DrawDebugMarkers(currencyExchangePanel);
                }

                if (currencyExchangePanel?.IsVisible != true)
                {
                    if (_sellSequenceStep != SellSequenceStep.Idle ||
                        _sellQueue.Count > 0)
                    {
                        LogError(
                            "The currency exchange panel closed; " +
                            "stopping the sell sequence.");

                        StopSellSequence();
                    }

                    if (_collectSequenceActive)
                    {
                        AddDebugMessage(
                            "The currency exchange panel closed; " +
                            "stopping the collect sequence.");

                        _collectSequenceActive = false;
                    }

                    EndInputSessionIfNeeded();
                    return;
                }

                ProcessSellSequence(currencyExchangePanel);
                ProcessCollectSequence(currencyExchangePanel);
                TryStartNextQueuedSell(currencyExchangePanel);
                EndInputSessionIfNeeded();

                var ownedItems = GetCurrencyExchangeItems(currencyExchangePanel);

                if (_sellSequenceStep == SellSequenceStep.Idle &&
                    _sellQueue.Count == 0)
                {
                    DrawOwnedItemsUi(currencyExchangePanel, ownedItems);
                }
            }
            catch (Exception ex)
            {
                LogError($"SellMyShit error: {ex}");
                StopSellSequence();
            }
        }

        private void DrawDebugMarkers(CurrencyExchangePanel currencyExchangePanel)
        {
            if (currencyExchangePanel?.IsVisible != true)
                return;

            var currencyPicker = currencyExchangePanel.CurrencyPicker;

            if (currencyPicker.IsVisible)
            {
                DrawDebugElementMarker(FindPickerSearchInput(currencyPicker));
                return;
            }

            if (_storedMousePosition != default)
                Graphics.DrawCircleFilled(_storedMousePosition, 5, Color.Red, 5);

            DrawDebugElementMarker(
                FindCurrencySelectButton(currencyExchangePanel, wantedSide: false));

            DrawDebugElementMarker(
                FindCurrencySelectButton(currencyExchangePanel, wantedSide: true));

            DrawDebugElementMarker(currencyExchangePanel.OfferedItemCountInput);
            DrawDebugElementMarker(currencyExchangePanel.WantedItemCountInput);
            DrawDebugElementMarker(FindSellButton(currencyExchangePanel));
        }

        private void DrawDebugElementMarker(Element element)
        {
            if (element == null)
                return;

            var rect = element.GetClientRect();

            Graphics.DrawFrame(rect.BottomLeft, rect.TopRight, Color.Red, 2);
            Graphics.DrawCircleFilled(rect.Center.ToVector2Num(), 5, Color.Red, 5);
        }

        private CurrencyExchangePanel GetCurrencyExchangePanel()
        {
            return GameController?
                .IngameState?
                .IngameUi?
                .CurrencyExchangePanel;
        }

        /// <summary>
        /// Finds a direct panel child whose first child carries the given text.
        /// Used instead of hardcoded child indexes so game UI reshuffles do not
        /// break element discovery.
        /// </summary>
        private static Element FindPanelButtonByText(
            CurrencyExchangePanel currencyExchangePanel,
            string text)
        {
            return currencyExchangePanel?
                .Children?
                .FirstOrDefault(child =>
                    child != null &&
                    child.ChildCount > 0 &&
                    string.Equals(
                        child.GetChildAtIndex(0)?.Text,
                        text,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static Element FindSellButton(
            CurrencyExchangePanel currencyExchangePanel)
        {
            return FindPanelButtonByText(currencyExchangePanel, SellButtonText);
        }

        /// <summary>
        /// Finds the "I Have" or "I Want" currency select button. The buttons
        /// show the selected currency name as their first child, which matches
        /// the panel's item types. Without a selection, falls back to the direct
        /// child closest to the corresponding count input on the same row
        /// (layout: button then count input per side).
        /// </summary>
        private static Element FindCurrencySelectButton(
            CurrencyExchangePanel currencyExchangePanel,
            bool wantedSide)
        {
            if (currencyExchangePanel == null)
                return null;

            var baseName = (wantedSide
                    ? currencyExchangePanel.WantedItemType
                    : currencyExchangePanel.OfferedItemType)?
                .BaseName;

            if (!string.IsNullOrEmpty(baseName))
            {
                var buttonByName =
                    FindPanelButtonByText(currencyExchangePanel, baseName);

                if (buttonByName != null)
                    return buttonByName;
            }

            var countInput = wantedSide
                ? currencyExchangePanel.WantedItemCountInput
                : currencyExchangePanel.OfferedItemCountInput;

            var otherCountInput = wantedSide
                ? currencyExchangePanel.OfferedItemCountInput
                : currencyExchangePanel.WantedItemCountInput;

            if (countInput == null)
                return null;

            var countInputRect = countInput.GetClientRect();

            return currencyExchangePanel
                .Children?
                .Where(child =>
                    child != null &&
                    child.ChildCount > 0 &&
                    child.IsVisible &&
                    child.Address != countInput.Address &&
                    child.Address != otherCountInput?.Address)
                .Where(child =>
                    Math.Abs(
                        child.GetClientRect().Center.Y -
                        countInputRect.Center.Y) <
                    countInputRect.Height)
                .OrderBy(child =>
                    Math.Abs(
                        child.GetClientRect().Center.X -
                        countInputRect.Center.X))
                .FirstOrDefault();
        }

        /// <summary>
        /// Finds the currency picker's search input: the only small direct
        /// child with exactly two children (text and caret). Falls back to
        /// whichever child holds keyboard focus.
        /// </summary>
        private static Element FindPickerSearchInput(
            CurrencyExchangeCurrencyPickerElement currencyPicker)
        {
            if (currencyPicker == null)
                return null;

            var optionContainerAddress =
                currencyPicker.OptionContainer?.Address ?? 0;

            return currencyPicker.Children?
                       .FirstOrDefault(child =>
                           child != null &&
                           child.ChildCount == 2 &&
                           child.Height > 0 &&
                           child.Height < 100 &&
                           child.Address != optionContainerAddress)
                   ?? currencyPicker.Children?
                       .FirstOrDefault(child => child?.IsActive == true);
        }

        /// <summary>
        /// Finds the collect slot of a placed order: the small childed element
        /// without text nearest below the order's "Buying"/"Selling" label.
        /// </summary>
        private static Element FindOrderCollectSlot(
            Element orderElement,
            bool buyingSide)
        {
            if (orderElement?.Children == null)
                return null;

            var labelText = buyingSide ? "Buying" : "Selling";

            var label = orderElement.Children
                .FirstOrDefault(child =>
                    string.Equals(
                        child?.Text,
                        labelText,
                        StringComparison.OrdinalIgnoreCase));

            if (label == null)
                return null;

            var labelCenterX = label.GetClientRect().Center.X;
            var maxSlotWidth = orderElement.GetClientRect().Width / 3;

            return orderElement.Children
                .Where(child =>
                    child != null &&
                    child.ChildCount > 0 &&
                    child.IsVisible &&
                    string.IsNullOrEmpty(child.Text))
                .Where(child =>
                {
                    var rect = child.GetClientRect();
                    return rect.Width > 0 && rect.Width < maxSlotWidth;
                })
                .OrderBy(child =>
                    Math.Abs(child.GetClientRect().Center.X - labelCenterX))
                .FirstOrDefault();
        }

        /// <summary>
        /// Returns the owned, non-excluded currency picker options, refreshed
        /// from panel memory at most every
        /// <see cref="CurrencyItemsRefreshInterval"/>.
        /// </summary>
        private List<CurrencyExchangeCurrencyPickerCurrencyOption>
            GetCurrencyExchangeItems(CurrencyExchangePanel currencyExchangePanel)
        {
            if (currencyExchangePanel == null)
                return [];

            var now = DateTime.UtcNow;

            if (_currencyExchangeItemsCacheInitialized &&
                now < _nextCurrencyItemsRefreshUtc)
            {
                return _currencyExchangeItemsCache;
            }

            _nextCurrencyItemsRefreshUtc = now.Add(CurrencyItemsRefreshInterval);

            try
            {
                var options = currencyExchangePanel.CurrencyPicker?.Options;

                if (options == null)
                    return _currencyExchangeItemsCache;

                _currencyExchangeItemsCache = options
                    .Where(item =>
                        item?.Children != null &&
                        item.Children.Count > 0 &&
                        item.Owned > 0)
                    .Where(item =>
                        !Settings.IsCurrencyExcluded(GetItemName(item)))
                    .Distinct()
                    .ToList();

                _currencyExchangeItemsCacheInitialized = true;
                _currencyItemsVersion++;

                return _currencyExchangeItemsCache;
            }
            catch (Exception ex)
            {
                LogError($"SellMyShit error while extracting items: {ex}");
                return _currencyExchangeItemsCache;
            }
        }

        /// <summary>
        /// Returns the owned items filtered and sorted for display, cached
        /// until the source items, filter, or sort configuration change.
        /// </summary>
        private IReadOnlyList<CurrencyExchangeCurrencyPickerCurrencyOption>
            GetDisplayItems(
                IReadOnlyList<CurrencyExchangeCurrencyPickerCurrencyOption> ownedItems)
        {
            var sortColumn = GetConfiguredSortColumn();
            var sortAscending = Settings.SortAscending.Value;
            var filter = _filterText?.Trim() ?? string.Empty;

            var cacheIsCurrent =
                _displayItemsSourceVersion == _currencyItemsVersion &&
                _displayItemsSortColumn == sortColumn &&
                _displayItemsSortAscending == sortAscending &&
                string.Equals(_displayItemsFilter, filter, StringComparison.Ordinal);

            if (cacheIsCurrent)
                return _displayItemsCache;

            _displayItemsCache.Clear();

            foreach (var item in ownedItems)
            {
                if (item == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(filter) &&
                    !GetItemName(item).Contains(
                        filter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _displayItemsCache.Add(item);
            }

            _displayItemsCache.Sort(
                (left, right) =>
                    CompareItems(left, right, sortColumn, sortAscending));

            _displayItemsSourceVersion = _currencyItemsVersion;
            _displayItemsFilter = filter;
            _displayItemsSortColumn = sortColumn;
            _displayItemsSortAscending = sortAscending;

            return _displayItemsCache;
        }

        private void DrawOwnedItemsUi(
            CurrencyExchangePanel currencyExchangePanel,
            List<CurrencyExchangeCurrencyPickerCurrencyOption> ownedItems)
        {
            ownedItems ??= [];

            if (IsGameInputBusy())
                return;

            var windowFlags = ImGuiWindowFlags.NoTitleBar;

            if (Settings.PinWindow)
            {
                var panelRect = currencyExchangePanel.GetClientRect();

                ImGui.SetNextWindowPos(
                    new NumVector2(
                        panelRect.Right + PinnedWindowPadding,
                        panelRect.Top),
                    ImGuiCond.Always);

                windowFlags |= ImGuiWindowFlags.NoMove;
            }

            ImGui.SetNextWindowSize(
                new NumVector2(520, 420),
                ImGuiCond.FirstUseEver);

            if (!ImGui.Begin("Owned Items", windowFlags))
            {
                ImGui.End();
                return;
            }

            PruneMarkedItems(currencyExchangePanel, ownedItems);

            ImGui.InputText("Filter", ref _filterText, 256);

            var filteredItems = GetDisplayItems(ownedItems);

            ImGui.Text($"Owned Items: {filteredItems.Count} / {ownedItems.Count}");
            ImGui.Separator();

            var bottomBarHeight =
                ImGui.GetTextLineHeightWithSpacing() +
                ImGui.GetFrameHeightWithSpacing() +
                ImGui.GetStyle().ItemSpacing.Y * 3 +
                4f;

            if (ImGui.BeginChild(
                    "OwnedItemsChild",
                    new NumVector2(0, -bottomBarHeight),
                    ImGuiChildFlags.None,
                    ImGuiWindowFlags.None))
            {
                DrawOwnedItemsTable(currencyExchangePanel, filteredItems);
            }

            ImGui.EndChild();

            DrawBatchControls(currencyExchangePanel);

            ImGui.End();
        }

        /// <summary>
        /// Drops marks for items that vanished from the owned list and trims
        /// the selection when trade slots filled up in the meantime.
        /// </summary>
        private void PruneMarkedItems(
            CurrencyExchangePanel currencyExchangePanel,
            List<CurrencyExchangeCurrencyPickerCurrencyOption> ownedItems)
        {
            if (_markedItemNames.Count == 0)
                return;

            var ownedNames = ownedItems
                .Select(GetItemName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _markedItemNames.RemoveAll(name => !ownedNames.Contains(name));

            var freeSlots = GetFreeTradeSlots(currencyExchangePanel);

            if (_markedItemNames.Count > freeSlots)
            {
                _markedItemNames.RemoveRange(
                    freeSlots,
                    _markedItemNames.Count - freeSlots);
            }
        }

        private void DrawBatchControls(
            CurrencyExchangePanel currencyExchangePanel)
        {
            var slotsInUse = GetSlotsInUse(currencyExchangePanel);
            var maxTrades = Settings.MaxConcurrentTrades.Value;
            var freeSlots = Math.Max(0, maxTrades - slotsInUse);
            var collectibleOrders = GetCollectibleOrderCount(currencyExchangePanel);

            ImGui.Separator();

            ImGui.Text($"Trade slots in use: {slotsInUse}/{maxTrades}");
            ImGui.SameLine();
            ImGui.Text($"| Marked to sell: {_markedItemNames.Count}/{freeSlots}");

            var sellDisabled =
                _markedItemNames.Count == 0 ||
                _collectSequenceActive;

            ImGui.BeginDisabled(sellDisabled);

            if (ImGui.Button(
                    $"Sell {_markedItemNames.Count} marked item(s)##SellMarked"))
            {
                StartMarkedSellBatch(currencyExchangePanel);
            }

            ImGui.EndDisabled();

            ImGui.SameLine();

            if (_collectSequenceActive)
            {
                if (ImGui.Button("Stop collecting##CollectAll"))
                    StopCollectSequence("the user requested it");
            }
            else
            {
                ImGui.BeginDisabled(collectibleOrders == 0);

                if (ImGui.Button(
                        $"Collect all items ({collectibleOrders})##CollectAll"))
                {
                    StartCollectSequence();
                }

                ImGui.EndDisabled();
            }
        }

        /// <summary>
        /// Adds <see cref="ImGuiTableColumnFlags.DefaultSort"/> to the flags
        /// when the given sort option is the configured default column.
        /// </summary>
        private ImGuiTableColumnFlags WithDefaultSort(
            string sortOption,
            ImGuiTableColumnFlags flags)
        {
            return Settings.SortBy.Value == sortOption
                ? flags | ImGuiTableColumnFlags.DefaultSort
                : flags;
        }

        private unsafe void DrawOwnedItemsTable(
            CurrencyExchangePanel currencyExchangePanel,
            IReadOnlyList<CurrencyExchangeCurrencyPickerCurrencyOption> items)
        {
            const ImGuiTableFlags tableFlags =
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.Sortable |
                ImGuiTableFlags.SizingFixedFit |
                ImGuiTableFlags.NoSavedSettings;

            if (!ImGui.BeginTable("CurrencyOwnedItemsTable", 5, tableFlags))
                return;

            var freeSlots = GetFreeTradeSlots(currencyExchangePanel);

            ImGui.TableSetupColumn(
                "Name",
                WithDefaultSort(
                    SortOptions.Name,
                    ImGuiTableColumnFlags.WidthStretch |
                    ImGuiTableColumnFlags.PreferSortAscending));

            ImGui.TableSetupColumn(
                "Value",
                WithDefaultSort(
                    SortOptions.Value,
                    ImGuiTableColumnFlags.WidthFixed |
                    ImGuiTableColumnFlags.PreferSortDescending));

            ImGui.TableSetupColumn(
                "Owned",
                WithDefaultSort(
                    SortOptions.Owned,
                    ImGuiTableColumnFlags.WidthFixed |
                    ImGuiTableColumnFlags.PreferSortDescending));

            ImGui.TableSetupColumn(
                "Mark",
                ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.NoSort);

            ImGui.TableSetupColumn(
                "Action",
                ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.NoSort);

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
                        DrawOwnedItemRow(items[index], index, freeSlots);
                    }
                }
            }
            finally
            {
                clipper.Destroy();
            }

            ImGui.EndTable();
        }

        /// <summary>
        /// Writes the table's clickable-header sort state back into the
        /// settings and invalidates the display cache when it changed.
        /// </summary>
        private unsafe void ApplyImGuiTableSorting()
        {
            var sortSpecs = ImGui.TableGetSortSpecs();

            if (sortSpecs.NativePtr == null ||
                !sortSpecs.SpecsDirty ||
                sortSpecs.SpecsCount <= 0)
            {
                return;
            }

            var columnSortSpec = sortSpecs.Specs;

            Settings.SortBy.Value = GetSortOption(columnSortSpec.ColumnIndex);

            Settings.SortAscending.Value =
                columnSortSpec.SortDirection == ImGuiSortDirection.Ascending;

            _displayItemsSourceVersion = -1;

            sortSpecs.SpecsDirty = false;
        }

        private void DrawOwnedItemRow(
            CurrencyExchangeCurrencyPickerCurrencyOption item,
            int index,
            int freeSlots)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            var itemName = GetItemName(item);
            ImGui.TextUnformatted(itemName);

            ImGui.TableNextColumn();

            var totalValue = GetTotalNinjaValue(item);
            ImGui.TextUnformatted(totalValue.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();

            ImGui.TextUnformatted(item.Owned.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();

            var isMarked = _markedItemNames.Contains(itemName);

            var markDisabled =
                !isMarked &&
                _markedItemNames.Count >= freeSlots;

            ImGui.BeginDisabled(markDisabled);

            if (ImGui.Checkbox($"##Mark{index}", ref isMarked))
            {
                if (isMarked)
                    _markedItemNames.Add(itemName);
                else
                    _markedItemNames.Remove(itemName);
            }

            ImGui.EndDisabled();

            ImGui.TableNextColumn();

            ImGui.BeginDisabled(_collectSequenceActive);

            if (ImGui.Button($"Sell##{index}"))
                StartSellSequence(item);

            ImGui.EndDisabled();
        }

        private int CompareItems(
            CurrencyExchangeCurrencyPickerCurrencyOption left,
            CurrencyExchangeCurrencyPickerCurrencyOption right,
            int sortColumn,
            bool ascending)
        {
            if (left == null && right == null)
                return 0;

            if (left == null)
                return ascending ? -1 : 1;

            if (right == null)
                return ascending ? 1 : -1;

            switch (sortColumn)
            {
                case SortColumnValue:
                {
                    var leftValue = GetTotalNinjaValue(left);
                    var rightValue = GetTotalNinjaValue(right);

                    return ascending
                        ? leftValue.CompareTo(rightValue)
                        : rightValue.CompareTo(leftValue);
                }

                case SortColumnOwned:
                    return ascending
                        ? left.Owned.CompareTo(right.Owned)
                        : right.Owned.CompareTo(left.Owned);

                case SortColumnName:
                default:
                {
                    var leftName = GetItemName(left);
                    var rightName = GetItemName(right);

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
                SortOptions.Name => SortColumnName,
                SortOptions.Owned => SortColumnOwned,
                _ => SortColumnValue
            };
        }

        private static string GetSortOption(int sortColumn)
        {
            return sortColumn switch
            {
                SortColumnName => SortOptions.Name,
                SortColumnOwned => SortOptions.Owned,
                _ => SortOptions.Value
            };
        }

        /// <summary>
        /// Advances the sell sequence state machine by at most one game action
        /// per frame.
        /// </summary>
        /// <remarks>
        /// Queued items start with only a name; the picker option element and
        /// owned amount are re-resolved from the live picker search results,
        /// because cached elements go stale once the picker is reopened. A step
        /// exceeding the configured timeout skips the current item but keeps
        /// the queue alive so one stuck item does not abort the whole batch.
        /// </remarks>
        private void ProcessSellSequence(
            CurrencyExchangePanel currencyExchangePanel)
        {
            if (_sellSequenceStep == SellSequenceStep.Idle)
                return;

            if (currencyExchangePanel == null ||
                !currencyExchangePanel.IsVisible)
            {
                LogError("The currency exchange panel is no longer visible.");
                StopSellSequence();
                return;
            }

            if (string.IsNullOrEmpty(_pendingSellItemName))
            {
                LogError("There is no pending sell item.");
                StopSellSequence();
                return;
            }

            if (TimeInCurrentStep() >
                TimeSpan.FromSeconds(Settings.SequenceTimeoutSeconds.Value))
            {
                LogError(
                    $"The sell sequence timed out at step {_sellSequenceStep} " +
                    $"for \"{_pendingSellItemName}\"; skipping this item.");

                CompleteCurrentSellItem();
                return;
            }

            var itemName = _pendingSellItemName;
            var ownedAmount = _pendingSellOwnedAmount;

            var currencyPicker = currencyExchangePanel.CurrencyPicker;
            var isCurrencyPickerVisible = currencyPicker?.IsVisible == true;

            var offeredItemCountInput = currencyExchangePanel.OfferedItemCountInput;
            var wantedItemCountInput = currencyExchangePanel.WantedItemCountInput;

            switch (_sellSequenceStep)
            {
                case SellSequenceStep.Start:
                {
                    SetSellSequenceStep(
                        isCurrencyPickerVisible
                            ? SellSequenceStep.ClickCurrencyPickerOfferedSearchInput
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

                    var iHaveButton = FindCurrencySelectButton(
                        currencyExchangePanel,
                        wantedSide: false);

                    if (iHaveButton == null)
                    {
                        LogError("The \"I Have\" button was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(iHaveButton.GetClientRect().Center))
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
                            SellSequenceStep.ClickCurrencyPickerOfferedSearchInput);
                    }

                    break;
                }

                case SellSequenceStep.ClickCurrencyPickerOfferedSearchInput:
                {
                    if (IsGameInputBusy())
                        break;

                    if (!isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.ClickIHave);
                        break;
                    }

                    var searchInput = FindPickerSearchInput(currencyPicker);

                    if (searchInput == null)
                    {
                        LogError("The currency picker search input was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(searchInput.GetClientRect().Center))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.TypeCurrencyPickerOfferedSearchQuery);
                    }

                    break;
                }

                case SellSequenceStep.TypeCurrencyPickerOfferedSearchQuery:
                {
                    if (IsGameInputBusy())
                        break;

                    if (!isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.ClickIHave);
                        break;
                    }

                    if (QueueGameTextReplacement(itemName))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.ValidateOfferedSearchQuery);
                    }

                    break;
                }

                case SellSequenceStep.ValidateOfferedSearchQuery:
                {
                    if (IsGameInputBusy())
                        break;

                    if (!isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.ClickIHave);
                        break;
                    }

                    var searchInput = FindPickerSearchInput(currencyPicker);

                    if (searchInput == null)
                    {
                        LogError("The currency picker search input was not found.");
                        StopSellSequence();
                        break;
                    }

                    var searchText = searchInput.GetChildAtIndex(0)?.Text;

                    if (searchText == itemName)
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.WaitForCurrencyPickerOfferedSearchResults);
                    }

                    break;
                }

                case SellSequenceStep.WaitForCurrencyPickerOfferedSearchResults:
                {
                    if (IsGameInputBusy())
                        break;

                    if (!isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.ClickIHave);
                        break;
                    }

                    if (TimeInCurrentStep() <
                        TimeSpan.FromMilliseconds(
                            Settings.GetRandomizedActionDelay()))
                    {
                        break;
                    }

                    _pendingSellItem = currencyPicker.Options?
                        .FirstOrDefault(option =>
                            option?.Children != null &&
                            option.Children.Any(child => child?.Text == itemName));

                    if (_pendingSellItem == null)
                        break;

                    _pendingSellOwnedAmount = _pendingSellItem.Owned;

                    if (!_pendingSellItem.IsVisible &&
                        !_pendingSellItem.IsVisibleLocal)
                    {
                        break;
                    }

                    if (_pendingSellItem.Position.Y >
                        currencyPicker.OptionContainer.Height)
                    {
                        break;
                    }

                    SetSellSequenceStep(SellSequenceStep.ClickOwnedItem);
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

                    if (_pendingSellItem == null)
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.WaitForCurrencyPickerOfferedSearchResults);

                        break;
                    }

                    var ownedItemRect = _pendingSellItem.GetClientRect();

                    if (ownedItemRect.Width <= 0 || ownedItemRect.Height <= 0)
                        break;

                    if (QueueGameClick(ownedItemRect.Center))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.WaitForOfferedCurrencyPickerToClose);
                    }

                    break;
                }

                case SellSequenceStep.WaitForOfferedCurrencyPickerToClose:
                {
                    if (IsGameInputBusy() || isCurrencyPickerVisible)
                        break;

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

                    var selectedWantedItemName =
                        currencyExchangePanel.WantedItemType?.BaseName;

                    if (selectedWantedItemName == WantedCurrencyName)
                    {
                        AddDebugMessage(
                            $"{WantedCurrencyName} is already selected " +
                            "as the wanted currency.");

                        SetSellSequenceStep(SellSequenceStep.WaitForMarketRatio);
                        break;
                    }

                    SetSellSequenceStep(SellSequenceStep.ClickIWant);
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

                    var iWantButton = FindCurrencySelectButton(
                        currencyExchangePanel,
                        wantedSide: true);

                    if (iWantButton == null)
                    {
                        LogError("The \"I Want\" button was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(iWantButton.GetClientRect().Center))
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
                        SetSellSequenceStep(SellSequenceStep.ClickIWant);
                        break;
                    }

                    var searchInput = FindPickerSearchInput(currencyPicker);

                    if (searchInput == null)
                    {
                        LogError(
                            "The wanted currency picker search input was not found.");

                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(searchInput.GetClientRect().Center))
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
                        SetSellSequenceStep(SellSequenceStep.ClickIWant);
                        break;
                    }

                    if (QueueGameTextReplacement(WantedCurrencyName))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.ValidateWantedSearchQuery);
                    }

                    break;
                }

                case SellSequenceStep.ValidateWantedSearchQuery:
                {
                    if (IsGameInputBusy())
                        break;

                    if (!isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.ClickIWant);
                        break;
                    }

                    var searchInput = FindPickerSearchInput(currencyPicker);

                    if (searchInput == null)
                    {
                        LogError(
                            "The wanted currency picker search input was not found.");

                        StopSellSequence();
                        break;
                    }

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
                            SellSequenceStep.WaitForCurrencyPickerWantedSearchResults);
                    }

                    break;
                }

                case SellSequenceStep.WaitForCurrencyPickerWantedSearchResults:
                {
                    if (IsGameInputBusy())
                        break;

                    if (!isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.ClickIWant);
                        break;
                    }

                    if (TimeInCurrentStep() <
                        TimeSpan.FromMilliseconds(
                            Settings.GetRandomizedActionDelay()))
                    {
                        break;
                    }

                    _pendingWantedItem = currencyPicker.Options?
                        .FirstOrDefault(option =>
                            option?.Children != null &&
                            option.Children.Any(child =>
                                child?.Text == WantedCurrencyName));

                    if (_pendingWantedItem == null)
                        break;

                    var wantedItemRect = _pendingWantedItem.GetClientRect();

                    if (wantedItemRect.Width <= 0 || wantedItemRect.Height <= 0)
                        break;

                    var optionContainer = currencyPicker.OptionContainer;

                    if (optionContainer != null &&
                        !optionContainer.GetClientRect().Intersects(wantedItemRect))
                    {
                        break;
                    }

                    SetSellSequenceStep(SellSequenceStep.ClickWantedItem);
                    break;
                }

                case SellSequenceStep.ClickWantedItem:
                {
                    if (IsGameInputBusy())
                        break;

                    if (_pendingWantedItem == null)
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.WaitForCurrencyPickerWantedSearchResults);

                        break;
                    }

                    var wantedItemRect = _pendingWantedItem.GetClientRect();

                    if (wantedItemRect.Width <= 0 || wantedItemRect.Height <= 0)
                        break;

                    if (QueueGameClick(wantedItemRect.Center))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.WaitForWantedCurrencyPickerToClose);
                    }

                    break;
                }

                case SellSequenceStep.WaitForWantedCurrencyPickerToClose:
                {
                    if (IsGameInputBusy() || isCurrencyPickerVisible)
                        break;

                    _pendingWantedItem = null;

                    SetSellSequenceStep(SellSequenceStep.WaitForMarketRatio);
                    break;
                }

                case SellSequenceStep.WaitForMarketRatio:
                {
                    if (IsGameInputBusy())
                        break;

                    if (TimeInCurrentStep() <
                        TimeSpan.FromMilliseconds(
                            Settings.GetRandomizedActionDelay()))
                    {
                        break;
                    }

                    var marketRatio = GetMarketRatioForPendingExchange(
                        currencyExchangePanel,
                        ownedAmount);

                    if (marketRatio == null ||
                        marketRatio.MarketGetRate <= 0 ||
                        marketRatio.MarketGiveRate <= 0)
                    {
                        break;
                    }

                    AddDebugMessage(
                        $"Market ratio for {ownedAmount} of {itemName}: " +
                        $"{marketRatio.MarketGetRate}:{marketRatio.MarketGiveRate}");

                    _pendingMarketRatio = marketRatio;

                    SetSellSequenceStep(SellSequenceStep.ClickOfferedItemInput);
                    break;
                }

                case SellSequenceStep.ClickOfferedItemInput:
                {
                    if (IsGameInputBusy())
                        break;

                    if (offeredItemCountInput == null)
                    {
                        LogError("The offered item count input was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(
                            offeredItemCountInput.GetClientRect().Center))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.TypeOfferedItemValue);
                    }

                    break;
                }

                case SellSequenceStep.TypeOfferedItemValue:
                {
                    if (IsGameInputBusy())
                        break;

                    var offeredValue =
                        ownedAmount.ToString(CultureInfo.InvariantCulture);

                    if (QueueGameTextReplacement(offeredValue))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.ClickWantedItemInput);
                    }

                    break;
                }

                case SellSequenceStep.ClickWantedItemInput:
                {
                    if (IsGameInputBusy())
                        break;

                    if (isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.Start);
                        break;
                    }

                    if (wantedItemCountInput == null)
                    {
                        LogError("The wanted item count input was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(
                            wantedItemCountInput.GetClientRect().Center))
                    {
                        SetSellSequenceStep(
                            SellSequenceStep.TypeWantedItemValue);
                    }

                    break;
                }

                case SellSequenceStep.TypeWantedItemValue:
                {
                    if (IsGameInputBusy())
                        break;

                    var wantedValue = CalculateWantedAmount(
                        ownedAmount,
                        _pendingMarketRatio.MarketGetRate,
                        _pendingMarketRatio.MarketGiveRate,
                        Settings.ListingPricePercent.Value);

                    if (wantedValue <= 0)
                    {
                        LogError(
                            $"Invalid market ratio: " +
                            $"{_pendingMarketRatio.MarketGetRate}:" +
                            $"{_pendingMarketRatio.MarketGiveRate}");

                        StopSellSequence();
                        break;
                    }

                    AddDebugMessage(
                        $"Pricing {ownedAmount} {itemName}: " +
                        $"market={_pendingMarketRatio.MarketGetRate}:" +
                        $"{_pendingMarketRatio.MarketGiveRate}, " +
                        $"wanted={wantedValue}");

                    if (QueueGameTextReplacement(
                            wantedValue.ToString(CultureInfo.InvariantCulture)))
                    {
                        SetSellSequenceStep(SellSequenceStep.BlurInput);
                    }

                    break;
                }

                case SellSequenceStep.BlurInput:
                {
                    if (IsGameInputBusy())
                        break;

                    var ratioElement = currencyExchangePanel.RatioElement;

                    if (ratioElement == null)
                    {
                        LogError("The market ratio element was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(ratioElement.GetClientRect().Center))
                    {
                        AddDebugMessage(
                            "Blurring the input to lock in the ratio...");

                        SetSellSequenceStep(
                            SellSequenceStep.CheckIfSellButtonIsActive);
                    }

                    break;
                }

                case SellSequenceStep.CheckIfSellButtonIsActive:
                {
                    if (IsGameInputBusy())
                        break;

                    var sellButton = FindSellButton(currencyExchangePanel);

                    if (sellButton == null)
                    {
                        LogError("The sell button was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (sellButton.IsActive)
                        SetSellSequenceStep(SellSequenceStep.ClickSellButton);

                    break;
                }

                case SellSequenceStep.ClickSellButton:
                {
                    if (IsGameInputBusy())
                        break;

                    if (isCurrencyPickerVisible)
                    {
                        SetSellSequenceStep(SellSequenceStep.Start);
                        break;
                    }

                    var sellButton = FindSellButton(currencyExchangePanel);

                    if (sellButton == null)
                    {
                        LogError("The sell button was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(sellButton.GetClientRect().Center))
                    {
                        AddDebugMessage(
                            $"Completed the sell sequence for " +
                            $"{ownedAmount} of {itemName}.");

                        SetSellSequenceStep(
                            SellSequenceStep.CheckUnfavorableTrade);
                    }

                    break;
                }

                case SellSequenceStep.CheckUnfavorableTrade:
                {
                    SetSellSequenceStep(SellSequenceStep.ReopenIHave);
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

                    var iHaveButton = FindCurrencySelectButton(
                        currencyExchangePanel,
                        wantedSide: false);

                    if (iHaveButton == null)
                    {
                        LogError("The \"I Have\" button was not found.");
                        StopSellSequence();
                        break;
                    }

                    if (QueueGameClick(iHaveButton.GetClientRect().Center))
                        SetSellSequenceStep(SellSequenceStep.End);

                    break;
                }

                case SellSequenceStep.End:
                {
                    if (IsGameInputBusy())
                        break;

                    if (_sellQueue.Count > 0 && !isCurrencyPickerVisible)
                        break;

                    CompleteCurrentSellItem();
                    break;
                }
            }
        }

        private void StartSellSequence(
            CurrencyExchangeCurrencyPickerCurrencyOption item)
        {
            if (item == null)
                return;

            StartSellSequenceByName(GetItemName(item));
        }

        /// <summary>
        /// Starts the sell sequence for an item identified only by name; the
        /// live picker option element is resolved during the sequence.
        /// </summary>
        private void StartSellSequenceByName(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return;

            if (_sellSequenceStep != SellSequenceStep.Idle)
            {
                AddDebugMessage("A sell sequence is already running.");
                return;
            }

            if (_collectSequenceActive)
            {
                AddDebugMessage(
                    "The collect sequence is running; " +
                    "not starting a sell sequence.");

                return;
            }

            BeginInputSession();

            _pendingSellItem = null;
            _pendingSellItemName = itemName;
            _pendingSellOwnedAmount = 0;

            AddDebugMessage($"Started the sell sequence for {itemName}.");

            SetSellSequenceStep(SellSequenceStep.Start);
        }

        /// <summary>
        /// Stores the cursor position (window-relative, so the restore lands on
        /// the original spot after adding the window offset) and brings the
        /// game window to the foreground.
        /// </summary>
        private void BeginInputSession()
        {
            if (!_inputSessionActive)
            {
                _inputSessionActive = true;

                var cursorPosition = MouseInput.Mouse.GetCursorPosition();

                var windowTopLeft = GameController.Window
                    .GetWindowRectangleReal()
                    .Location;

                _storedMousePosition = new NumVector2(
                    cursorPosition.X - windowTopLeft.X,
                    cursorPosition.Y - windowTopLeft.Y);

                AddDebugMessage(
                    $"Stored the mouse position at {_storedMousePosition}.");
            }

            if (!GameController.Window.IsForeground())
            {
                var focused = WinApi.SetForegroundWindow(
                    GameController.Window.Process.MainWindowHandle);

                AddDebugMessage($"Focused the game window: {focused}");
            }
        }

        /// <summary>
        /// Runs once the sell queue and the collect sequence are fully drained:
        /// restores the mouse position (waiting a frame for the move to
        /// complete) and then releases the InputHumanizer controller.
        /// </summary>
        private void EndInputSessionIfNeeded()
        {
            if (!_inputSessionActive)
                return;

            if (_sellSequenceStep != SellSequenceStep.Idle ||
                _sellQueue.Count > 0 ||
                _collectSequenceActive ||
                IsGameInputBusy())
            {
                return;
            }

            if (Settings.RestoreMousePosition &&
                _storedMousePosition != default)
            {
                var restorePosition = _storedMousePosition;
                _storedMousePosition = default;

                QueueGameMove(
                    new Vector2(restorePosition.X, restorePosition.Y));

                return;
            }

            _storedMousePosition = default;
            _inputSessionActive = false;
            _releaseInputControlPending = true;
        }

        private void StartMarkedSellBatch(
            CurrencyExchangePanel currencyExchangePanel)
        {
            if (_sellSequenceStep != SellSequenceStep.Idle ||
                _collectSequenceActive ||
                _markedItemNames.Count == 0)
            {
                return;
            }

            var freeSlots = GetFreeTradeSlots(currencyExchangePanel);

            foreach (var itemName in _markedItemNames.Take(freeSlots))
                _sellQueue.Enqueue(itemName);

            AddDebugMessage(
                $"Queued {_sellQueue.Count} marked item(s) for selling " +
                $"({freeSlots} free trade slot(s)).");

            _markedItemNames.Clear();
        }

        private void TryStartNextQueuedSell(
            CurrencyExchangePanel currencyExchangePanel)
        {
            if (_sellSequenceStep != SellSequenceStep.Idle ||
                _sellQueue.Count == 0 ||
                _collectSequenceActive ||
                IsGameInputBusy())
            {
                return;
            }

            if (GetFreeTradeSlots(currencyExchangePanel) <= 0)
            {
                LogError(
                    "There are no free trade slots left; " +
                    "dropping the remaining sell queue.");

                _sellQueue.Clear();
                return;
            }

            StartSellSequenceByName(_sellQueue.Dequeue());
        }

        private int GetSlotsInUse(
            CurrencyExchangePanel currencyExchangePanel)
        {
            try
            {
                return currencyExchangePanel?.Orders?.Count ?? 0;
            }
            catch (Exception ex)
            {
                LogError($"Failed to read the placed orders: {ex}");
                return 0;
            }
        }

        private int GetFreeTradeSlots(
            CurrencyExchangePanel currencyExchangePanel)
        {
            return Math.Max(
                0,
                Settings.MaxConcurrentTrades.Value -
                GetSlotsInUse(currencyExchangePanel));
        }

        /// <summary>
        /// A placed order has something to pick up when wanted currency has
        /// accrued, or when the order is done (completed with a leftover from
        /// ratio rounding, or canceled) and offered items await pickup in the
        /// selling slot.
        /// </summary>
        private static bool IsOrderCollectible(
            PlacedCurrencyExchangeOrder order)
        {
            if (order == null)
                return false;

            return order.WantedItemStackSize > 0 ||
                   ((order.IsCompleted || order.IsCanceled) &&
                    order.OfferedItemStackSize > 0);
        }

        private int GetCollectibleOrderCount(
            CurrencyExchangePanel currencyExchangePanel)
        {
            try
            {
                return currencyExchangePanel?
                           .Orders?
                           .Count(IsOrderCollectible)
                       ?? 0;
            }
            catch (Exception ex)
            {
                LogError($"Failed to read the collectible orders: {ex}");
                return 0;
            }
        }

        private void StartCollectSequence()
        {
            if (_sellSequenceStep != SellSequenceStep.Idle ||
                _sellQueue.Count > 0 ||
                _collectSequenceActive)
            {
                return;
            }

            BeginInputSession();

            _collectSequenceActive = true;
            _nextCollectActionUtc = DateTime.MinValue;
            _lastCollectOrderId = -1;
            _lastCollectRemaining = -1;
            _collectRetryCount = 0;

            AddDebugMessage("Started the collect sequence.");
        }

        private void StopCollectSequence(string reason)
        {
            if (!_collectSequenceActive)
                return;

            _collectSequenceActive = false;

            AddDebugMessage($"The collect sequence stopped: {reason}.");
        }

        /// <summary>
        /// Collects one order per pass, verifying inventory space first. The
        /// configured collect delay applies with and without InputHumanizer
        /// because of trade rate limits, and collection waits while the
        /// currency picker covers the orders area. Aborts when repeated clicks
        /// on the same order change nothing.
        /// </summary>
        private void ProcessCollectSequence(
            CurrencyExchangePanel currencyExchangePanel)
        {
            if (!_collectSequenceActive)
                return;

            if (IsGameInputBusy())
                return;

            if (DateTime.UtcNow < _nextCollectActionUtc)
                return;

            if (currencyExchangePanel.CurrencyPicker?.IsVisible == true)
                return;

            List<PlacedCurrencyExchangeOrder> orders;
            List<CurrencyExchangePanelOrderElement> orderElements;

            try
            {
                orders = currencyExchangePanel.Orders;
                orderElements = currencyExchangePanel.OrderElements;
            }
            catch (Exception ex)
            {
                LogError($"Failed to read the placed orders: {ex}");
                StopCollectSequence("the orders could not be read");
                return;
            }

            if (orders == null || orderElements == null)
            {
                StopCollectSequence("the orders are unavailable");
                return;
            }

            var inventory = GetMainInventory();

            if (inventory == null)
            {
                LogError(
                    "The player inventory is unavailable; " +
                    "collection space cannot be verified.");

                StopCollectSequence("the player inventory is unavailable");
                return;
            }

            var freeInventoryCells = GetFreeInventoryCells(inventory);
            var skippedForInventorySpace = false;

            var orderCount = Math.Min(orders.Count, orderElements.Count);

            for (var index = 0; index < orderCount; index++)
            {
                var order = orders[index];

                if (!IsOrderCollectible(order))
                    continue;

                var buyingSide = order.WantedItemStackSize > 0;

                var collectItemType = buyingSide
                    ? order.WantedItemType
                    : order.OfferedItemType;

                var collectAmount = buyingSide
                    ? order.WantedItemStackSize
                    : order.OfferedItemStackSize;

                var requiredCells = GetRequiredInventoryCells(
                    inventory,
                    collectItemType,
                    collectAmount);

                if (requiredCells > freeInventoryCells)
                {
                    AddDebugMessage(
                        $"Not enough inventory space for order " +
                        $"{order.PlayerOrderId} " +
                        $"({collectItemType?.BaseName} x{collectAmount}): " +
                        $"{requiredCells} cell(s) needed, " +
                        $"{freeInventoryCells} free; skipping it.");

                    skippedForInventorySpace = true;
                    continue;
                }

                var collectSlot = FindOrderCollectSlot(
                    orderElements[index],
                    buyingSide);

                var collectSlotRect =
                    collectSlot?.GetClientRect() ?? default;

                if (collectSlot?.IsVisible != true ||
                    collectSlotRect.Width <= 0 ||
                    collectSlotRect.Height <= 0)
                {
                    continue;
                }

                var remaining = buyingSide
                    ? order.WantedItemStackSize
                    : order.OfferedItemStackSize;

                if (order.PlayerOrderId == _lastCollectOrderId &&
                    remaining == _lastCollectRemaining)
                {
                    _collectRetryCount++;

                    if (_collectRetryCount >= 3)
                    {
                        LogError(
                            "Collecting is not making progress; stopping.");

                        StopCollectSequence("no progress was being made");
                        return;
                    }
                }
                else
                {
                    _lastCollectOrderId = order.PlayerOrderId;
                    _lastCollectRemaining = remaining;
                    _collectRetryCount = 0;
                }

                if (QueueGameCtrlRightClick(collectSlotRect.Center))
                {
                    AddDebugMessage(
                        $"Collecting order {order.PlayerOrderId} " +
                        $"({collectItemType?.BaseName} x{collectAmount}).");

                    _nextCollectActionUtc =
                        DateTime.UtcNow.AddMilliseconds(
                            Settings.CollectDelayMilliseconds.Value);
                }

                return;
            }

            if (skippedForInventorySpace)
            {
                LogError(
                    "There is not enough inventory space to collect " +
                    "the remaining orders; stopping.");

                StopCollectSequence("there is not enough inventory space");
                return;
            }

            StopCollectSequence("all orders were collected");
        }

        private ServerInventory GetMainInventory()
        {
            try
            {
                return GameController?
                    .Game?
                    .IngameState?
                    .Data?
                    .ServerData?
                    .PlayerInventories?
                    .FirstOrDefault()?
                    .Inventory;
            }
            catch (Exception ex)
            {
                LogError($"Failed to read the player inventory: {ex}");
                return null;
            }
        }

        private static int GetFreeInventoryCells(ServerInventory inventory)
        {
            if (inventory == null ||
                inventory.Rows <= 0 ||
                inventory.Columns <= 0)
            {
                return 0;
            }

            var occupied = new bool[inventory.Rows, inventory.Columns];

            foreach (var slotItem in inventory.InventorySlotItems ?? [])
            {
                if (slotItem == null)
                    continue;

                var startX = Math.Max(0, slotItem.PosX);
                var startY = Math.Max(0, slotItem.PosY);

                var endX = Math.Min(
                    inventory.Columns,
                    slotItem.PosX + slotItem.SizeX);

                var endY = Math.Min(
                    inventory.Rows,
                    slotItem.PosY + slotItem.SizeY);

                for (var y = startY; y < endY; y++)
                for (var x = startX; x < endX; x++)
                    occupied[y, x] = true;
            }

            var freeCells = 0;

            for (var y = 0; y < inventory.Rows; y++)
            for (var x = 0; x < inventory.Columns; x++)
                if (!occupied[y, x])
                    freeCells++;

            return freeCells;
        }

        /// <summary>
        /// Returns the inventory cells needed to fit the given amount of a
        /// currency, counting free space in existing partial stacks of the
        /// same type first. Currency items occupy one cell each.
        /// </summary>
        private static int GetRequiredInventoryCells(
            ServerInventory inventory,
            BaseItemType itemType,
            int amount)
        {
            if (itemType == null || amount <= 0)
                return 0;

            var maxStackSize = 0;
            var partialStackCapacity = 0;

            foreach (var slotItem in inventory?.InventorySlotItems ?? [])
            {
                var itemEntity = slotItem?.Item;

                if (itemEntity?.Path != itemType.Metadata)
                    continue;

                var stack = itemEntity.GetComponent<Stack>();
                var itemMaxStackSize = stack?.Info?.MaxStackSize ?? 0;

                if (itemMaxStackSize <= 0)
                    continue;

                maxStackSize = itemMaxStackSize;

                partialStackCapacity +=
                    Math.Max(0, itemMaxStackSize - stack.Size);
            }

            if (maxStackSize <= 0)
            {
                try
                {
                    maxStackSize = itemType.CurrencyInfo?.MaxStackSize ?? 0;
                }
                catch
                {
                }
            }

            if (maxStackSize <= 0)
                maxStackSize = DefaultCurrencyMaxStackSize;

            var remainingAmount = amount - partialStackCapacity;

            if (remainingAmount <= 0)
                return 0;

            return (remainingAmount + maxStackSize - 1) / maxStackSize;
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

        private void SetSellSequenceStep(SellSequenceStep step)
        {
            _sellSequenceStep = step;
            _sellSequenceStepStartedUtc = DateTime.UtcNow;

            AddDebugMessage($"Sell sequence step: {step}");
        }

        /// <summary>
        /// Finishes the current item but keeps the batch alive so the next
        /// queued item can start.
        /// </summary>
        private void CompleteCurrentSellItem()
        {
            _sellSequenceStep = SellSequenceStep.Idle;

            _pendingSellItem = null;
            _pendingWantedItem = null;

            _pendingSellItemName = string.Empty;
            _pendingSellOwnedAmount = 0;
            _pendingMarketRatio = null;
        }

        /// <summary>
        /// Hard stop: aborts the current item and drops the remaining queue.
        /// <see cref="EndInputSessionIfNeeded"/> then restores the mouse and
        /// releases the InputHumanizer controller.
        /// </summary>
        private void StopSellSequence()
        {
            CompleteCurrentSellItem();
            _sellQueue.Clear();
        }

        private TimeSpan TimeInCurrentStep()
        {
            return DateTime.UtcNow - _sellSequenceStepStartedUtc;
        }

        private bool IsGameInputBusy()
        {
            return _inputTask != null;
        }

        /// <summary>
        /// Converts a window-relative position to a screen position by adding
        /// the game window's top-left offset.
        /// </summary>
        private NumVector2 ToScreenPosition(Vector2 windowRelativePosition)
        {
            return (windowRelativePosition +
                    GameController.Window
                        .GetWindowRectangleReal()
                        .Location)
                .ToVector2Num();
        }

        private bool QueueGameTextReplacement(string text)
        {
            if (text == null)
                return false;

            return QueueGameInput(
                "Game keyboard input failed",
                async () =>
                {
                    var typed = await Input.ReplaceText(text);

                    AddDebugMessage(
                        $"Replaced the input text with \"{text}\": {typed}");

                    return typed;
                });
        }

        private bool QueueGameClick(Vector2 windowRelativePosition)
        {
            var targetPosition = ToScreenPosition(windowRelativePosition);

            return QueueGameInput(
                "Game click failed",
                async () =>
                {
                    var clicked = await Input.Click(targetPosition);

                    AddDebugMessage(
                        $"Clicked the game window: {clicked}, " +
                        $"position: {targetPosition}");

                    return clicked;
                });
        }

        private bool QueueGameCtrlRightClick(Vector2 windowRelativePosition)
        {
            var targetPosition = ToScreenPosition(windowRelativePosition);

            return QueueGameInput(
                "Game Ctrl+right-click failed",
                async () =>
                {
                    var clicked = await Input.CtrlRightClick(targetPosition);

                    AddDebugMessage(
                        $"Ctrl+right-clicked the game window: {clicked}, " +
                        $"position: {targetPosition}");

                    return clicked;
                });
        }

        private bool QueueGameMove(Vector2 windowRelativePosition)
        {
            var targetPosition = ToScreenPosition(windowRelativePosition);

            return QueueGameInput(
                "Game mouse move failed",
                async () =>
                {
                    var moved = await Input.MoveMouse(targetPosition);

                    AddDebugMessage(
                        $"Moved the mouse: {moved}, " +
                        $"position: {targetPosition}");

                    return moved;
                });
        }

        /// <summary>
        /// Starts the input as a <see cref="SyncTask{T}"/> that
        /// <see cref="Tick"/> pumps to completion;
        /// <see cref="IsGameInputBusy"/> reports true until then. Returns
        /// false when another input is still running.
        /// </summary>
        private bool QueueGameInput(
            string errorMessage,
            Func<SyncTask<bool>> inputTaskFactory)
        {
            if (_inputTask != null)
                return false;

            _inputTask = RunGuardedInput(errorMessage, inputTaskFactory);

            return true;
        }

        private async SyncTask<bool> RunGuardedInput(
            string errorMessage,
            Func<SyncTask<bool>> inputTaskFactory)
        {
            try
            {
                return await inputTaskFactory();
            }
            catch (Exception ex)
            {
                LogError($"{errorMessage}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Calculates the wanted-currency amount to request for the offered
        /// amount at the given ratio, scaled by the listing price percentage
        /// and rounded down to a minimum of 1.
        /// </summary>
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

            var priceMultiplier = listingPricePercent / 100d;

            var exactWantedAmount =
                offeredAmount * (marketRateGet * priceMultiplier) /
                marketRateGive;

            return Math.Max(1, (int)Math.Floor(exactWantedAmount));
        }

        private bool TryGetNinjaValue(
            CurrencyExchangeCurrencyPickerCurrencyOption option,
            out double chaosValue)
        {
            chaosValue = 0;

            var itemType = option?.ItemType;

            if (itemType == null)
                return false;

            _getNinjaBaseItemTypeValue ??=
                GameController.PluginBridge
                    .GetMethod<Func<BaseItemType, double>>(
                        "NinjaPrice.GetBaseItemTypeValue");

            if (_getNinjaBaseItemTypeValue == null)
            {
                LogError("NinjaPrice.GetBaseItemTypeValue is unavailable.");
                return false;
            }

            try
            {
                chaosValue = _getNinjaBaseItemTypeValue(itemType);
                return true;
            }
            catch (Exception ex)
            {
                LogError(
                    $"Failed to retrieve the NinjaPrice value for " +
                    $"{GetItemName(option)}: {ex}");

                return false;
            }
        }

        private double GetTotalNinjaValue(
            CurrencyExchangeCurrencyPickerCurrencyOption item)
        {
            if (item == null)
                return 0;

            return TryGetNinjaValue(item, out var unitValue)
                ? Math.Floor(unitValue * item.Owned)
                : 0;
        }

        /// <summary>
        /// Reads market ratios directly from the exchange panel memory (like
        /// MarketWizard) instead of hovering the ratio tooltip.
        /// </summary>
        /// <remarks>
        /// <c>OfferedItemStock</c> holds the competing listings of the offered
        /// item: <c>Give</c> is the wanted amount they ask, <c>Get</c> is the
        /// offered amount they list, and <c>ListedCount</c> is their stock in
        /// offered-item units. The first <c>WantedItemStock</c> entry always
        /// matches <c>MarketRateGet:MarketRateGive</c>.
        /// </remarks>
        private MarketRatio GetMarketRatioForPendingExchange(
            CurrencyExchangePanel currencyExchangePanel,
            int neededAmount)
        {
            if (currencyExchangePanel == null)
                return null;

            var marketRateRatio = new MarketRatio
            {
                MarketGetRate = currencyExchangePanel.MarketRateGet,
                MarketGiveRate = currencyExchangePanel.MarketRateGive,
                AvailableTrades = 0
            };

            if (!Settings.UseHighestCompetingRatio)
                return marketRateRatio;

            var competingRatios =
                (currencyExchangePanel.OfferedItemStock ?? [])
                .Where(stock =>
                    stock != null &&
                    stock.Get > 0 &&
                    stock.Give > 0 &&
                    stock.ListedCount > 0)
                .Select(stock => new MarketRatio
                {
                    MarketGetRate = stock.Give,
                    MarketGiveRate = stock.Get,
                    AvailableTrades = stock.ListedCount
                })
                .OrderByDescending(ratio =>
                    (double)ratio.MarketGetRate / ratio.MarketGiveRate)
                .ToList();

            if (Settings.Debug)
            {
                AddDebugMessage(
                    $"Competing ratios: " +
                    $"{JsonConvert.SerializeObject(competingRatios)}");
            }

            if (Settings.RequireSufficientStock)
            {
                competingRatios = competingRatios
                    .Where(ratio => ratio.AvailableTrades >= neededAmount)
                    .ToList();
            }

            var bestCompetingRatio = competingRatios.FirstOrDefault();

            if (bestCompetingRatio == null)
            {
                AddDebugMessage(
                    "No competing ratio qualifies; falling back " +
                    "to the current market rate.");

                return marketRateRatio;
            }

            return bestCompetingRatio;
        }

        /// <summary>
        /// Records a debug message for the debug window, keeping only the most
        /// recent <see cref="MaxDebugMessages"/> entries.
        /// </summary>
        private void AddDebugMessage(string message)
        {
            _debugMessages.Add(message);

            if (_debugMessages.Count > MaxDebugMessages)
                _debugMessages.RemoveAt(0);
        }

        /// <summary>
        /// Draws the sequence state and recent debug messages. Exceptions are
        /// swallowed so a debug-draw failure never breaks <see cref="Render"/>.
        /// </summary>
        private void DrawDebugWindow()
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
                ImGui.Text($"Input in progress: {IsGameInputBusy()}");
                ImGui.Text($"Sell queue: {_sellQueue.Count}");
                ImGui.Text($"Collect sequence active: {_collectSequenceActive}");
                ImGui.Text($"Input session active: {_inputSessionActive}");

                ImGui.Separator();

                foreach (var message in _debugMessages)
                {
                    ImGui.NewLine();
                    ImGui.TextWrapped(message);
                }
            }
            catch
            {
            }
        }
    }
}
