#nullable enable

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// Shared test infrastructure for drydock integration tests.
    /// </summary>
    internal static class DrydockTestHelpers
    {
        /// <summary>
        /// The owner column is a real foreign key, so a ship cannot be filed for a player who does
        /// not exist. That is the intended behaviour, and it means a test has to supply one.
        /// </summary>
        internal static Task InsertPlayer(IServerDbManager db, Guid userId)
        {
            return db.RunTriadDbCommand(async (context, token) =>
            {
                context.Player.Add(new Player
                {
                    UserId = userId,
                    LastSeenUserName = $"drydock-test-{userId:N}",
                    FirstSeenTime = DateTime.UtcNow,
                    LastSeenTime = DateTime.UtcNow,
                    LastSeenAddress = IPAddress.Loopback,
                });

                await context.SaveChangesAsync(token);
            }, CancellationToken.None);
        }
    }
}
