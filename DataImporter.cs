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
        public static void ImportJsonData(RootObject rootObject, AppContext dbContext)
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            
            // Disable WAL
            dbContext.Database.ExecuteSql($"PRAGMA journal_mode=OFF;");
            dbContext.Database.ExecuteSql($"PRAGMA synchronous=OFF;");

            foreach (var item in rootObject.Maps)
            {
                if (item.ModifiersRating != null) {
                    item.ModifiersRating.Id = 0;
                }
            }

            var leaderboards = rootObject.Maps.Select(map => new Leaderboard
            {
                Id = map.Id,
                Name = map.Name,
                Hash = map.Hash,
                SongId = map.SongId,
                ModeName = map.ModeName,
                DifficultyName = map.DifficultyName,
                PassRating = map.PassRating,
                AccRating = map.AccRating,
                TechRating = map.TechRating,
                StaminaRating = map.StaminaRating,
                PredictedAcc = map.PredictedAcc,
                ModifiersRating = map.ModifiersRating,
                Cover = map.CoverImage,
                Mapper = map.Mapper,
                Stars = ReplayUtils.ToStars(map.AccRating, map.PassRating, map.TechRating, map.StaminaRating)
            });

            dbContext.Leaderboards.BulkInsertOptimized(leaderboards, options => options.IncludeGraph = true);

            var players = rootObject.Players.Select(player => new Player
            {
                Id = player.Id,
                Name = player.Name,
                Country = player.Country,
                Avatar = player.Avatar,
            });
            
            dbContext.Players.BulkInsertOptimized(players);

            var scores = rootObject.Scores.Select(score => new Score
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
