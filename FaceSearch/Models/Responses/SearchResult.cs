namespace FaceSearch.Models.Responses
{
    public class SearchItem
    {
        public string AssetId { get; set; } = string.Empty;
        public float Score { get; set; }
    }

    public class SearchResult
    {
        public List<SearchItem> Items { get; set; } = new();
    }
}
