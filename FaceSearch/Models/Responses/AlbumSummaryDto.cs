namespace FaceSearch.Models.Responses
{
    public class AlbumSummaryDto
    {
        public string AlbumId { get; set; } = string.Empty;
        public string? DominantPerson { get; set; }
        public int PeopleCount { get; set; }
    }
}
