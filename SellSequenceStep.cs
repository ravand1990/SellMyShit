namespace SellMyShit
{
    /// <summary>
    /// Ordered states of the automated sell sequence. <c>Core.ProcessSellSequence</c>
    /// advances through these states one game action per frame, re-validating the
    /// live UI before every input.
    /// </summary>
    public enum SellSequenceStep
    {
        /// <summary>No sequence is running.</summary>
        Idle,

        /// <summary>Entry point; routes based on whether the currency picker is already open.</summary>
        Start,

        /// <summary>Clicks the "I Have" currency select button to open the offered-side picker.</summary>
        ClickIHave,

        /// <summary>Waits for the offered-side currency picker to become visible.</summary>
        WaitForOfferedCurrencyPicker,

        /// <summary>Clicks the picker search input on the offered side.</summary>
        ClickCurrencyPickerOfferedSearchInput,

        /// <summary>Types the offered item name into the picker search input.</summary>
        TypeCurrencyPickerOfferedSearchQuery,

        /// <summary>Confirms the search input now contains the offered item name.</summary>
        ValidateOfferedSearchQuery,

        /// <summary>Waits for the picker to list the offered item, then resolves its option element.</summary>
        WaitForCurrencyPickerOfferedSearchResults,

        /// <summary>Clicks the offered item in the picker result list.</summary>
        ClickOwnedItem,

        /// <summary>Waits for the offered-side picker to close after the selection.</summary>
        WaitForOfferedCurrencyPickerToClose,

        /// <summary>Skips the wanted-side selection when the wanted currency is already chosen.</summary>
        CheckIfChaosIsWanted,

        /// <summary>Clicks the "I Want" currency select button to open the wanted-side picker.</summary>
        ClickIWant,

        /// <summary>Waits for the wanted-side currency picker to become visible.</summary>
        WaitForWantedCurrencyPicker,

        /// <summary>Clicks the picker search input on the wanted side.</summary>
        ClickCurrencyPickerWantedSearchInput,

        /// <summary>Types the wanted currency name into the picker search input.</summary>
        TypeCurrencyPickerWantedSearchQuery,

        /// <summary>Confirms the search input now contains the wanted currency name.</summary>
        ValidateWantedSearchQuery,

        /// <summary>Waits for the picker to list the wanted currency, then resolves its option element.</summary>
        WaitForCurrencyPickerWantedSearchResults,

        /// <summary>Clicks the wanted currency in the picker result list.</summary>
        ClickWantedItem,

        /// <summary>Waits for the wanted-side picker to close after the selection.</summary>
        WaitForWantedCurrencyPickerToClose,

        /// <summary>Waits until the panel exposes a usable market ratio for the pair.</summary>
        WaitForMarketRatio,

        /// <summary>Clicks the offered item count input.</summary>
        ClickOfferedItemInput,

        /// <summary>Types the owned amount into the offered item count input.</summary>
        TypeOfferedItemValue,

        /// <summary>Clicks the wanted item count input.</summary>
        ClickWantedItemInput,

        /// <summary>Calculates and types the requested amount into the wanted item count input.</summary>
        TypeWantedItemValue,

        /// <summary>Clicks the ratio display to take keyboard focus off the amount inputs.</summary>
        BlurInput,

        /// <summary>Waits until the game enables the sell button for the entered amounts.</summary>
        CheckIfSellButtonIsActive,

        /// <summary>Clicks the sell button to place the order.</summary>
        ClickSellButton,

        /// <summary>Placeholder — the unfavorable-trade confirmation dialog is not handled yet.</summary>
        CheckUnfavorableTrade,

        /// <summary>Reopens the offered-side picker so a queued follow-up item can start immediately.</summary>
        ReopenIHave,

        /// <summary>Finishes the current item and hands control back to the queue.</summary>
        End
    }
}
