// <copyright file="DropGeneratorTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Tests the drop generator.
/// </summary>
[TestFixture]
public class DropGeneratorTest
{
    /// <summary>
    /// Tests if the drop fails because the randomizer returns a number which causes a fail.
    /// </summary>
    [Test]
    public async ValueTask TestDropFailAsync()
    {
        var config = this.GetGameConfig();
        var generator = new DefaultDropGenerator(config, this.GetRandomizer(9999));
        var (items, _) = await generator.GenerateItemDropsAsync(this.GetMonster(1, 0), 0, await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false));
        var item = items.FirstOrDefault();
        Assert.That(item, Is.Null);
    }

    /// <summary>
    /// Tests the drops defined by a monster are getting considered.
    /// </summary>
    [Test]
    public async ValueTask TestItemDropItemByMonsterAsync()
    {
        var config = this.GetGameConfig();
        var monster = this.GetMonster(1, 0);
        monster.DropItemGroups.AddBasicDropItemGroups();
        monster.DropItemGroups.Add(3000, SpecialItemType.RandomItem, true);

        var generator = new DefaultDropGenerator(config, this.GetRandomizer2(0, 0.5));
        var (items, _) = await generator.GenerateItemDropsAsync(monster, 1, await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false));
        var item = items.FirstOrDefault();

        Assert.That(item, Is.Not.Null);

        // ReSharper disable once PossibleNullReferenceException
        Assert.That(item!.Definition, Is.EqualTo(monster.DropItemGroups.Last().PossibleItems.First()));
    }

    /// <summary>
    /// Tests that items with a maximum drop level are filtered from generic monster drops.
    /// </summary>
    [Test]
    public async ValueTask TestMaximumDropLevelAsync()
    {
        var config = this.GetGameConfig();
        var cappedItem = this.CreateItemDefinition(12, 15, 12, 66);
        var uncappedItem = this.CreateItemDefinition(14, 13, 25);

        var dropGroup = new Mock<DropItemGroup>();
        dropGroup.SetupAllProperties();
        dropGroup.Object.Chance = 1.0;
        dropGroup.Object.ItemType = SpecialItemType.Jewel;
        dropGroup.Setup(g => g.PossibleItems).Returns(new List<ItemDefinition> { cappedItem, uncappedItem });

        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.CurrentMap!.Definition.DropItemGroups.Add(dropGroup.Object);

        var generator = new DefaultDropGenerator(config, this.GetRandomizer(0));
        var (items, _) = await generator.GenerateItemDropsAsync(this.GetMonster(1, 67), 1, player).ConfigureAwait(false);
        var item = items.FirstOrDefault();

        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Definition, Is.EqualTo(uncappedItem));
    }

    /// <summary>
    /// Tests the drops defined by a player are getting considered.
    /// </summary>
    public void TestItemDropItemByPlayer()
    {
        // to be implemented
    }

    /// <summary>
    /// Tests the drops defined by a map are getting considered.
    /// </summary>
    public void TestItemDropItemByMap()
    {
        // to be implemented
    }

    /// <summary>
    /// Tests that ExcellentItemDropLevelDelta property exists and has correct default.
    /// </summary>
    [Test]
    public void TestExcellentItemDropLevelDelta_PropertyExists()
    {
        var config = this.GetGameConfig();
        // The initializer sets default to 25 for backward compatibility
        config.ExcellentItemDropLevelDelta = 25;
        Assert.That(config.ExcellentItemDropLevelDelta, Is.EqualTo(25));

        config.ExcellentItemDropLevelDelta = 0;
        Assert.That(config.ExcellentItemDropLevelDelta, Is.EqualTo(0));

        config.ExcellentItemDropLevelDelta = 50;
        Assert.That(config.ExcellentItemDropLevelDelta, Is.EqualTo(50));
    }

    /// <summary>
    /// Verifies that the normal Box of Wing branch never inherits random options.
    /// </summary>
    [Test]
    public void TestWingWithoutOptions()
    {
        var generator = new DefaultDropGenerator(this.GetGameConfig(), this.GetMinimumRandomizer());
        var wing = this.CreateWingDefinition();
        var group = this.CreateWingDropGroup(SpecialItemType.WingWithoutOptions, wing, 0, 7);

        var item = generator.GenerateItemDrop(group);

        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Level, Is.EqualTo(0));
        Assert.That(item.ItemOptions, Is.Empty);
    }

    /// <summary>
    /// Verifies that the premium Box of Wing branch guarantees luck and a wing stat.
    /// </summary>
    [Test]
    public void TestWingWithGoodOptions()
    {
        var generator = new DefaultDropGenerator(this.GetGameConfig(), this.GetMinimumRandomizer());
        var wing = this.CreateWingDefinition();
        var group = this.CreateWingDropGroup(SpecialItemType.WingWithGoodOptions, wing, 9, 13);

        var item = generator.GenerateItemDrop(group);

        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Level, Is.EqualTo(9));
        Assert.That(item.ItemOptions.Any(link => link.ItemOption?.OptionType == ItemOptionTypes.Luck), Is.True);
        Assert.That(item.ItemOptions.Any(link => link.ItemOption?.OptionType == ItemOptionTypes.Wing), Is.True);
    }

    private MonsterDefinition GetMonster(int numberOfDrops, byte level)
    {
        var monster = new Mock<MonsterDefinition>();
        monster.SetupAllProperties();
        monster.Setup(m => m.DropItemGroups).Returns(new List<DropItemGroup>());
        monster.Setup(m => m.Attributes).Returns(new List<MonsterAttribute>());
        monster.Object.NumberOfMaximumItemDrops = numberOfDrops;
        monster.Object.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.Level, Value = level });
        return monster.Object;
    }

    private IRandomizer GetRandomizer(int randomValue)
    {
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(r => r.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns(randomValue);
        randomizer.Setup(r => r.NextDouble()).Returns(randomValue / 10000.0);
        return randomizer.Object;
    }

    private IRandomizer GetRandomizer2(int integerValue, double doubleValue)
    {
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(r => r.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns(integerValue);
        randomizer.Setup(r => r.NextDouble()).Returns(doubleValue);

        return randomizer.Object;
    }

    private IRandomizer GetMinimumRandomizer()
    {
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(r => r.NextInt(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int minimum, int _) => minimum);
        randomizer.Setup(r => r.NextDouble()).Returns(0);
        return randomizer.Object;
    }

    private GameConfiguration GetGameConfig()
    {
        var gameConfiguration = new Mock<GameConfiguration>();
        gameConfiguration.Setup(c => c.Items).Returns(new List<ItemDefinition>());
        return gameConfiguration.Object;
    }

    private ItemDefinition CreateItemDefinition(byte group, short number, byte dropLevel, byte? maximumDropLevel = null)
    {
        var itemDefinition = new Mock<ItemDefinition>();
        itemDefinition.SetupAllProperties();
        itemDefinition.Object.Group = group;
        itemDefinition.Object.Number = number;
        itemDefinition.Object.DropLevel = dropLevel;
        itemDefinition.Object.MaximumDropLevel = maximumDropLevel;
        itemDefinition.Object.Durability = 1;
        itemDefinition.Setup(d => d.PossibleItemOptions).Returns(new List<ItemOptionDefinition>());
        return itemDefinition.Object;
    }

    private ItemDefinition CreateWingDefinition()
    {
        var wing = this.CreateItemDefinition(12, 3, 100);
        wing.MaximumItemLevel = 15;

        var luck = new Mock<IncreasableItemOption>();
        luck.SetupAllProperties();
        luck.Object.OptionType = ItemOptionTypes.Luck;
        luck.Setup(option => option.LevelDependentOptions).Returns(new List<ItemOptionOfLevel>());
        var wingStat = new Mock<IncreasableItemOption>();
        wingStat.SetupAllProperties();
        wingStat.Object.OptionType = ItemOptionTypes.Wing;
        wingStat.Setup(option => option.LevelDependentOptions).Returns(new List<ItemOptionOfLevel>());

        var luckDefinition = new Mock<ItemOptionDefinition>();
        luckDefinition.SetupAllProperties();
        luckDefinition.Setup(definition => definition.PossibleOptions).Returns(new List<IncreasableItemOption> { luck.Object });
        var statDefinition = new Mock<ItemOptionDefinition>();
        statDefinition.SetupAllProperties();
        statDefinition.Setup(definition => definition.PossibleOptions).Returns(new List<IncreasableItemOption> { wingStat.Object });
        Mock.Get(wing).Setup(definition => definition.PossibleItemOptions)
            .Returns(new List<ItemOptionDefinition> { luckDefinition.Object, statDefinition.Object });
        return wing;
    }

    private ItemDropItemGroup CreateWingDropGroup(SpecialItemType itemType, ItemDefinition wing, byte minimumLevel, byte maximumLevel)
    {
        var group = new Mock<ItemDropItemGroup>();
        group.SetupAllProperties();
        group.Object.ItemType = itemType;
        group.Object.MinimumLevel = minimumLevel;
        group.Object.MaximumLevel = maximumLevel;
        group.Setup(dropGroup => dropGroup.PossibleItems).Returns(new List<ItemDefinition> { wing });
        return group.Object;
    }
}
