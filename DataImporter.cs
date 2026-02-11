using System;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using Z.EntityFramework.Extensions;

namespace portaBLe
{
    public static class DataImporter
    {
        public static void ImportData(BigExportResponse export, AppContext dbContext)
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            
            // Disable WAL
            dbContext.Database.ExecuteSql($"PRAGMA journal_mode=OFF;");
            dbContext.Database.ExecuteSql($"PRAGMA synchronous=OFF;");

            var leaderboards = export.Maps.Select(map => new Leaderboard
            {
                Id = map.Id,
                Name = map.Name,
                Hash = map.Hash,
                SongId = map.SongId,
                ModeName = map.ModeName,
                DifficultyName = map.DifficultyName,
                PassRating = map.PassRating ?? 0,
                AccRating = map.AccRating ?? 0,
                TechRating = map.TechRating ?? 0,
                PredictedAcc = map.PredictedAcc ?? 0,
                ModifiersRating = map.ModifiersRating?.ToDBModel(),
                Cover = map.CoverImage,
                Mapper = map.Mapper,
                Stars = ReplayUtils.ToStars(map.AccRating ?? 0, map.PassRating ?? 0, map.TechRating ?? 0)
            });

            dbContext.Leaderboards.BulkInsertOptimized(leaderboards, options => options.IncludeGraph = true);

            var players = export.Players.Select(player => new Player
            {
                Id = player.Id,
                Name = player.Name,
                Country = player.Country,
                Avatar = player.Avatar,
            });
            
            dbContext.Players.BulkInsertOptimized(players);

            var scores = export.Scores.Select(score => new Score
            {
                Id = score.Id,
                PlayerId = score.PlayerId,
                Timepost = score.Timepost,
                LeaderboardId = score.LeaderboardId,
                Accuracy = score.Accuracy,
                Modifiers = score.Modifiers,
                FC = score.FC,
                FCAcc = score.FCAcc
            });
            
            dbContext.Scores.BulkInsertOptimized(scores);
        }
    }
}
