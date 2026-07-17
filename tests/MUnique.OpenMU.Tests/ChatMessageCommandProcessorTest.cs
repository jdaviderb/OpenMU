// <copyright file="ChatMessageCommandProcessorTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.PlayerActions.Chat;

/// <summary>
/// Tests for the multilingual stat command aliases.
/// </summary>
[TestFixture]
public class ChatMessageCommandProcessorTest
{
    /// <summary>
    /// Verifies that supported aliases use the standard add-stat command and preserve the amount.
    /// </summary>
    /// <param name="alias">The player-facing alias.</param>
    /// <param name="stat">The canonical stat name.</param>
    [TestCase("/str", "str")]
    [TestCase("/fuerza", "str")]
    [TestCase("/força", "str")]
    [TestCase("/agi", "agi")]
    [TestCase("/agilidad", "agi")]
    [TestCase("/agilidade", "agi")]
    [TestCase("/vit", "vit")]
    [TestCase("/vitalidad", "vit")]
    [TestCase("/vida", "vit")]
    [TestCase("/ene", "ene")]
    [TestCase("/eng", "ene")]
    [TestCase("/energia", "ene")]
    [TestCase("/cmd", "cmd")]
    [TestCase("/liderazgo", "cmd")]
    [TestCase("/liderança", "cmd")]
    public void StatAliasesAreNormalized(string alias, string stat)
    {
        Assert.That(ChatMessageCommandProcessor.NormalizeCommand($"{alias} 250"), Is.EqualTo($"/add {stat} 250"));
    }

    /// <summary>
    /// Verifies that unrelated commands, including reset, are not rewritten as stat commands.
    /// </summary>
    [Test]
    public void UnrelatedCommandsAreUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChatMessageCommandProcessor.NormalizeCommand("/reset"), Is.EqualTo("/reset"));
            Assert.That(ChatMessageCommandProcessor.NormalizeCommand("/post hello"), Is.EqualTo("/post hello"));
        });
    }
}
