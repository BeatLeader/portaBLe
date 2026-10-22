using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;

namespace portaBLe.Pages
{
    public class BasePageModel : PageModel
    {
        protected readonly IDynamicDbContextService _dbService;
        
        public List<DatabaseConfig> AvailableDatabases { get; set; }
        public string SelectedDatabase { get; set; }

        public BasePageModel(IDynamicDbContextService dbService)
        {
            _dbService = dbService;
        }

        protected async Task InitializeDatabaseSelectionAsync(string selectedDb = null)
        {
            AvailableDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            // No database asked for, or one that isn't listed or present anymore (an old link): the main database
            SelectedDatabase = _dbService.ResolveDatabaseFileName(selectedDb);
        }

        protected DbContext GetDbContext()
        {
            return _dbService.CreateContext(SelectedDatabase);
        }

        protected DbContext GetDbContext(string dbFileName)
        {
            return _dbService.CreateContext(dbFileName);
        }
    }
}

