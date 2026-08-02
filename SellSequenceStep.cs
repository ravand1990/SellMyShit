enum SellSequenceStep
{
    Idle,

    Start,
    ClickIHave,
    WaitForOfferedCurrencyPicker,
    ClickCurrencyPickerOfferedSearchInput,
    TypeCurrencyPickerOfferedSearchQuery,
    ValidateOfferedSearchQuery,
    WaitForCurrencyPickerOfferedSearchResults,
    ClickOwnedItem,
    WaitForOfferedCurrencyPickerToClose,
    ClickOfferedItemInput,
    TypeOfferedItemValue,


    CheckIfChaosIsWanted,
    ClickIWant,
    WaitForWantedCurrencyPicker,
    ClickCurrencyPickerWantedSearchInput,
    TypeCurrencyPickerWantedSearchQuery,
    ValidatedWantedSearchQuery,
    WaitForCurrencyPickerWantedSearchResults,
    ClickWantedItem,
    WaitForWantedCurrencyPickerToClose,


    ClickWantedItemInput,
    TypeWantedItemValue,
    BlurInput,

    ShowMarketRatioTooltip,
    ShowDetailedMarketRatioTooltip,
    WaitForDetailedMarketRatioInfo,
    WaitForMarketRatio,



    CheckIfSellButtonIsActive,
    ClickSellButton,
    CheckUnfavorableTrade,
    ReopenIHave,
    End,
    CloseCurrencyPicker,

}
