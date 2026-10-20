using Microsoft.AspNetCore.Mvc.RazorPages;
using portaBLe.Refresh;

namespace portaBLe.Pages
{
    // Shows wwwroot/pass-window-check.json, written by PassWindowCheck.Run (called from Program.Main). The ranks are computed
    // in the browser, so they follow the filters.
    public class PassRatingComparisonModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        public PassRatingComparisonModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        public string? DataJson { get; set; }

        public void OnGet()
        {
            var path = Path.Combine(_env.WebRootPath, PassWindowCheck.ResultFile);
            if (System.IO.File.Exists(path)) DataJson = System.IO.File.ReadAllText(path);
        }
    }
}
