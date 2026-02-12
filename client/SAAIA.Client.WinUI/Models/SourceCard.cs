namespace SAAIA.Client.WinUI.Models;

public sealed class SourceCard
{
    public string DocPath { get; set; } = "";
    public string DocName { get; set; } = "";
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public string Snippet { get; set; } = "";
    public double? Score { get; set; }

    public string PagesLabel
        => (PageStart is null && PageEnd is null) ? ""
         : (PageStart is not null && PageEnd is null) ? $"p. {PageStart}"
         : (PageStart is null && PageEnd is not null) ? $"p. {PageEnd}"
         : (PageStart == PageEnd) ? $"p. {PageStart}"
         : $"p. {PageStart}–{PageEnd}";

    public string ScoreLabel => Score is null ? "" : $"score {Score:0.###}";
}
