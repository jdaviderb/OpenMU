// <copyright file="AddAllClassSkillsForNewCharactersPlugInTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Persistence.Initialization.PlugIns.CharacterCreated;

/// <summary>
/// Tests the automatic class skill assignment for newly created characters.
/// </summary>
[TestFixture]
public class AddAllClassSkillsForNewCharactersPlugInTest
{
    /// <summary>
    /// Verifies final evolution, matching non-master skill assignment and duplicate protection.
    /// </summary>
    [Test]
    public async Task AssignsMatchingNonMasterSkillsWithoutDuplicatesAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var baseClass = new Mock<CharacterClass>();
        baseClass.SetupAllProperties();
        var secondClass = new Mock<CharacterClass>();
        secondClass.SetupAllProperties();
        var finalClass = new Mock<CharacterClass>();
        finalClass.SetupAllProperties();
        baseClass.Object.NextGenerationClass = secondClass.Object;
        secondClass.Object.NextGenerationClass = finalClass.Object;
        var otherClass = new Mock<CharacterClass>().Object;

        var matchingSkill = CreateSkill(1, finalClass.Object);
        var secondMatchingSkill = CreateSkill(2, finalClass.Object);
        var otherClassSkill = CreateSkill(3, otherClass);
        var masterSkill = CreateSkill(4, finalClass.Object, new Mock<MasterSkillDefinition>().Object);
        var eventOnlySkill = CreateSkill(213, finalClass.Object);
        player.GameContext.Configuration.Skills.Add(matchingSkill);
        player.GameContext.Configuration.Skills.Add(secondMatchingSkill);
        player.GameContext.Configuration.Skills.Add(otherClassSkill);
        player.GameContext.Configuration.Skills.Add(masterSkill);
        player.GameContext.Configuration.Skills.Add(eventOnlySkill);
        player.GameContext.Configuration.Items.Add(CreateLearnableItem(matchingSkill));
        player.GameContext.Configuration.Items.Add(CreateLearnableItem(secondMatchingSkill));
        player.GameContext.Configuration.Items.Add(CreateLearnableItem(otherClassSkill));
        player.GameContext.Configuration.Items.Add(CreateLearnableItem(masterSkill));

        var character = new Mock<Character>();
        character.SetupAllProperties();
        character.Setup(c => c.LearnedSkills).Returns(new List<SkillEntry>
        {
            new() { Skill = matchingSkill },
        });
        character.Object.CharacterClass = baseClass.Object;

        var plugIn = new AddAllClassSkillsForNewCharactersPlugIn();
        plugIn.CharacterCreated(player, character.Object);
        plugIn.CharacterCreated(player, character.Object);

        Assert.That(character.Object.CharacterClass, Is.SameAs(finalClass.Object));
        Assert.That(character.Object.LearnedSkills.Select(entry => entry.Skill?.Number), Is.EquivalentTo(new short[] { 1, 2 }));
    }

    private static Skill CreateSkill(short number, CharacterClass characterClass, MasterSkillDefinition? masterDefinition = null)
    {
        var skill = new Mock<Skill>();
        skill.SetupAllProperties();
        skill.Setup(s => s.QualifiedCharacters).Returns(new List<CharacterClass> { characterClass });
        skill.Object.Number = number;
        skill.Object.MasterDefinition = masterDefinition;
        return skill.Object;
    }

    private static ItemDefinition CreateLearnableItem(Skill skill)
    {
        var item = new Mock<ItemDefinition>();
        item.SetupAllProperties();
        item.Object.Skill = skill;
        item.Object.Group = 15;
        return item.Object;
    }
}
