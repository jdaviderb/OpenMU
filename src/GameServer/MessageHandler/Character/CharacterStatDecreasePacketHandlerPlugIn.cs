// <copyright file="CharacterStatDecreasePacketHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.Character;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handles the MuMain stat-refund extension (0xF3, 0x07).
/// </summary>
[PlugIn]
[Display(Name = "Character stat decrease", Description = "Refunds one invested stat point without going below the class base value.")]
[Guid("AD4EBA93-CE7A-46BD-86C2-6148BC1606C0")]
[BelongsToGroup(CharacterGroupHandlerPlugIn.GroupKey)]
internal class CharacterStatDecreasePacketHandlerPlugIn : ISubPacketHandlerPlugIn
{
    private const int ResponseLength = 26;
    private readonly DecreaseStatsAction _decreaseStatsAction = new();

    /// <inheritdoc/>
    public bool IsEncryptionExpected => false;

    /// <inheritdoc/>
    public byte Key => 0x07;

    /// <inheritdoc />
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < 5 || player is not RemotePlayer { Connection: { } connection }
                              || player.SelectedCharacter is not { CharacterClass: not null } selectedCharacter
                              || player.Attributes is null)
        {
            return;
        }

        var statType = (CharacterStatAttribute)packet.Span[4];
        if (!System.Enum.IsDefined(typeof(CharacterStatAttribute), statType))
        {
            return;
        }

        this._decreaseStatsAction.TryDecreaseStat(player, statType.GetAttributeDefinition());

        var levelUpPoints = (ushort)Math.Clamp(selectedCharacter.LevelUpPoints, 0, ushort.MaxValue);
        var strength = (uint)player.Attributes[Stats.BaseStrength];
        var agility = (uint)player.Attributes[Stats.BaseAgility];
        var vitality = (uint)player.Attributes[Stats.BaseVitality];
        var energy = (uint)player.Attributes[Stats.BaseEnergy];
        var leadership = (uint)player.Attributes[Stats.BaseLeadership];

        int WriteResponse()
        {
            var response = connection.Output.GetSpan(ResponseLength)[..ResponseLength];
            response.Clear();
            response[0] = 0xC1;
            response[1] = ResponseLength;
            response[2] = 0xF3;
            response[3] = 0x33;
            BinaryPrimitives.WriteUInt16LittleEndian(response[4..6], levelUpPoints);
            BinaryPrimitives.WriteUInt32LittleEndian(response[6..10], strength);
            BinaryPrimitives.WriteUInt32LittleEndian(response[10..14], agility);
            BinaryPrimitives.WriteUInt32LittleEndian(response[14..18], vitality);
            BinaryPrimitives.WriteUInt32LittleEndian(response[18..22], energy);
            BinaryPrimitives.WriteUInt32LittleEndian(response[22..26], leadership);
            return ResponseLength;
        }

        await connection.SendAsync(WriteResponse).ConfigureAwait(false);
    }
}
