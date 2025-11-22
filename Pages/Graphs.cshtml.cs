using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Xml.Linq;

namespace portaBLe.Pages
{
    public class GraphsModel : PageModel
    {
        public List<LinkItem> Links { get; set; }

        public void OnGet()
        {
            // Add your page links here
            Links = new List<LinkItem>
            {
                new LinkItem { Title = "Weighted Scores Graph", Url = "/WeightedScoresGraph" },
                new LinkItem { Title = "Unweighted Scores Graph", Url = "/UnweightedScoresGraph" },
                new LinkItem { Title = "Players PP 3D Graph", Url = "/PlayersPPGraph" },
                new LinkItem { Title = "Players Balancing Graphs", Url = "/PlayersBalancingGraphs" }
            };
        }

        public class LinkItem
        {
            public string Title { get; set; }
            public string Url { get; set; }
        }
    }
}
