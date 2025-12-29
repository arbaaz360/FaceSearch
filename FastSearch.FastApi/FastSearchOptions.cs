namespace FastSearch.FastApi;

public sealed class FastSearchOptions
{
    public string Collection { get; set; } = "faces_fast_512";
    public int DefaultTopK { get; set; } = 20;
    public string JobDirectory { get; set; } = ".fast-jobs";
}
