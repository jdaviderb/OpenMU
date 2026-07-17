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
using MUnique.OpenMU.DataModel.Configuration.Items;
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
    private const string GiftSignedPath = "/api/internal/live-rewards/gift-code";
    private const string ShopCheckSignedPath = "/api/internal/live-rewards/shop-purchase/check";
    private const string ShopDeliverSignedPath = "/api/internal/live-rewards/shop-purchase";
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
        if (!this.IsAuthorized(request, "v1", GiftSignedPath, bindRewardLines: false))
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

            return await this.DeliverItemsAsync(player, request, "mumain-live-gift-v1", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            characterLock.Release();
        }
    }

    /// <summary>
    /// Checks that a selected online character can receive the one item frozen in a shop purchase.
    /// This never reserves a slot; the delivery endpoint re-checks after payment and remains idempotent.
    /// </summary>
    [HttpPost("shop-purchase/check")]
    public async Task<IActionResult> CheckShopPurchaseAsync([FromBody] LiveGiftRewardRequest request, CancellationToken cancellationToken)
    {
        if (!this.IsAuthorized(request, "v2", ShopCheckSignedPath, bindRewardLines: true))
        {
            return this.Unauthorized(new { error = "unauthorized" });
        }

        if (!HasValidShopRequest(request))
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
            if (!MatchesPlayer(player, request) || player.Inventory is null)
            {
                return this.Conflict(new { error = "character_offline" });
            }

            var reward = request.Rewards[0];
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

            var temporaryItem = new TemporaryItem
            {
                Definition = definition,
                Durability = definition.Durability,
                HasSkill = definition.Skill is not null,
                Level = (byte)reward.Level,
                SocketCount = 0,
            };
            return player.Inventory.CheckInvSpace(temporaryItem) is null
                ? this.Conflict(new { error = "inventory_full" })
                : this.Ok(new { ok = true, inventorySpace = true });
        }
        finally
        {
            characterLock.Release();
        }
    }

    /// <summary>
    /// Delivers a paid shop item. The purchase UUID deterministically identifies the physical item.
    /// </summary>
    [HttpPost("shop-purchase")]
    public async Task<IActionResult> DeliverShopPurchaseAsync([FromBody] LiveGiftRewardRequest request, CancellationToken cancellationToken)
    {
        if (!this.IsAuthorized(request, "v2", ShopDeliverSignedPath, bindRewardLines: true))
        {
            return this.Unauthorized(new { error = "unauthorized" });
        }

        if (!HasValidShopRequest(request))
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
            if (!MatchesPlayer(player, request) || player.Inventory is null)
            {
                return this.Conflict(new { error = "character_offline" });
            }

            return await this.DeliverItemsAsync(player, request, "mumain-live-shop-v1", cancellationToken).ConfigureAwait(false);
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
               && request.RewardHash is { Length: 64 }
               && request.RewardHash.All(Uri.IsHexDigit)
               && request.Rewards is { Count: > 0 and <= 24 };
    }

    private static bool HasValidItemRewards(LiveGiftRewardRequest request)
    {
        return request.Rewards.All(reward => reward.Quantity is > 0 and <= MaximumItemCount
                                             && reward.Group is >= 0 and <= 31
                                             && reward.Number is >= 0 and <= 511
                                             && reward.Level is >= 0 and <= 15
                                             && IsValidVariant(reward))
               && request.Rewards.Sum(reward => reward.Quantity) <= MaximumItemCount;
    }

    private static bool HasValidShopRequest(LiveGiftRewardRequest request)
    {
        return HasValidEnvelope(request)
               && request.Rewards.Count == 1
               && string.Equals(request.Rewards[0].Type, "item", StringComparison.OrdinalIgnoreCase)
               && request.Rewards[0].Quantity == 1
               && HasValidItemRewards(request);
    }

    private static bool IsValidVariant(LiveGiftRewardLine reward)
    {
        return string.IsNullOrWhiteSpace(reward.Variant)
               || (reward.Group == 13
                   && reward.Number == 37
                   && GetVariantOptionType(reward.Variant) is not null);
    }

    private static ItemOptionType? GetVariantOptionType(string? variant)
    {
        return variant?.Trim().ToLowerInvariant() switch
        {
            "fenrir-black" => ItemOptionTypes.BlackFenrir,
            "fenrir-blue" => ItemOptionTypes.BlueFenrir,
            "fenrir-gold" => ItemOptionTypes.GoldFenrir,
            _ => null,
        };
    }

    private static bool MatchesPlayer(Player player, LiveGiftRewardRequest request)
    {
        return player.Account?.GetId() == request.OpenMuAccountId
               && player.SelectedCharacter?.GetId() == request.CharacterId
               && string.Equals(player.Name, request.CharacterName, StringComparison.OrdinalIgnoreCase);
    }

    private static Guid GetRewardItemId(string domain, Guid redemptionId, int itemIndex)
    {
        var source = Encoding.UTF8.GetBytes($"{domain}:{redemptionId:D}:{itemIndex}");
        var hash = SHA256.HashData(source);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // UUID version 5 marker (name-derived).
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(hash.AsSpan(0, 16));
        return Guid.ParseExact($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}", "D");
    }

    private async Task<IActionResult> DeliverItemsAsync(Player player, LiveGiftRewardRequest request, string itemIdDomain, CancellationToken cancellationToken)
    {
        var inventory = player.Inventory!;
        var requestedItems = new List<(Guid Id, LiveGiftRewardLine Reward, ItemDefinition Definition, IncreasableItemOption? VariantOption)>();
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

            IncreasableItemOption? variantOption = null;
            if (GetVariantOptionType(reward.Variant) is { } optionType)
            {
                variantOption = definition.PossibleItemOptions
                    .SelectMany(option => option.PossibleOptions)
                    .FirstOrDefault(option => option.OptionType == optionType);
                if (variantOption is null)
                {
                    return this.UnprocessableEntity(new { error = "item_variant_not_found" });
                }
            }

            for (var quantityIndex = 0; quantityIndex < reward.Quantity; quantityIndex++)
            {
                requestedItems.Add((GetRewardItemId(itemIdDomain, request.RedemptionId, itemIndex++), reward, definition, variantOption));
            }
        }

        // Resolve deterministic reward IDs globally through persistence, not only in the
        // current inventory. Moving a delivered item to vault/trade before an HTTP retry
        // must still count as already applied and can never mint a duplicate.
        var existingItems = new Dictionary<Guid, Item>();
        foreach (var expected in requestedItems)
        {
            if (await player.PersistenceContext.GetByIdAsync<Item>(expected.Id, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                existingItems[expected.Id] = existing;
            }
        }

        foreach (var expected in requestedItems)
        {
            if (existingItems.TryGetValue(expected.Id, out var existing)
                && (existing.Definition is null
                    || existing.Definition.Group != expected.Definition.Group
                    || existing.Definition.Number != expected.Definition.Number
                    || existing.Level != expected.Reward.Level
                    || (expected.VariantOption is not null
                        && !existing.ItemOptions.Any(link => link.ItemOption == expected.VariantOption))))
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
                var slot = inventory.CheckInvSpace(temporaryItem);
                if (slot is null)
                {
                    await RollBackAddedItemsAsync(player, addedItems).ConfigureAwait(false);
                    return this.Conflict(new { error = "inventory_full" });
                }

                var item = temporaryItem.MakePersistent(player.PersistenceContext);
                ((Persistence.IIdentifiable)item).Id = expected.Id;
                if (expected.VariantOption is not null)
                {
                    var optionLink = player.PersistenceContext.CreateNew<ItemOptionLink>();
                    optionLink.ItemOption = expected.VariantOption;
                    item.ItemOptions.Add(optionLink);
                }

                if (!await inventory.AddItemAsync(slot.Value, item).ConfigureAwait(false))
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

    private bool IsAuthorized(LiveGiftRewardRequest request, string version, string signedPath, bool bindRewardLines)
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

        var canonicalLines = new List<string>
        {
            version,
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SignedMethod,
            signedPath,
            request.RedemptionId.ToString("D").ToLowerInvariant(),
            request.OpenMuAccountId.ToString("D").ToLowerInvariant(),
            request.CharacterId.ToString("D").ToLowerInvariant(),
            request.RewardHash?.ToLowerInvariant() ?? string.Empty,
        };
        if (bindRewardLines)
        {
            canonicalLines.Add(GetLiveRewardBinding(request.Rewards));
        }

        var variants = request.Rewards
            .Select(reward => reward.Variant?.Trim().ToLowerInvariant() ?? string.Empty)
            .ToArray();
        if (variants.Any(variant => variant.Length > 0))
        {
            // Optional for rolling compatibility with existing rewards, mandatory whenever a variant is present.
            canonicalLines.Add($"variants:{string.Join(';', variants)}");
        }

        var canonical = string.Join('\n', canonicalLines);
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

    private static string GetLiveRewardBinding(IReadOnlyList<LiveGiftRewardLine>? rewards)
    {
        var lines = string.Join(
            ';',
            (rewards ?? []).Select(reward => string.Join(
                ':',
                reward.Type?.ToLowerInvariant() ?? string.Empty,
                reward.Group.ToString(System.Globalization.CultureInfo.InvariantCulture),
                reward.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                reward.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                reward.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"mumain-live-reward-v2\n{lines}")))
            .ToLowerInvariant();
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

    public string? Variant { get; init; }
}
