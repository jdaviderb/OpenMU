// <copyright file="ChatMessageCommandProcessor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Chat;

using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

/// <summary>
/// A chat message processor which handles chat commands.
/// </summary>
public class ChatMessageCommandProcessor : IChatMessageProcessor
{
    private static readonly IReadOnlyDictionary<string, string> StatCommandAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/str"] = "str",
            ["/fue"] = "str",
            ["/strength"] = "str",
            ["/fuerza"] = "str",
            ["/forca"] = "str",
            ["/força"] = "str",
            ["/agi"] = "agi",
            ["/dex"] = "agi",
            ["/agility"] = "agi",
            ["/agilidad"] = "agi",
            ["/agilidade"] = "agi",
            ["/destreza"] = "agi",
            ["/vit"] = "vit",
            ["/sta"] = "vit",
            ["/vitality"] = "vit",
            ["/vitalidad"] = "vit",
            ["/vitalidade"] = "vit",
            ["/vida"] = "vit",
            ["/ene"] = "ene",
            ["/eng"] = "ene",
            ["/int"] = "ene",
            ["/energy"] = "ene",
            ["/energia"] = "ene",
            ["/cmd"] = "cmd",
            ["/command"] = "cmd",
            ["/comando"] = "cmd",
            ["/liderazgo"] = "cmd",
            ["/lideranca"] = "cmd",
            ["/liderança"] = "cmd",
        };

    /// <summary>
    /// Normalizes short English, Spanish and Portuguese stat aliases to the standard add-stat command.
    /// </summary>
    /// <param name="command">The original chat command.</param>
    /// <returns>The normalized command, or the original command when it is not a stat alias.</returns>
    public static string NormalizeCommand(string command)
    {
        var trimmed = command.Trim();
        var separator = trimmed.IndexOf(' ');
        var commandKey = separator < 0 ? trimmed : trimmed[..separator];
        if (!StatCommandAliases.TryGetValue(commandKey, out var stat))
        {
            return command;
        }

        var amount = separator < 0 ? string.Empty : trimmed[(separator + 1)..].TrimStart();
        return amount.Length == 0 ? $"/add {stat}" : $"/add {stat} {amount}";
    }

    /// <inheritdoc />
    public async ValueTask ProcessMessageAsync(Player sender, (string Message, string PlayerName) content)
    {
        var command = NormalizeCommand(content.Message);
        var commandKey = command.Split(' ').First();
        var commandHandler = sender.GameContext.PlugInManager.GetStrategy<IChatCommandPlugIn>(commandKey);
        if (commandHandler is null)
        {
            return;
        }

        if (sender.SelectedCharacter!.CharacterStatus < commandHandler.MinCharacterStatusRequirement)
        {
            sender.Logger.LogWarning($"{sender.Name} is trying to execute {commandKey} command without meeting the requirements");
            return;
        }

        await commandHandler.HandleCommandAsync(sender, command).ConfigureAwait(false);
    }
}
