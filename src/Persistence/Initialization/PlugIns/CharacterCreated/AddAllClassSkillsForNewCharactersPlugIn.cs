// <copyright file="AddAllClassSkillsForNewCharactersPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.PlugIns.CharacterCreated;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Promotes a newly created character to its final evolution and assigns every learnable non-master skill.
/// </summary>
/// <remarks>
/// Skill requirements still apply when the character tries to use a skill. Master skills are
/// deliberately excluded because they are learned through the master skill tree.
/// </remarks>
[Guid("16F89612-A63A-4A51-81A7-F3C30223D6AB")]
[PlugIn]
[Display(
    Name = "Final evolution and all class skills for new characters",
    Description = "Promotes new characters to their final class and assigns all supported scroll/orb skills.")]
public class AddAllClassSkillsForNewCharactersPlugIn : ICharacterCreatedPlugIn
{
    /// <inheritdoc />
    public void CharacterCreated(Player player, Character createdCharacter)
    {
        using var logScope = player.Logger.BeginScope(this.GetType());
        if (createdCharacter.CharacterClass is not { } originalClass)
        {
            player.Logger.LogError("The new character has no character class, so skills cannot be assigned.");
            return;
        }

        var characterClass = GetFinalEvolution(originalClass, player.Logger);
        createdCharacter.CharacterClass = characterClass;

        var learnedSkillNumbers = createdCharacter.LearnedSkills
            .Where(entry => entry.Skill is not null)
            .Select(entry => entry.Skill!.Number)
            .ToHashSet();

        var learnableSkillNumbers = GetLearnableSkillNumbers(player.GameContext.Configuration.Items);

        var skillsToLearn = player.GameContext.Configuration.Skills
            .Where(skill => skill.MasterDefinition is null
                            && learnableSkillNumbers.Contains(skill.Number)
                            && skill.QualifiedCharacters.Contains(characterClass)
                            && learnedSkillNumbers.Add(skill.Number))
            .OrderBy(skill => skill.Number)
            .ToList();

        foreach (var skill in skillsToLearn)
        {
            var skillEntry = player.PersistenceContext.CreateNew<SkillEntry>();
            skillEntry.Skill = skill;
            createdCharacter.LearnedSkills.Add(skillEntry);
        }

        player.Logger.LogInformation(
            "Promoted new character {CharacterName} from {OriginalClass} to {CharacterClass} and assigned {SkillCount} class skills.",
            createdCharacter.Name,
            originalClass.Name,
            characterClass.Name,
            skillsToLearn.Count);
    }

    private static CharacterClass GetFinalEvolution(CharacterClass originalClass, ILogger logger)
    {
        var visitedClasses = new HashSet<CharacterClass>();
        var characterClass = originalClass;
        while (characterClass.NextGenerationClass is { } nextClass)
        {
            if (!visitedClasses.Add(characterClass))
            {
                logger.LogError("Detected a cycle in the evolution chain of character class {CharacterClass}.", originalClass.Name);
                break;
            }

            characterClass = nextClass;
        }

        return characterClass;
    }

    private static HashSet<short> GetLearnableSkillNumbers(IEnumerable<ItemDefinition> itemDefinitions)
    {
        var result = new HashSet<short>();
        foreach (var itemDefinition in itemDefinitions.Where(item => item.Skill is not null && item.ItemSlot is null))
        {
            var baseSkillNumber = itemDefinition.Skill!.Number;
            result.Add(baseSkillNumber);

            // The Orb of Summoning uses its item level to select one of seven consecutive summon skills.
            if (itemDefinition is { Group: 12, Number: 11 })
            {
                for (short level = 1; level <= itemDefinition.MaximumItemLevel; level++)
                {
                    result.Add((short)(baseSkillNumber + level));
                }
            }
        }

        return result;
    }
}
