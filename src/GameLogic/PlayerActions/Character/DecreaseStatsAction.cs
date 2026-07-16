// <copyright file="DecreaseStatsAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Character;

using MUnique.OpenMU.AttributeSystem;

/// <summary>
/// Refunds stat points which the player previously invested.
/// </summary>
public class DecreaseStatsAction
{
    /// <summary>
    /// Tries to decrease the specified stat by one and refunds the point.
    /// </summary>
    /// <param name="player">The player whose selected character is changed.</param>
    /// <param name="targetAttribute">The base stat to decrease.</param>
    /// <returns><see langword="true"/> when one point was refunded; otherwise, <see langword="false"/>.</returns>
    public bool TryDecreaseStat(Player player, AttributeDefinition targetAttribute)
    {
        if (player.SelectedCharacter is not { CharacterClass: { } characterClass } selectedCharacter
            || player.Attributes is null)
        {
            return false;
        }

        var classStat = characterClass.GetStatAttribute(targetAttribute);
        if (classStat is not { IncreasableByPlayer: true, Attribute: { } statAttribute }
            || player.Attributes[statAttribute] <= classStat.BaseValue)
        {
            return false;
        }

        player.Attributes[statAttribute] -= 1;
        selectedCharacter.LevelUpPoints += 1;
        return true;
    }
}
