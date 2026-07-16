// <copyright file="DecreaseStatsActionTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;

/// <summary>
/// Tests for <see cref="DecreaseStatsAction"/>.
/// </summary>
[TestFixture]
public class DecreaseStatsActionTest
{
    /// <summary>
    /// Verifies that repeated requests stop exactly at the class base stat and only invested points are refunded.
    /// </summary>
    [Test]
    public async Task RepeatedRequestsCannotDecreaseBelowClassBaseAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var action = new DecreaseStatsAction();
        var baseStrength = player.SelectedCharacter!.CharacterClass!.GetStatAttribute(Stats.BaseStrength)!.BaseValue;
        player.Attributes![Stats.BaseStrength] = baseStrength + 3;
        player.SelectedCharacter.LevelUpPoints = 7;

        var results = Enumerable.Range(0, 6)
            .Select(_ => action.TryDecreaseStat(player, Stats.BaseStrength))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results.Count(result => result), Is.EqualTo(3));
            Assert.That(player.Attributes[Stats.BaseStrength], Is.EqualTo(baseStrength));
            Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(10));
        });
    }

    /// <summary>
    /// Verifies that attributes which players cannot increase cannot be converted into free points.
    /// </summary>
    [Test]
    public async Task NonPlayerStatCannotBeRefundedAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var action = new DecreaseStatsAction();
        var initialLevel = player.Attributes![Stats.Level];

        var result = action.TryDecreaseStat(player, Stats.Level);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False);
            Assert.That(player.Attributes[Stats.Level], Is.EqualTo(initialLevel));
            Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.Zero);
        });
    }
}
