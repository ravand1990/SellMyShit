enum SellSequenceStep
{
    Idle,

    Start,
    ClickIHave,
    WaitForCurrencyPicker,
    ClickCurrencyPickerSearchInput,
    TypeCurrencyPickerSearchQuery,
    ValidateSearchQuery,
    WaitForCurrencyPickerSearchResults,
    ClickOwnedItem,
    WaitForCurrencyPickerToClose,
    ClickOfferedItemInput,
    TypeOfferedItemValue,
    ClickWantedItemInput,
    TypeWantedItemValue,
    WaitForMarketRatio,
    CheckUnfavorableTrade,
    ClickSellButton,
    ReopenIHave,
    End,
    CloseCurrencyPicker,

}
