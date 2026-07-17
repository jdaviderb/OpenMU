// <copyright file="ExperienceMath.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

/// <summary>
/// Safe conversions for experience calculations on servers with high rate multipliers.
/// </summary>
internal static class ExperienceMath
{
    /// <summary>
    /// Converts a calculated experience value to the packet/domain representation without allowing
    /// values above <see cref="int.MaxValue"/> to wrap into a negative number.
    /// </summary>
    /// <param name="experience">The calculated experience.</param>
    /// <returns>A non-negative, saturated 32-bit experience value.</returns>
    internal static int SaturateToInt32(double experience)
    {
        if (double.IsNaN(experience) || experience <= 0)
        {
            return 0;
        }

        return experience >= int.MaxValue ? int.MaxValue : (int)experience;
    }
}
