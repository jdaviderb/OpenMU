// <copyright file="LiveRewardController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.API;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Internal, authenticated API which delivers gateway rewards to an already connected character.
/// </summary>
[ApiController]
[Route("api/internal/live-rewards")]
public sealed class LiveRewardController : ControllerBase
{
    private const string SignatureHeader = "X-Mu-Gateway-Signature";
    private const string TimestampHeader = "X-Mu-Gateway-Timestamp";
    private const string SignedMethod = "POST";
    private const string SignedPath = "/api/internal/live-rewards/gift-code";
    private const long MaximumClockSkewSeconds = 120;
    private const int MaximumItemCount = 32;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CharacterLocks = new();
    private readonly IDictionary<int, IGameServer> _gameServers;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveRewardController"/> class.
    /// </summary>
    public LiveRewardController(IDictionary<int, IGameServer> gameServers, IConfiguration configuration)
    {
        this._gameServers = gameServers;
        this._apiKey = configuration["MUMAIN_GATEWAY_API_KEY"] ?? string.Empty;
    }

    /// <summary>
    /// Delivers an immutable gift-code reward to the selected online character.
    /// </summary>
    [HttpPost("gift-code")]
    public async Task<IActionResult> DeliverGiftCodeAsync([FromBody] LiveGiftRewardRequest request, CancellationToken cancellationToken)
    {
        if (!this.IsAuthorized(request))
        {
            return this.Unauthorized(new { error = "unauthorized" });
        }

        if (!HasValidEnvelope(request))
        {
            return this.BadRequest(new { error = "invalid_reward_request" });
        }

        if (request.Rewards.Any(reward => !string.Equals(reward.Type, "item", StringComparison.OrdinalIgnoreCase)))
        {
            // Offline delivery remains the safe fallback for Zen until it has its own durable OpenMU receipt.
            return this.UnprocessableEntity(new { error = "live_reward_not_supported" });
        }

        if (!HasValidItemRewards(request))
        {
            return this.BadRequest(new { error = "invalid_reward_request" });
        }

        var player = await this.FindPlayerAsync(request).ConfigureAwait(false);
        if (player?.Inventory is null || player.SelectedCharacter is null)
        {
            return this.Conflict(new { error = "character_offline" });
        }

        var characterLock = CharacterLocks.GetOrAdd(request.CharacterId, _ => new SemaphoreSlim(1, 1));
        await characterLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after waiting: the player may have changed character or disconnected.
            if (!MatchesPlayer(player, request) || player.Inventory is null)
            {
                return this.Conflict(new { error = "character_offline" });
            }

            return await this.DeliverItemsAsync(player, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            characterLock.Release();
        }
    }

    private static bool HasValidEnvelope(LiveGiftRewardRequest request)
    {
        return request.RedemptionId != Guid.Empty
               && request.OpenMuAccountId != Guid.Empty
               && request.CharacterId != Guid.Empty
               && !string.IsNullOrWhiteSpace(request.CharacterName)
               && request.RewardHash.Length == 64
               && request.RewardHash.All(Uri.IsHexDigit)
               && request.Rewards.Count is > 0 and <= 24;
    }

    private static bool HasValidItemRewards(LiveGiftRewardRequest request)
    {
        return request.Rewards.All(reward => reward.Quantity is > 0 and <= MaximumItemCount
                                             && reward.Group is >= 0 and <= 31
                                             && reward.Number is >= 0 and <= 511
                                             && reward.Level is >= 0 and <= 15)
               && request.Rewards.Sum(reward => reward.Quantity) <= MaximumItemCount;
    }

    private static bool MatchesPlayer(Player player, LiveGiftRewardRequest request)
    {
        return player.Account?.GetId() == request.OpenMuAccountId
               && player.SelectedCharacter?.GetId() == request.CharacterId
               && string.Equals(player.Name, request.CharacterName, StringComparison.OrdinalIgnoreCase);
    }

    private static Guid GetRewardItemId(Guid redemptionId, int itemIndex)
    {
        var source = Encoding.UTF8.GetBytes($"mumain-live-gift-v1:{redemptionId:D}:{itemIndex}");
        var hash = SHA256.HashData(source);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // UUID version 5 marker (name-derived).
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(hash.AsSpan(0, 16));
        return Guid.ParseExact($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}", "D");
    }

    private async Task<IActionResult> DeliverItemsAsync(Player player, LiveGiftRewardRequest request, CancellationToken cancellationToken)
    {
        var requestedItems = new List<(Guid Id, LiveGiftRewardLine Reward, DataModel.Configuration.Items.ItemDefinition Definition)>();
        var itemIndex = 0;
        foreach (var reward in request.Rewards)
        {
            var definition = player.GameContext.Configuration.Items
                .FirstOrDefault(item => item.Group == reward.Group && item.Number == reward.Number);
            if (definition is null)
            {
                return this.UnprocessableEntity(new { error = "item_not_found" });
            }

            if (reward.Level > definition.MaximumItemLevel)
            {
                return this.UnprocessableEntity(new { error = "item_level_invalid" });
            }

            for (var quantityIndex = 0; quantityIndex < reward.Quantity; quantityIndex++)
            {
                requestedItems.Add((GetRewardItemId(request.RedemptionId, itemIndex++), reward, definition));
            }
        }

        var existingItems = player.Inventory!.Items
            .Where(item => item is Persistence.IIdentifiable identifiable && requestedItems.Any(expected => expected.Id == identifiable.Id))
            .ToDictionary(item => item.GetId());
        foreach (var expected in requestedItems)
        {
            if (existingItems.TryGetValue(expected.Id, out var existing)
                && (existing.Definition != expected.Definition || existing.Level != expected.Reward.Level))
            {
                return this.Conflict(new { error = "reward_identity_mismatch" });
            }
        }

        if (existingItems.Count == requestedItems.Count)
        {
            return this.Ok(new { ok = true, applied = true, alreadyApplied = true, itemCount = existingItems.Count });
        }

        var addedItems = new List<Item>();
        try
        {
            foreach (var expected in requestedItems.Where(expected => !existingItems.ContainsKey(expected.Id)))
            {
                var temporaryItem = new TemporaryItem
                {
                    Definition = expected.Definition,
                    Durability = expected.Definition.Durability,
                    HasSkill = expected.Definition.Skill is not null,
                    Level = (byte)expected.Reward.Level,
                    SocketCount = 0,
                };
                var slot = player.Inventory.CheckInvSpace(temporaryItem);
                if (slot is null)
                {
                    await RollBackAddedItemsAsync(player, addedItems).ConfigureAwait(false);
                    return this.Conflict(new { error = "inventory_full" });
                }

                var item = temporaryItem.MakePersistent(player.PersistenceContext);
                ((Persistence.IIdentifiable)item).Id = expected.Id;
                if (!await player.Inventory.AddItemAsync(slot.Value, item).ConfigureAwait(false))
                {
                    player.PersistenceContext.Detach(item);
                    await RollBackAddedItemsAsync(player, addedItems).ConfigureAwait(false);
                    return this.Conflict(new { error = "inventory_full" });
                }

                addedItems.Add(item);
            }

            if (!await player.SaveProgressAsync(cancellationToken).ConfigureAwait(false))
            {
                await RollBackAddedItemsAsync(player, addedItems).ConfigureAwait(false);
                return this.StatusCode(503, new { error = "reward_save_failed" });
            }
        }
        catch
        {
            await RollBackAddedItemsAsync(player, addedItems).ConfigureAwait(false);
            throw;
        }

        // These are the normal OpenMU inventory packets; the running client sees every item immediately.
        foreach (var item in addedItems)
        {
            await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(plugIn => plugIn.ItemAppearAsync(item)).ConfigureAwait(false);
        }

        return this.Ok(new
        {
            ok = true,
            applied = true,
            alreadyApplied = false,
            itemCount = requestedItems.Count,
            slots = addedItems.Select(item => item.ItemSlot).ToArray(),
        });
    }

    private static async ValueTask RollBackAddedItemsAsync(Player player, IEnumerable<Item> addedItems)
    {
        foreach (var item in addedItems.Reverse())
        {
            await player.Inventory!.RemoveItemAsync(item).ConfigureAwait(false);
            player.PersistenceContext.Detach(item);
        }
    }

    private async ValueTask<Player?> FindPlayerAsync(LiveGiftRewardRequest request)
    {
        foreach (var server in this._gameServers.Values.OfType<GameServer>())
        {
            var players = await server.Context.GetPlayersAsync().ConfigureAwait(false);
            var player = players.FirstOrDefault(candidate => MatchesPlayer(candidate, request));
            if (player is not null)
            {
                return player;
            }
        }

        return null;
    }

    private bool IsAuthorized(LiveGiftRewardRequest request)
    {
        if (this._apiKey.Length < 32
            || !this.Request.Headers.TryGetValue(TimestampHeader, out var timestampHeader)
            || !this.Request.Headers.TryGetValue(SignatureHeader, out var signatureHeader)
            || signatureHeader.ToString().Length != 64
            || !long.TryParse(timestampHeader.ToString(), out var timestamp))
        {
            return false;
        }

        var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (timestamp < currentTimestamp - MaximumClockSkewSeconds
            || timestamp > currentTimestamp + MaximumClockSkewSeconds)
        {
            return false;
        }

        var canonical = string.Join(
            '\n',
            "v1",
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SignedMethod,
            SignedPath,
            request.RedemptionId.ToString("D").ToLowerInvariant(),
            request.OpenMuAccountId.ToString("D").ToLowerInvariant(),
            request.CharacterId.ToString("D").ToLowerInvariant(),
            request.RewardHash.ToLowerInvariant());
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(this._apiKey));
        var expectedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        try
        {
            var suppliedBytes = Convert.FromHexString(signatureHeader.ToString());
            return suppliedBytes.Length == expectedBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Immutable live-delivery request created by the authenticated web gateway.
/// </summary>
public sealed class LiveGiftRewardRequest
{
    public Guid RedemptionId { get; init; }

    public Guid OpenMuAccountId { get; init; }

    public Guid CharacterId { get; init; }

    public string CharacterName { get; init; } = string.Empty;

    public string RewardHash { get; init; } = string.Empty;

    public IReadOnlyList<LiveGiftRewardLine> Rewards { get; init; } = [];
}

/// <summary>
/// One normalized reward line.
/// </summary>
public sealed class LiveGiftRewardLine
{
    public string Type { get; init; } = string.Empty;

    public int Group { get; init; }

    public int Number { get; init; }

    public int Level { get; init; }

    public int Quantity { get; init; }
}
