using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using ProtoBuf;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class ExportModel : BasePageModel
    {
        public ExportModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        // Builds a BigExportResponse from the selected database and returns it as a
        // protobuf-serialized, zipped file - the same shape Program.ParseProtobuf reads,
        // so the download can be re-imported later via DataImporter.ImportData.
        public async Task<IActionResult> OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            try
            {
                using var context = (Services.DynamicDbContext)GetDbContext();

                var players = await context.Players
                    .AsNoTracking()
                    .Select(p => new ExportPlayer
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Country = p.Country,
                        Avatar = p.Avatar,
                    })
                    .ToListAsync();

                var scores = await context.Scores
                    .AsNoTracking()
                    .Select(s => new ExportScore
                    {
                        Id = s.Id,
                        LeaderboardId = s.LeaderboardId,
                        Accuracy = s.Accuracy,
                        Modifiers = s.Modifiers,
                        PlayerId = s.PlayerId,
                        Timepost = s.Timepost,
                        FC = s.FC,
                        FCAcc = s.FCAcc,
                    })
                    .ToListAsync();

                var leaderboards = await context.Leaderboards
                    .AsNoTracking()
                    .Include(l => l.ModifiersRating)
                    .ToListAsync();

                var maps = leaderboards.Select(l => new ExportMap
                {
                    Id = l.Id,
                    Hash = l.Hash,
                    Name = l.Name,
                    CoverImage = l.Cover,
                    Mapper = l.Mapper,
                    SongId = l.SongId,
                    ModeName = l.ModeName,
                    DifficultyName = l.DifficultyName,
                    AccRating = l.AccRating,
                    PassRating = l.PassRating,
                    TechRating = l.TechRating,
                    PredictedAcc = l.PredictedAcc,
                    MultiPercentage = l.MultiPercentage,
                    LinearPercent = l.LinearPercent,
                    ParityErrors = l.ParityErrors,
                    BombAvoidances = l.BombAvoidances,
                    ModifiersRating = l.ModifiersRating == null ? null : new ExportModifiersRating
                    {
                        SSPredictedAcc = l.ModifiersRating.SSPredictedAcc,
                        SSPassRating = l.ModifiersRating.SSPassRating,
                        SSAccRating = l.ModifiersRating.SSAccRating,
                        SSTechRating = l.ModifiersRating.SSTechRating,
                        SSStars = l.ModifiersRating.SSStars,
                        FSPredictedAcc = l.ModifiersRating.FSPredictedAcc,
                        FSPassRating = l.ModifiersRating.FSPassRating,
                        FSAccRating = l.ModifiersRating.FSAccRating,
                        FSTechRating = l.ModifiersRating.FSTechRating,
                        FSStars = l.ModifiersRating.FSStars,
                        SFPredictedAcc = l.ModifiersRating.SFPredictedAcc,
                        SFPassRating = l.ModifiersRating.SFPassRating,
                        SFAccRating = l.ModifiersRating.SFAccRating,
                        SFTechRating = l.ModifiersRating.SFTechRating,
                        SFStars = l.ModifiersRating.SFStars,
                    },
                }).ToList();

                var export = new BigExportResponse
                {
                    Players = players,
                    Scores = scores,
                    Maps = maps,
                };

                // Write to a temp file (deleted once the response stream closes) so the
                // serialized + zipped payload is not held entirely in memory.
                var tempPath = Path.GetTempFileName();
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("export.bin", CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    Serializer.Serialize(entryStream, export);
                }

                var resultStream = new FileStream(
                    tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose);

                var fileName = $"export-{Path.GetFileNameWithoutExtension(SelectedDatabase)}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
                return File(resultStream, "application/zip", fileName);
            }
            catch (Exception ex)
            {
                return Content($"Export failed: {ex.Message}", "text/plain");
            }
        }
    }
}
