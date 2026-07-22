// <copyright file="IncreaseStatsAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Character;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Action to increase stat attributes.
/// </summary>
public class IncreaseStatsAction
{
    /// <summary>
    /// Increases the specified stat attribute by one point, if enough points are available.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="targetAttribute">The stat attribute definition.</param>
    /// <param name="amount">The amount of points.</param>
    public async ValueTask IncreaseStatsAsync(Player player, AttributeDefinition targetAttribute, ushort amount = 1)
    {
        if (player.SelectedCharacter is not { } selectedCharacter)
        {
            throw new InvalidOperationException("No character selected");
        }

        if (amount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount must be greater than 0.");
        }

        if (!selectedCharacter.CanIncreaseStats(amount))
        {
            await player.ShowLocalizedBlueMessageAsync(PlayerMessage.NotEnoughLevelUpPointsAvailable).ConfigureAwait(false);
            return;
        }

        var attributeDef = selectedCharacter.CharacterClass?.GetStatAttribute(targetAttribute);
        if (attributeDef is { IncreasableByPlayer: true })
        {
            var maximumValue = Math.Min(
                attributeDef.Attribute?.MaximumValue ?? Stats.MaximumBaseStatValue,
                Stats.MaximumBaseStatValue);
            if (player.Attributes![attributeDef.Attribute] is { } current)
            {
                if (current >= maximumValue)
                {
                    await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.MaximumAttributeValueReachedFormat), maximumValue, new LocalizedString(attributeDef.Attribute?.Designation).GetTranslation(player.Culture)).ConfigureAwait(false);
                    return;
                }

                if (current + amount > maximumValue)
                {
                    amount = (ushort)(maximumValue - current);
                }
            }

            player.Attributes![attributeDef.Attribute] += amount;
            selectedCharacter.LevelUpPoints -= Math.Min(selectedCharacter.LevelUpPoints, amount);

            await player.InvokeViewPlugInAsync<IStatIncreaseResultPlugIn>(p => p.StatIncreaseResultAsync(targetAttribute, amount)).ConfigureAwait(false);
        }
        else
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.AttributeNotAvailable)).ConfigureAwait(false);
        }
    }
}
