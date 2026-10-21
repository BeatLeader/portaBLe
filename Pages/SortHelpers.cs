using System.Linq.Expressions;

namespace portaBLe.Pages
{
    public static class SortHelpers
    {
        // Sorted by a nullable key, the rows without a value last either way
        public static IQueryable<T> NullsLast<T>(IQueryable<T> query, Expression<Func<T, float?>> key, bool descending)
        {
            var isNull = Expression.Lambda<Func<T, bool>>(Expression.Equal(key.Body, Expression.Constant(null, typeof(float?))), key.Parameters);
            var withNullsLast = query.OrderBy(isNull);
            return descending ? withNullsLast.ThenByDescending(key) : withNullsLast.ThenBy(key);
        }
    }
}
