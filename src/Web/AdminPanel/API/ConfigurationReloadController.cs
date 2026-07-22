// <copyright file="ConfigurationReloadController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.API;

using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Reloads the safely hot-swappable parts of the game configuration from PostgreSQL.
/// </summary>
[ApiController]
[Route("api/internal/configuration")]
public sealed class ConfigurationReloadController : ControllerBase
{
    private const string ApiKeyHeader = "X-Mu-Gateway-Api-Key";
    private static readonly HashSet<string> AllowedScopes = new(["spawns", "shops"], StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ReloadLock = new(1, 1);
    private readonly IDictionary<int, IGameServer> _gameServers;
    private readonly IPersistenceContextProvider _persistenceContextProvider;
    private readonly IConfigurationChangeMediatorListener _changeMediator;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationReloadController"/> class.
    /// </summary>
    public ConfigurationReloadController(
        IDictionary<int, IGameServer> gameServers,
        IPersistenceContextProvider persistenceContextProvider,
        IConfigurationChangeMediatorListener changeMediator,
        IConfiguration configuration)
    {
        this._gameServers = gameServers;
        this._persistenceContextProvider = persistenceContextProvider;
        this._changeMediator = changeMediator;
        this._apiKey = configuration["MUMAIN_GATEWAY_API_KEY"] ?? string.Empty;
    }

    /// <summary>
    /// Reloads monster/NPC spawn areas and merchant stores without restarting game servers.
    /// </summary>
    [HttpPost("reload")]
    public async Task<IActionResult> ReloadAsync([FromBody] ConfigurationReloadRequest? request, CancellationToken cancellationToken)
    {
        if (!this.IsAuthorized())
        {
            return this.Unauthorized(new { error = "unauthorized" });
        }

        var scopes = request?.Scopes?.Count > 0
            ? request.Scopes.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["spawns", "shops"], StringComparer.OrdinalIgnoreCase);
        if (scopes.Any(scope => !AllowedScopes.Contains(scope)))
        {
            return this.BadRequest(new { error = "invalid_scope", allowed = new[] { "spawns", "shops" } });
        }

        await ReloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contexts = this._gameServers.Values
                .OfType<IGameServerContextProvider>()
                .Select(provider => provider.Context)
                .GroupBy(context => context.Configuration, ReferenceEqualityComparer.Instance)
                .Select(group => group.First())
                .ToList();

            if (contexts.Count == 0)
            {
                return this.Conflict(new { error = "no_running_game_server" });
            }

            var result = new ConfigurationReloadResult();
            if (scopes.Contains("spawns"))
            {
                using var context = this._persistenceContextProvider.CreateNewTypedContext(typeof(MonsterSpawnArea), false);
                var freshSpawns = (await context.GetAsync<MonsterSpawnArea>(cancellationToken).ConfigureAwait(false)).ToList();
                foreach (var gameContext in contexts)
                {
                    await ReloadSpawnsAsync(gameContext, freshSpawns, this._changeMediator, result).ConfigureAwait(false);
                }
            }

            if (scopes.Contains("shops"))
            {
                using var context = this._persistenceContextProvider.CreateNewTypedContext(typeof(MonsterDefinition), false);
                var freshMonsters = (await context.GetAsync<MonsterDefinition>(cancellationToken).ConfigureAwait(false)).ToList();
                foreach (var gameContext in contexts)
                {
                    await ReloadShopsAsync(gameContext, freshMonsters, this._changeMediator, result).ConfigureAwait(false);
                }
            }

            return this.Ok(result);
        }
        finally
        {
            ReloadLock.Release();
        }
    }

    private static async ValueTask ReloadSpawnsAsync(
        IGameServerContext gameContext,
        IReadOnlyCollection<MonsterSpawnArea> freshSpawns,
        IConfigurationChangeMediatorListener changeMediator,
        ConfigurationReloadResult result)
    {
        var configuration = gameContext.Configuration;
        var liveSpawns = configuration.Maps.SelectMany(map => map.MonsterSpawns).ToDictionary(spawn => spawn.GetId());
        var freshById = freshSpawns.ToDictionary(spawn => spawn.GetId());

        foreach (var removed in liveSpawns.Values.Where(spawn => !freshById.ContainsKey(spawn.GetId())).ToList())
        {
            await changeMediator
                .HandleConfigurationRemovedAsync(typeof(MonsterSpawnArea), removed.GetId())
                .ConfigureAwait(false);
            removed.GameMap?.MonsterSpawns.Remove(removed);
            result.SpawnsRemoved++;
        }

        foreach (var fresh in freshSpawns)
        {
            if (liveSpawns.TryGetValue(fresh.GetId(), out var live))
            {
                if (SpawnSignature(live) == SpawnSignature(fresh))
                {
                    continue;
                }

                live.AssignValuesOf(fresh, configuration);
                await changeMediator
                    .HandleConfigurationChangedAsync(typeof(MonsterSpawnArea), live.GetId(), live)
                    .ConfigureAwait(false);
                result.SpawnsChanged++;
                continue;
            }

            var added = fresh.Clone(configuration);
            if (added.GameMap is null)
            {
                continue;
            }

            added.GameMap.MonsterSpawns.Add(added);
            await changeMediator
                .HandleConfigurationAddedAsync(typeof(MonsterSpawnArea), added.GetId(), added)
                .ConfigureAwait(false);
            result.SpawnsAdded++;
        }
    }

    private static async ValueTask ReloadShopsAsync(
        IGameServerContext gameContext,
        IReadOnlyCollection<MonsterDefinition> freshMonsters,
        IConfigurationChangeMediatorListener changeMediator,
        ConfigurationReloadResult result)
    {
        var configuration = gameContext.Configuration;
        var liveById = configuration.Monsters.ToDictionary(monster => monster.GetId());
        foreach (var freshMonster in freshMonsters)
        {
            if (!liveById.TryGetValue(freshMonster.GetId(), out var liveMonster))
            {
                continue;
            }

            var freshStore = freshMonster.MerchantStore;
            var liveStore = liveMonster.MerchantStore;
            if (freshStore is null)
            {
                if (liveStore is not null)
                {
                    liveMonster.MerchantStore = null;
                    result.ShopsChanged++;
                }

                continue;
            }

            if (liveStore is null || liveStore.GetId() != freshStore.GetId())
            {
                liveMonster.MerchantStore = freshStore.Clone(configuration);
                result.ShopsChanged++;
                continue;
            }

            liveStore.AssignValuesOf(freshStore, configuration);
            await changeMediator
                .HandleConfigurationChangedAsync(typeof(ItemStorage), liveStore.GetId(), liveStore)
                .ConfigureAwait(false);
            result.ShopsChanged++;
        }
    }

    private static string SpawnSignature(MonsterSpawnArea spawn)
    {
        return string.Join(
            ':',
            spawn.MonsterDefinition?.GetId(),
            spawn.GameMap?.GetId(),
            spawn.X1,
            spawn.Y1,
            spawn.X2,
            spawn.Y2,
            (int)spawn.Direction,
            spawn.Quantity,
            (int)spawn.SpawnTrigger,
            spawn.WaveNumber,
            spawn.MaximumHealthOverride);
    }

    private bool IsAuthorized()
    {
        if (string.IsNullOrWhiteSpace(this._apiKey)
            || !this.Request.Headers.TryGetValue(ApiKeyHeader, out var supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(this._apiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
        return expectedBytes.Length == suppliedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

/// <summary>
/// Requested hot-reload scopes. Empty means all supported scopes.
/// </summary>
public sealed record ConfigurationReloadRequest(IReadOnlyCollection<string>? Scopes);

/// <summary>
/// Counts of runtime objects affected by a configuration reload.
/// </summary>
public sealed class ConfigurationReloadResult
{
    public int SpawnsAdded { get; set; }

    public int SpawnsChanged { get; set; }

    public int SpawnsRemoved { get; set; }

    public int ShopsChanged { get; set; }
}
