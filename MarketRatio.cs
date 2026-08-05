namespace SellMyShit
{
    /// <summary>
    /// An exchange ratio between the offered and the wanted currency, either the
    /// panel's own market rate or one competing listing read from panel memory.
    /// </summary>
    public class MarketRatio
    {
        /// <summary>Units of the offered currency given per <see cref="MarketGetRate"/> units received.</summary>
        public int MarketGiveRate { get; set; }

        /// <summary>Units of the wanted currency received per <see cref="MarketGiveRate"/> units given.</summary>
        public int MarketGetRate { get; set; }

        /// <summary>
        /// Stock available at this ratio in offered-item units; 0 when the ratio is
        /// the panel's own market rate rather than a competing listing.
        /// </summary>
        public int AvailableTrades { get; set; }
    }
}
