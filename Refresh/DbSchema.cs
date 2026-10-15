using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Data;

namespace portaBLe.Refresh
{
    public static class DbSchema
    {
        // Adds a column (DEFAULT 0) only when the table doesn't have it yet. Trying the ALTER TABLE and catching the error
        // worked too, but EF Core logs every failed command as "fail: ... Failed executing DbCommand".
        public static async Task AddColumnIfMissing(AppContext dbContext, string table, string column, string type)
        {
            var connection = dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0) return;
            }
            await dbContext.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD COLUMN {column} {type} DEFAULT 0");
            Console.WriteLine($"{table}.{column} column added.");
        }
    }
}
