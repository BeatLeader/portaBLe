using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace portaBLe.MapRecommendation.Ranked
{
    public static class HtmlExporter
    {
        private static string GetExportDirectory()
        {
            string exportDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "exports");
            if (!Directory.Exists(exportDir))
            {
                Directory.CreateDirectory(exportDir);
            }
            return exportDir;
        }

        public static void ExportList<T>(string filename, List<T> data, List<string> columnHeaders = null)
        {
            try
            {
                string exportDir = GetExportDirectory();
                string filePath = Path.Combine(exportDir, $"{filename}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

                var properties = typeof(T).GetProperties();
                
                if (columnHeaders == null)
                {
                    columnHeaders = properties.Select(p => p.Name).ToList();
                }

                var html = new StringBuilder();
                html.AppendLine("<!DOCTYPE html>");
                html.AppendLine("<html>");
                html.AppendLine("<head>");
                html.AppendLine("<meta charset=\"UTF-8\">");
                html.AppendLine($"<title>{filename}</title>");
                html.AppendLine("<style>");
                html.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; }");
                html.AppendLine("h1 { color: #333; }");
                html.AppendLine("table { border-collapse: collapse; width: 100%; background-color: white; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
                html.AppendLine("th { background-color: #2196F3; color: white; padding: 12px; text-align: left; font-weight: bold; }");
                html.AppendLine("td { padding: 10px 12px; border-bottom: 1px solid #ddd; }");
                html.AppendLine("tr:nth-child(even) { background-color: #f9f9f9; }");
                html.AppendLine("tr:hover { background-color: #f0f0f0; }");
                html.AppendLine(".info { margin-bottom: 20px; padding: 10px; background-color: #e3f2fd; border-left: 4px solid #2196F3; }");
                html.AppendLine("</style>");
                html.AppendLine("</head>");
                html.AppendLine("<body>");
                html.AppendLine($"<h1>{filename}</h1>");
                html.AppendLine($"<div class=\"info\"><strong>Generated:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss} | <strong>Records:</strong> {data.Count}</div>");
                html.AppendLine("<table>");
                
                // Header
                html.AppendLine("<thead><tr>");
                foreach (var header in columnHeaders)
                {
                    html.AppendLine($"<th>{System.Net.WebUtility.HtmlEncode(header)}</th>");
                }
                html.AppendLine("</tr></thead>");
                
                // Body
                html.AppendLine("<tbody>");
                foreach (var item in data)
                {
                    html.AppendLine("<tr>");
                    foreach (var prop in properties)
                    {
                        var value = prop.GetValue(item);
                        string displayValue = value?.ToString() ?? "";
                        
                        // Format numbers
                        if (value is float f)
                            displayValue = f.ToString("F4");
                        else if (value is double d)
                            displayValue = d.ToString("F4");
                        
                        html.AppendLine($"<td>{System.Net.WebUtility.HtmlEncode(displayValue)}</td>");
                    }
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</tbody>");
                html.AppendLine("</table>");
                html.AppendLine("</body>");
                html.AppendLine("</html>");

                File.WriteAllText(filePath, html.ToString());
                Console.WriteLine($"[HtmlExporter] Exported to: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HtmlExporter] Error exporting {filename}: {ex.Message}");
            }
        }

        public static void ExportDictionary<TKey, TValue>(string filename, Dictionary<TKey, TValue> data)
        {
            try
            {
                string exportDir = GetExportDirectory();
                string filePath = Path.Combine(exportDir, $"{filename}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

                var html = new StringBuilder();
                html.AppendLine("<!DOCTYPE html>");
                html.AppendLine("<html>");
                html.AppendLine("<head>");
                html.AppendLine("<meta charset=\"UTF-8\">");
                html.AppendLine($"<title>{filename}</title>");
                html.AppendLine("<style>");
                html.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; }");
                html.AppendLine("h1 { color: #333; }");
                html.AppendLine("table { border-collapse: collapse; width: 100%; background-color: white; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
                html.AppendLine("th { background-color: #2196F3; color: white; padding: 12px; text-align: left; font-weight: bold; }");
                html.AppendLine("td { padding: 10px 12px; border-bottom: 1px solid #ddd; }");
                html.AppendLine("tr:nth-child(even) { background-color: #f9f9f9; }");
                html.AppendLine("tr:hover { background-color: #f0f0f0; }");
                html.AppendLine(".info { margin-bottom: 20px; padding: 10px; background-color: #e3f2fd; border-left: 4px solid #2196F3; }");
                html.AppendLine("</style>");
                html.AppendLine("</head>");
                html.AppendLine("<body>");
                html.AppendLine($"<h1>{filename}</h1>");
                html.AppendLine($"<div class=\"info\"><strong>Generated:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss} | <strong>Records:</strong> {data.Count}</div>");
                html.AppendLine("<table>");
                
                // Header
                html.AppendLine("<thead><tr><th>Key</th><th>Value</th></tr></thead>");
                
                // Body
                html.AppendLine("<tbody>");
                foreach (var kvp in data.OrderByDescending(x => x.Value))
                {
                    html.AppendLine("<tr>");
                    html.AppendLine($"<td>{System.Net.WebUtility.HtmlEncode(kvp.Key.ToString())}</td>");
                    
                    string valueStr = kvp.Value.ToString();
                    if (kvp.Value is float f)
                        valueStr = f.ToString("F4");
                    else if (kvp.Value is double d)
                        valueStr = d.ToString("F4");
                    
                    html.AppendLine($"<td>{System.Net.WebUtility.HtmlEncode(valueStr)}</td>");
                    html.AppendLine("</tr>");
                }
                html.AppendLine("</tbody>");
                html.AppendLine("</table>");
                html.AppendLine("</body>");
                html.AppendLine("</html>");

                File.WriteAllText(filePath, html.ToString());
                Console.WriteLine($"[HtmlExporter] Exported to: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HtmlExporter] Error exporting {filename}: {ex.Message}");
            }
        }
    }
}
