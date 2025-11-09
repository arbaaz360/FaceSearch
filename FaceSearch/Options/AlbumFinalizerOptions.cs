namespace FaceSearch.Options.Config
{
    public sealed class AlbumFinalizerOptions
    {
        public double LinkThreshold { get; set; } = 0.45;    // T_LINK
        public int TopK { get; set; } = 50;                   // TOPK
        public double AggregatorThreshold { get; set; } = 0.50; // AGG_THRESHOLD
    }
}
